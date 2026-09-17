using System.Security.Claims;
using System.Text.Json;
using EventTracking.Api.Access;
using EventTracking.Api.Persistence;
using EventTracking.Api.Services;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace EventTracking.Api.Dashboard;

public sealed record DashboardLogin(string? Username, string? Password);
public sealed record NewProject(string? Name);
public sealed record DemoPurchase(Guid EventId, DateTimeOffset? OccurredAt, string? SessionId, string ProductId, int Quantity);

public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication app)
    {
        var group = app.MapGroup("/dashboard-api").RequireRateLimiting("dashboard");
        group.MapGet("/auth/token", (HttpContext context, IAntiforgery csrf) =>
            Results.Ok(new { token = csrf.GetAndStoreTokens(context).RequestToken }));
        group.MapPost("/auth/login", async (DashboardLogin request, HttpContext context, DashboardAccounts accounts, CancellationToken ct) =>
        {
            var user = await accounts.Authenticate(request.Username, request.Password, ct);
            if (user is null) return Results.Problem(statusCode: 401, title: "Username or password is incorrect.");
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username)
            ], DashboardAccess.Scheme));
            await context.SignInAsync(DashboardAccess.Scheme, principal);
            return Results.Ok(new { username = user.Username });
        }).RequireRateLimiting("dashboard-login").WithMetadata(new BodyLimit(4096));
        group.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(DashboardAccess.Scheme);
            return Results.NoContent();
        }).WithMetadata(new DashboardPermission());
        group.MapGet("/session", async (HttpContext context, DashboardAccounts accounts, CancellationToken ct) =>
            Results.Ok(new { username = context.User.Identity!.Name, projects = await accounts.Projects(context.UserId(), ct) }))
            .WithMetadata(new DashboardPermission());
        group.MapPost("/projects", async (NewProject request, HttpContext context, DashboardAccounts accounts, CancellationToken ct) =>
        {
            string name = request.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) return Invalid("name", "Use a project name of 1–100 characters.");
            var project = await accounts.CreateProject(context.UserId(), name, ct);
            return project is null ? Results.Problem(statusCode: 409, title: "This account cannot create another project (limit 20).") : Results.Ok(project);
        }).WithMetadata(new DashboardPermission());

        var projectGroup = group.MapGroup("/projects/{projectId}").WithMetadata(new DashboardPermission(Project: true));
        projectGroup.MapGet("/overview", async (DateTimeOffset? from, DateTimeOffset? to, string? eventType,
            string? userId, string? propertyName, string? propertyValue, HttpContext context,
            PostgresAnalytics analytics, StorageOptions storage, CancellationToken ct) =>
        {
            var filter = new AnalyticsFilter(from, to, eventType, userId, propertyName, propertyValue);
            var errors = Validate(filter);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            string project = context.Project().ProjectId;
            var summary = analytics.SummaryAsync(project, filter, ct);
            var series = analytics.TimeSeriesAsync(project, filter, to - from <= TimeSpan.FromDays(2) ? "hour" : "day", ct);
            var users = analytics.ActiveAsync(project, filter, ct);
            var status = analytics.StatusAsync(project, ct);
            await Task.WhenAll(summary, series, users, status);
            return Results.Ok(new { summary = summary.Result, series = series.Result, users = users.Result, status = status.Result,
                profile = storage.Profile, refreshedAt = DateTimeOffset.UtcNow, interval = to - from <= TimeSpan.FromDays(2) ? "hour" : "day" });
        });
        projectGroup.MapGet("/events", async (DateTimeOffset? from, DateTimeOffset? to, string? eventType, string? userId,
            string? propertyName, string? propertyValue, string? cursor, int? limit, HttpContext context, PostgresAnalytics analytics, CancellationToken ct) =>
        {
            var filter = new AnalyticsFilter(from, to, eventType, userId, propertyName, propertyValue);
            var errors = Validate(filter);
            if (limit is < 1 or > 100) errors["limit"] = ["Use 1–100 events per page."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            try { return Results.Ok(await analytics.TimelineAsync(context.Project().ProjectId, filter, limit ?? 50, cursor, ct)); }
            catch (ArgumentException) { return Invalid("cursor", "The cursor is invalid or the filters changed."); }
        });
        projectGroup.MapPost("/demo/events", async (BatchEventRequest request, HttpContext context, PostgresStore store,
            EventValidation validation, StorageOptions storage, CancellationToken ct) =>
        {
            if (request.Events is not { Length: > 0 and <= 100 }) return Invalid("events", "Send 1–100 events.");
            Dictionary<string, string[]> errors = [];
            for (int i = 0; i < request.Events.Length; i++)
            {
                var item = request.Events[i];
                if (item is null) { errors[$"events[{i}]"] = ["Event is required."]; continue; }
                foreach (var error in validation.Validate(item, DateTimeOffset.UtcNow)) errors[$"events[{i}].{error.Key}"] = error.Value;
                if (item.EventType is not ("page_view" or "login" or "checkout_started")) errors[$"events[{i}].eventType"] = ["Use a supported browser activity; purchases are confirmed by the demo backend."];
                if (item.Properties is not { ValueKind: JsonValueKind.Object } properties || !properties.TryGetProperty("source", out var source)
                    || source.ValueKind != JsonValueKind.String || source.GetString() != "browser") errors[$"events[{i}].properties.source"] = ["Browser events must identify source=browser."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var receipts = await store.AcceptAsync(context.Project().ProjectId, request.Events.Select(e => e!).ToArray(), ct);
            return storage.Profile == "Hosted" ? Results.Ok(new BatchAcceptance(receipts)) : Results.Accepted(value: new BatchAcceptance(receipts));
        }).WithMetadata(new DashboardPermission(Project: true, Demo: true), new BodyLimit(1024 * 1024)).RequireRateLimiting("ingestion");
        projectGroup.MapPost("/demo/purchase", async (DemoPurchase order, HttpContext context, EventValidation validation,
            PostgresStore store, StorageOptions storage, CancellationToken ct) =>
        {
            int unitPrice = order.ProductId switch { "mug" => 2400, "tote" => 3200, "notebook" => 1800, _ => 0 };
            if (unitPrice == 0 || order.Quantity is < 1 or > 10) return Invalid("order", "Choose a catalog product and quantity 1–10.");
            var item = new V1EventRequest(order.EventId, "purchase", 1, order.OccurredAt, "demo-user-001", SessionId: order.SessionId,
                Properties: JsonSerializer.SerializeToElement(new { source = "server", demo = true, orderId = order.EventId,
                    productId = order.ProductId, quantity = order.Quantity, amountMinor = unitPrice * order.Quantity, currency = "USD" }));
            var errors = validation.Validate(item, DateTimeOffset.UtcNow);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var receipt = (await store.AcceptAsync(context.Project().ProjectId, [item], ct))[0];
            return Results.Json(new { receipt, amountMinor = unitPrice * order.Quantity, currency = "USD" }, statusCode: storage.Profile == "Hosted" ? 200 : 202);
        }).WithMetadata(new DashboardPermission(Project: true, Demo: true)).RequireRateLimiting("ingestion");
    }

    private static Dictionary<string, string[]> Validate(AnalyticsFilter filter)
    {
        var errors = PostgresAnalytics.Validate(filter);
        if (filter.From is null || filter.To is null || filter.To <= filter.From || filter.To - filter.From > TimeSpan.FromDays(31))
            errors["from"] = ["Choose a UTC date range greater than zero and at most 31 days."];
        return errors;
    }
    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
