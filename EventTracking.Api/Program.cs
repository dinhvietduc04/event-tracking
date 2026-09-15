using System.Threading.RateLimiting;
using EventTracking.Api;
using EventTracking.Api.Access;
using EventTracking.Api.Demo;
using EventTracking.Api.Services;
using Microsoft.OpenApi;
using EventTracking.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

string[] operations = ["--migrate", "--provision", "--retain", "--revoke", "--storage-info"];
var builder = WebApplication.CreateBuilder(args.Where(arg => !operations.Contains(arg)).ToArray());
var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
storage.Validate(builder.Configuration);
builder.Services.AddSingleton(storage);
if (builder.Configuration["PORT"] is { Length: > 0 } port)
{
    if (!int.TryParse(port, out int portNumber) || portNumber is < 1 or > 65535) throw new InvalidOperationException("Invalid PORT.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{portNumber}");
}

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info.Description = "Project-scoped event tracking. Distributed: durable 202 inbox commit; Hosted: 200 queryable commit. See docs/API_V1.md.";
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ProjectKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer",
            Description = "Project API key. Ingestion requires ingest; analytics requires read."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        var permission = context.Description.ActionDescriptor.EndpointMetadata.OfType<ProjectPermission>().SingleOrDefault();
        if (permission is not null)
        {
            operation.Security = [new OpenApiSecurityRequirement
                { [new OpenApiSecuritySchemeReference("ProjectKey", context.Document)] = [] }];
            operation.Description = $"{operation.Description} Requires {permission.Name} permission; project is derived from the key.";
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
if (storage.Durable)
{
    string connection = builder.Configuration.GetConnectionString("Tracking")
        ?? throw new InvalidOperationException("Set ConnectionStrings:Tracking for PostgreSQL, or explicitly select Storage:Profile=Volatile for the old local prototype.");
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
    builder.Services.AddDbContextFactory<TrackingDbContext>(options => options.UseNpgsql(connection));
    builder.Services.AddSingleton<IProjectKeys, PostgresKeys>();
    builder.Services.AddSingleton<PostgresStore>();
    builder.Services.AddSingleton<PostgresAnalytics>();
    if (storage.Profile == "Distributed" && storage.WorkerEnabled) builder.Services.AddHostedService<PostgresWorker>();
}
else builder.Services.AddSingleton<IProjectKeys, ProjectKeys>();
builder.Services.AddSingleton<EventValidation>();
builder.Services.AddSingleton<IEventQueue, EventQueue>();
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
if (!storage.Durable || builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("DemoShop:Enabled"))
    builder.Services.AddHostedService<EventIngestionWorker>(); // The local shop deliberately retains its independent prototype store.
builder.Services.AddSingleton<DemoShop>();
int permits = builder.Configuration.GetValue("Ingestion:RequestsPerMinute", 600);
if (permits < 1) throw new InvalidOperationException("Ingestion:RequestsPerMinute must be positive.");
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("ingestion", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Project().ProjectId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
    options.OnRejected = async (rejection, cancellationToken) =>
    {
        rejection.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "Project ingestion rate limit exceeded.")
            .ExecuteAsync(rejection.HttpContext);
    };
});

var app = builder.Build();
_ = app.Services.GetRequiredService<IProjectKeys>();
_ = app.Services.GetRequiredService<EventValidation>();
if (storage.Durable)
{
    await DatabaseSetup.InitializeAsync(app.Services, app.Configuration, app.Environment, args.Contains("--migrate"), args.Contains("--provision"));
    if (args.Contains("--storage-info"))
    {
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT pg_database_size(current_database()),(SELECT count(*) FROM inbox WHERE processed_at IS NULL),coalesce((SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()),false)");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { profile = storage.Profile, databaseBytes = reader.GetInt64(0),
            maxDatabaseBytes = storage.MaxDatabaseBytes, pendingEvents = reader.GetInt64(1), tls = reader.GetBoolean(2) }));
        return;
    }
    if (args.Contains("--retain"))
    {
        int removed = await app.Services.GetRequiredService<PostgresStore>().RetainAsync(default);
        app.Logger.LogInformation("Retention removed {Count} expired identities", removed);
        return;
    }
    if (args.Contains("--revoke"))
    {
        string hash = app.Configuration["Administration:KeyHash"] ?? throw new InvalidOperationException("Set Administration:KeyHash to revoke a key.");
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = DatabaseSql.Command(connection, null, "UPDATE credentials SET revoked=true WHERE key_hash=$1", hash.ToUpperInvariant());
        if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Credential not found.");
        return;
    }
    if (args.Contains("--migrate") || args.Contains("--provision")) return;
}
else app.Logger.LogWarning("Explicit Volatile profile: accepted events are lost on restart and retries double-count.");

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRouting();
app.Use(RequestBoundary.Invoke);
app.Use(ProjectAccess.Authorize);
app.UseRateLimiter();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("DemoShop:Enabled"))
{
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Redirect("/shop/"));
    app.MapGet("/shop/", () => Results.Redirect("/shop/index.html"));
    app.MapDemoShop();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "alive", profile = storage.Profile, durability = storage.Durable ? "durable" : "volatile" }));
app.MapGet("/health/ready", async (IServiceProvider services, CancellationToken ct) =>
{
    if (storage.Durable)
    {
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        await using var command = DatabaseSql.Command(connection, null, "SELECT profile FROM storage_state WHERE id=1");
        if ((string?)await command.ExecuteScalarAsync(ct) != storage.Profile) return Results.Problem(statusCode: 503, title: "Storage profile has changed; restart with matching configuration.");
    }
    return Results.Ok(new { status = "ready", profile = storage.Profile });
});
app.MapTrackingApi();
app.Run();

public partial class Program;
