using System.Net;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventTracking.Api.Tests;

public sealed class TelemetryTests : IClassFixture<PrototypeFactory>
{
    private readonly PrototypeFactory _factory;

    public TelemetryTests(PrototypeFactory factory) => _factory = factory;

    [Fact]
    public void Metrics_IncrementAndGenerateValidPrometheusFormat()
    {
        string testProject = "telemetry-test-project-" + Guid.NewGuid().ToString("N");
        EventTrackingTelemetry.RecordAccepted(testProject, 5, 12.5);
        EventTrackingTelemetry.RecordProjected(testProject, 5, 8.2);
        EventTrackingTelemetry.RecordRejected(testProject, 2, "quota");
        EventTrackingTelemetry.RecordDeadLettered(testProject, 1, "poison_message");

        string metrics = EventTrackingTelemetry.GeneratePrometheusMetrics();

        Assert.Contains("# HELP events_accepted_total", metrics);
        Assert.Contains("# TYPE events_accepted_total counter", metrics);
        Assert.Contains($"events_accepted_total{{project_id=\"{testProject}\"}} 5", metrics);

        Assert.Contains("# HELP events_projected_total", metrics);
        Assert.Contains($"events_projected_total{{project_id=\"{testProject}\"}} 5", metrics);

        Assert.Contains("# HELP events_rejected_total", metrics);
        Assert.Contains("events_rejected_total{reason=\"quota\"}", metrics);

        Assert.Contains("# HELP events_dead_lettered_total", metrics);
        Assert.Contains($"events_dead_lettered_total{{project_id=\"{testProject}\"}} 1", metrics);
    }

    [Fact]
    public async Task MetricsEndpoint_ReturnsPrometheusTextFormat()
    {
        using var app = _factory.WithWebHostBuilder(TestProjects.Configure);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Contains("text/plain", response.Content.Headers.ContentType.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("events_accepted_total", body);
        Assert.Contains("events_projected_total", body);
    }

    [Fact]
    public void Gauges_ReportOutboxAndInboxDepth()
    {
        EventTrackingTelemetry.SetOutboxGauge(7, 12.5);
        EventTrackingTelemetry.SetInboxGauge(3);
        string metrics = EventTrackingTelemetry.GeneratePrometheusMetrics();
        Assert.Contains("outbox_pending 7", metrics);
        Assert.Contains("inbox_pending 3", metrics);
        Assert.Contains("outbox_oldest_seconds", metrics);
    }

    [Fact]
    public void Tracing_ActivitySource_EmitsAcceptSpan()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == EventTrackingTelemetry.MeterName,
            Sample = (
                ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _
            ) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var activity = EventTrackingTelemetry.Activity.StartActivity("test.accept");
        Assert.NotNull(activity);
        Assert.Equal(EventTrackingTelemetry.MeterName, activity.Source.Name);
    }
}
