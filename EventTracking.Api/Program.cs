using EventTracking.Persistence;
using System.Threading.RateLimiting;
using EventTracking.Api;
using EventTracking.Api.Access;
using EventTracking.Api.Demo;
using EventTracking.Api.Services;
using Microsoft.OpenApi;
using EventTracking.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using EventTracking.Api.Dashboard;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;

string[] operations = ["--migrate", "--provision", "--retain", "--revoke", "--storage-info", "--dashboard-provision", "--dashboard-admin", "--grant-runtime", "--protect-keys"];
foreach (string exclusive in new[] { "--dashboard-admin", "--grant-runtime", "--protect-keys" })
    if (args.Contains(exclusive) && args.Any(arg => operations.Contains(arg) && arg != exclusive && arg != "--migrate"))
        throw new InvalidOperationException($"Run {exclusive} separately from other operator actions (it may be combined with --migrate).");
var builder = WebApplication.CreateBuilder(args.Where(arg => !operations.Contains(arg)).ToArray());
bool operatorMode = args.Any(operations.Contains);
bool strict = ProductionSecurity.Required(builder.Environment.EnvironmentName);
var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
storage.Validate(builder.Configuration);
ProductionSecurity.Validate(builder.Configuration, storage, strict, operatorMode, api: true);
if (operatorMode && !storage.Durable) throw new InvalidOperationException("Database operator commands require a durable profile.");
builder.Services.AddSingleton(storage);
bool dashboard = storage.Durable && builder.Configuration.GetValue("Dashboard:Enabled", builder.Environment.IsDevelopment());
DashboardAccess.Configure(builder.Services, builder.Environment.IsDevelopment());
if (builder.Configuration.GetValue<bool>("ReverseProxy:TrustForwardedProto"))
{
    // Opt in only when the container is reachable exclusively through a trusted TLS proxy.
    // Vercel supplies the original protocol; do not trust forwarded hosts or client IPs here.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
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
    string connection = ProductionSecurity.Connection(builder.Configuration, strict, operatorMode);
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
    builder.Services.AddDbContextFactory<TrackingDbContext>(options => options.UseNpgsql(connection));
    // Cookies and antiforgery tokens must survive instance changes and container restarts.
    var protection = builder.Services.AddDataProtection()
        .SetApplicationName("EventTracking")
        .PersistKeysToDbContext<TrackingDbContext>();
    var certificates = !operatorMode || args.Contains("--protect-keys")
        ? ProtectionCertificates.Load(builder.Configuration, required: strict && !operatorMode)
        : new ProtectionCertificates();
    certificates.Configure(protection);
    builder.Services.AddSingleton(_ => certificates);
    builder.Services.AddSingleton<IProjectKeys, PostgresKeys>();
    builder.Services.AddSingleton<PostgresStore>();
    builder.Services.AddSingleton<PostgresAnalytics>();
    builder.Services.AddSingleton<ProductAnalytics>();
    builder.Services.AddSingleton<DashboardAccounts>();
    builder.Services.AddSingleton<SharedLoginLimiter>();
}
else builder.Services.AddSingleton<IProjectKeys, ProjectKeys>();
builder.Services.AddSingleton<EventValidation>();
builder.Services.AddSingleton<IEventQueue, EventQueue>();
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
if (!storage.Durable || builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("DemoShop:Enabled"))
    builder.Services.AddHostedService<EventIngestionWorker>(); // The local shop retains its independent prototype store.
builder.Services.AddSingleton<DemoShop>();
int permits = builder.Configuration.GetValue("Ingestion:RequestsPerMinute", 600);
if (permits < 1) throw new InvalidOperationException("Ingestion:RequestsPerMinute must be positive.");
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("dashboard", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("dashboard-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("ingestion", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Project().ProjectId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
    options.OnRejected = async (rejection, cancellationToken) =>
    {
        rejection.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "Request limit exceeded. Try again shortly.")
            .ExecuteAsync(rejection.HttpContext);
    };
});

var app = builder.Build();
if (storage.Profile == "Distributed" && builder.Configuration["Storage:WorkerEnabled"] is not null)
    app.Logger.LogWarning("Storage:WorkerEnabled is obsolete. Run EventTracking.Worker separately to project Distributed events.");
_ = app.Services.GetRequiredService<IProjectKeys>();
_ = app.Services.GetRequiredService<EventValidation>();
if (storage.Durable)
{
    _ = app.Services.GetRequiredService<ProtectionCertificates>(); // Ensure the container owns certificate disposal.
    if (strict)
    {
        await using var transport = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        // Opening with validated VerifyFull + GSS disabled proves the TLS handshake succeeded,
        // including hostname/chain verification, even when a pooler terminates client TLS.
        if (!operatorMode) await RuntimeDatabaseAccess.Verify(app.Services.GetRequiredService<NpgsqlDataSource>(), "Api");
    }
    await DatabaseSetup.InitializeAsync(app.Services, app.Configuration, app.Environment, args.Contains("--migrate"), args.Contains("--provision"), operatorMode);
    if (args.Contains("--grant-runtime"))
    {
        await RuntimeDatabaseAccess.Grant(app.Services.GetRequiredService<NpgsqlDataSource>(),
            app.Configuration["Administration:RoleName"] ?? "", app.Configuration["Administration:RoleKind"] ?? "");
        app.Logger.LogInformation("Runtime database grants applied and audited.");
        return;
    }
    if (args.Contains("--protect-keys"))
    {
        int count = await ProtectionCertificates.ProtectStoredKeys(app.Services);
        app.Logger.LogInformation("Encrypted {Count} stored protection keys", count);
        return;
    }
    if (args.Contains("--dashboard-provision") || (dashboard && app.Environment.IsDevelopment()
        && !string.IsNullOrEmpty(app.Configuration["Dashboard:Bootstrap:Password"])))
    {
        bool created = await app.Services.GetRequiredService<DashboardAccounts>().Provision(app.Configuration);
        if (!created && args.Contains("--dashboard-provision"))
            throw new InvalidOperationException("Dashboard username already exists. Reuse its credentials; provisioning does not reset accounts.");
    }
    if (args.Contains("--dashboard-provision")) return;
    if (args.Contains("--storage-info"))
    {
        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = DatabaseSql.Command(connection, null,
            "SELECT pg_database_size(current_database()),(SELECT count(*) FROM inbox WHERE processed_at IS NULL),coalesce((SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()),false)");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { profile = storage.Profile, databaseBytes = reader.GetInt64(0),
            maxDatabaseBytes = storage.MaxDatabaseBytes, pendingEvents = reader.GetInt64(1), tls = reader.GetBoolean(2),
            tlsVerified = new NpgsqlConnectionStringBuilder(connection.ConnectionString) is { SslMode: SslMode.VerifyFull, GssEncryptionMode: GssEncryptionMode.Disable } }));
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
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = DatabaseSql.Command(connection, transaction, "UPDATE credentials SET revoked=true WHERE key_hash=$1 RETURNING project_id", hash.ToUpperInvariant());
        if (await command.ExecuteScalarAsync() is not string projectId) throw new InvalidOperationException("Credential not found.");
        await AuditLog.Write(connection, transaction, "operator:revoke", projectId, "key.revoked", hash.ToUpperInvariant(), new { });
        await transaction.CommitAsync();
        return;
    }
    if (args.Contains("--dashboard-admin"))
    {
        await DashboardOperator.Execute(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Configuration);
        app.Logger.LogInformation("Dashboard administration completed; audit record committed.");
        return;
    }
    if (args.Contains("--migrate") || args.Contains("--provision")) return;
    if (strict) await ProtectionCertificates.VerifyStoredKeys(app.Services);
}

else app.Logger.LogWarning("Explicit Volatile profile: accepted events are lost on restart and retries double-count.");

app.UseExceptionHandler();
if (app.Configuration.GetValue<bool>("ReverseProxy:TrustForwardedProto")) app.UseForwardedHeaders();
if (strict)
{
    app.UseHsts();
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/health"))
        {
            await Results.Problem(statusCode: 400, title: "HTTPS is required.").ExecuteAsync(context);
            return;
        }
        await next(context);
    });
}
if (!strict) app.UseHttpsRedirection();
app.UseRouting();
app.Use(RequestBoundary.Invoke);
app.UseAuthentication();
if (dashboard) app.Use(DashboardAccess.Authorize);
app.Use(ProjectAccess.Authorize);
app.UseRateLimiter();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
if (dashboard)
{
    app.MapGet("/", () => Results.Redirect("/dashboard/"));
    string assets = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "dashboard");
    if (Directory.Exists(assets)) app.UseStaticFiles(new StaticFileOptions { RequestPath = "/dashboard", FileProvider = new PhysicalFileProvider(assets) });
    app.MapGet("/dashboard", () => Results.Redirect("/dashboard/index.html"));
    app.MapDashboard();
}
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("DemoShop:Enabled"))
{
    app.UseStaticFiles();
    if (!dashboard) app.MapGet("/", () => Results.Redirect("/shop/"));
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
