using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace EventTracking.Persistence;

public static class EventTrackingTelemetry
{
    public const string MeterName = "EventTracking";
    public const string MeterVersion = "1.0.0";
    public static readonly ActivitySource Activity = new(MeterName, MeterVersion);

    public static readonly Meter Meter = new(MeterName, MeterVersion);

    public static readonly Counter<long> EventsAccepted =
        Meter.CreateCounter<long>("events.accepted", "events", "Total number of events accepted into the system.");

    public static readonly Counter<long> EventsProjected =
        Meter.CreateCounter<long>("events.projected", "events", "Total number of events successfully projected.");

    public static readonly Counter<long> EventsRejected =
        Meter.CreateCounter<long>("events.rejected", "events", "Total number of events rejected (conflict, quota, validation).");

    public static readonly Counter<long> EventsDeadLettered =
        Meter.CreateCounter<long>("events.dead_lettered", "events", "Total number of events moved to dead-letter storage.");

    public static readonly Histogram<double> IngestionLatencyMs =
        Meter.CreateHistogram<double>("ingestion.latency_ms", "ms", "Latency of event ingestion acceptance in milliseconds.");

    public static readonly Histogram<double> ProjectionLatencyMs =
        Meter.CreateHistogram<double>("projection.latency_ms", "ms", "Latency of projection batch processing in milliseconds.");

    public static readonly Histogram<int> ProjectionBatchSize =
        Meter.CreateHistogram<int>("projection.batch_size", "events", "Number of events in projection batches.");

    private static long OutboxPendingCache;
    private static double OutboxOldestSecondsCache;
    private static long InboxPendingCache;

    static EventTrackingTelemetry()
    {
        Meter.CreateObservableGauge("outbox.pending", () => Interlocked.Read(ref OutboxPendingCache),
            "events", "Current unpublished broker outbox depth.");
        Meter.CreateObservableGauge("outbox.oldest_seconds", () => Volatile.Read(ref OutboxOldestSecondsCache),
            "s", "Age of the oldest pending outbox message in seconds.");
        Meter.CreateObservableGauge("inbox.pending", () => Interlocked.Read(ref InboxPendingCache),
            "events", "Current unprojected inbox depth.");
    }

    public static void SetOutboxGauge(long pending, double? oldestSeconds)
    {
        Interlocked.Exchange(ref OutboxPendingCache, pending);
        Volatile.Write(ref OutboxOldestSecondsCache, oldestSeconds ?? 0);
    }

    public static void SetInboxGauge(long pending) => Interlocked.Exchange(ref InboxPendingCache, pending);

    // In-memory aggregates for the Prometheus /metrics scrape endpoint.
    // Keyed dimensions are bounded: beyond MaxSeries keys, counts roll into "_other"
    // so a high-cardinality label can neither OOM the process nor bloat scrapes.
    private const int MaxSeries = 1000;
    private const string OverflowKey = "_other";
    private static readonly ConcurrentDictionary<string, long> AcceptedByProject = new();
    private static readonly ConcurrentDictionary<string, long> ProjectedByProject = new();
    private static readonly ConcurrentDictionary<string, long> RejectedByReason = new();
    private static readonly ConcurrentDictionary<string, long> DeadLetteredByProject = new();

    private static string SeriesKey(ConcurrentDictionary<string, long> series, string key) =>
        series.ContainsKey(key) || series.Count < MaxSeries ? key : OverflowKey;
    private static long TotalAccepted;
    private static long TotalProjected;
    private static long TotalRejected;
    private static long TotalDeadLettered;

    public static void RecordAccepted(string project, int count, double durationMs)
    {
        EventsAccepted.Add(count, new KeyValuePair<string, object?>("project_id", project));
        IngestionLatencyMs.Record(durationMs, new KeyValuePair<string, object?>("project_id", project));
        Interlocked.Add(ref TotalAccepted, count);
        string key = SeriesKey(AcceptedByProject, project);
        AcceptedByProject.AddOrUpdate(key, count, (_, cur) => cur + count);
    }

    public static void RecordProjected(string project, int count, double durationMs)
    {
        EventsProjected.Add(count, new KeyValuePair<string, object?>("project_id", project));
        ProjectionLatencyMs.Record(durationMs, new KeyValuePair<string, object?>("project_id", project));
        ProjectionBatchSize.Record(count, new KeyValuePair<string, object?>("project_id", project));
        Interlocked.Add(ref TotalProjected, count);
        string key = SeriesKey(ProjectedByProject, project);
        ProjectedByProject.AddOrUpdate(key, count, (_, cur) => cur + count);
    }

    public static void RecordRejected(string project, int count, string reason)
    {
        EventsRejected.Add(count,
            new KeyValuePair<string, object?>("project_id", project),
            new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Add(ref TotalRejected, count);
        string key = SeriesKey(RejectedByReason, reason);
        RejectedByReason.AddOrUpdate(key, count, (_, cur) => cur + count);
    }

    public static void RecordDeadLettered(string project, int count, string reason)
    {
        EventsDeadLettered.Add(count,
            new KeyValuePair<string, object?>("project_id", project),
            new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Add(ref TotalDeadLettered, count);
        string key = SeriesKey(DeadLetteredByProject, project);
        DeadLetteredByProject.AddOrUpdate(key, count, (_, cur) => cur + count);
    }

    public static string GeneratePrometheusMetrics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP outbox_pending Current unpublished broker outbox depth.");
        sb.AppendLine("# TYPE outbox_pending gauge");
        sb.AppendLine($"outbox_pending {Interlocked.Read(ref OutboxPendingCache)}");
        sb.AppendLine("# HELP outbox_oldest_seconds Age of the oldest pending outbox message in seconds.");
        sb.AppendLine("# TYPE outbox_oldest_seconds gauge");
        sb.AppendLine($"outbox_oldest_seconds {Volatile.Read(ref OutboxOldestSecondsCache):F1}");
        sb.AppendLine("# HELP inbox_pending Current unprojected inbox depth.");
        sb.AppendLine("# TYPE inbox_pending gauge");
        sb.AppendLine($"inbox_pending {Interlocked.Read(ref InboxPendingCache)}");

        sb.AppendLine("# HELP events_accepted_total Total number of events accepted into the system.");
        sb.AppendLine("# TYPE events_accepted_total counter");
        sb.AppendLine($"events_accepted_total {Interlocked.Read(ref TotalAccepted)}");
        foreach (var (project, count) in AcceptedByProject)
            sb.AppendLine($"events_accepted_total{{project_id=\"{project}\"}} {count}");

        sb.AppendLine("# HELP events_projected_total Total number of events successfully projected.");
        sb.AppendLine("# TYPE events_projected_total counter");
        sb.AppendLine($"events_projected_total {Interlocked.Read(ref TotalProjected)}");
        foreach (var (project, count) in ProjectedByProject)
            sb.AppendLine($"events_projected_total{{project_id=\"{project}\"}} {count}");

        sb.AppendLine("# HELP events_rejected_total Total number of events rejected.");
        sb.AppendLine("# TYPE events_rejected_total counter");
        sb.AppendLine($"events_rejected_total {Interlocked.Read(ref TotalRejected)}");
        foreach (var (reason, count) in RejectedByReason)
            sb.AppendLine($"events_rejected_total{{reason=\"{reason}\"}} {count}");

        sb.AppendLine("# HELP events_dead_lettered_total Total number of events moved to dead-letter storage.");
        sb.AppendLine("# TYPE events_dead_lettered_total counter");
        sb.AppendLine($"events_dead_lettered_total {Interlocked.Read(ref TotalDeadLettered)}");
        foreach (var (project, count) in DeadLetteredByProject)
            sb.AppendLine($"events_dead_lettered_total{{project_id=\"{project}\"}} {count}");

        return sb.ToString();
    }
}
