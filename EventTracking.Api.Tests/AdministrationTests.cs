using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTracking.Api.Dashboard;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class AdministrationTests
{
    private const string Password = "synthetic-administration-password";
    private static Dictionary<string, string?> Settings => new()
    {
        ["Dashboard:Enabled"] = "true", ["Dashboard:Bootstrap:Username"] = "admin",
        ["Dashboard:Bootstrap:Password"] = Password, ["Dashboard:Bootstrap:Projects"] = "a"
    };
    private static HttpClient Client(WebApplicationFactory<Program> app) => app.CreateClient(new()
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object value)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/dashboard-api/auth/token");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/dashboard-api" + path) { Content = JsonContent.Create(value) };
        message.Headers.Add("X-CSRF-Token", token.GetProperty("token").GetString());
        return await client.SendAsync(message);
    }
    private static async Task Login(HttpClient client, string username = "admin", string password = Password) =>
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/auth/login", new { username, password })).StatusCode);
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static Task Operate(WebApplicationFactory<Program> app, string action, string username = "admin", string? password = null) =>
        DashboardOperator.Execute(app.Services.GetRequiredService<NpgsqlDataSource>(), Config(new()
        {
            ["Administration:Action"] = action, ["Administration:Username"] = username,
            ["Administration:ProjectId"] = "a", ["Administration:Actor"] = "test-operator",
            ["Administration:Reason"] = "Synthetic integration verification", ["Administration:Password"] = password
        }));
    private static Task<bool> Provision(WebApplicationFactory<Program> app, string username, string projects = "a") =>
        app.Services.GetRequiredService<DashboardAccounts>().Provision(Config(new()
        {
            ["Dashboard:Bootstrap:Username"] = username, ["Dashboard:Bootstrap:Password"] = Password,
            ["Dashboard:Bootstrap:Projects"] = projects
        }));

    [PostgresFact]
    public async Task RolesRequireExplicitGrant_AndNewProjectsMakeTheirCreatorAdmin()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var admin = Client(app); await Login(admin);
        foreach (string route in new[] { "members", "keys", "audit" })
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/dashboard-api/projects/a/admin/{route}")).StatusCode);
        await Operate(app, "grant-admin");
        var session = await admin.GetFromJsonAsync<JsonElement>("/dashboard-api/session");
        Assert.Equal("admin", session.GetProperty("projects")[0].GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/dashboard-api/projects/a/admin/keys", new { permissions = new[] { "read" } })).StatusCode);
        Assert.True(await Provision(app, "viewer"));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, "/projects/a/admin/members", new { username = "viewer", role = "viewer" })).StatusCode);
        using var viewer = Client(app); await Login(viewer, "viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/dashboard-api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(viewer, "/projects/a/demo/purchase", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(viewer, "/projects/a/admin/members", new { username = "viewer", role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(viewer, "/projects/a/admin/keys", new { permissions = new[] { "read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, "/projects/a/admin/members", new { username = "viewer", role = "contributor" })).StatusCode);
        session = await viewer.GetFromJsonAsync<JsonElement>("/dashboard-api/session");
        Assert.Equal("contributor", session.GetProperty("projects")[0].GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/dashboard-api/projects/a/admin/audit")).StatusCode);
        var created = await (await Post(viewer, "/projects", new { name = "Owned project" })).Content.ReadFromJsonAsync<DashboardProject>();
        Assert.NotNull(created); Assert.True(created.CanManage); Assert.Equal("admin", created.Role);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/dashboard-api/projects/{created.Id}/admin/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/dashboard-api/projects/{created.Id}/admin/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(admin, "/projects/a/admin/members", new { username = "admin", role = "remove" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, "/projects/a/admin/members", new { username = "viewer", role = "remove" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/dashboard-api/projects/a/admin/members")).StatusCode);
    }

    [PostgresFact]
    public async Task KeysAreScoped_RotateAtomically_AndNeverAppearInAuditOrLists()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var admin = Client(app); await Login(admin); await Operate(app, "grant-admin");
        var issuedResponse = await Post(admin, "/projects/a/admin/keys", new { permissions = new[] { "read" } });
        Assert.Equal(HttpStatusCode.OK, issuedResponse.StatusCode);
        Assert.Contains("no-store", issuedResponse.Headers.CacheControl!.ToString());
        var issued = await issuedResponse.Content.ReadFromJsonAsync<JsonElement>();
        string key = issued.GetProperty("key").GetString()!, hash = issued.GetProperty("keyHash").GetString()!;
        using var reader = Client(app).WithKey(key);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/analytics/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/v1/events", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/dashboard-api/projects/a/admin/keys")).StatusCode);
        string foreignHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TestProjects.ReadB)));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(admin, "/projects/a/admin/keys/revoke", new { keyHash = foreignHash })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(admin, "/projects/a/admin/keys/rotate", new { keyHash = foreignHash })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(admin, "/projects/b/admin/keys", new { permissions = new[] { "read" } })).StatusCode);
        var rotated = await (await Post(admin, "/projects/a/admin/keys/rotate", new { keyHash = hash })).Content.ReadFromJsonAsync<JsonElement>();
        string nextKey = rotated.GetProperty("key").GetString()!, nextHash = rotated.GetProperty("keyHash").GetString()!;
        Assert.NotEqual(key, nextKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/v1/analytics/events")).StatusCode);
        reader.WithKey(nextKey);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/analytics/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(admin, "/projects/a/admin/keys/rotate", new { keyHash = hash })).StatusCode);
        foreach (string endpoint in new[] { "keys", "audit" })
        {
            string body = await admin.GetStringAsync($"/dashboard-api/projects/a/admin/{endpoint}");
            Assert.DoesNotContain(key, body); Assert.DoesNotContain(nextKey, body); Assert.DoesNotContain(Password, body);
        }
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM audit_records WHERE action='key.rotated'"));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, "/projects/a/admin/keys/revoke", new { keyHash = nextHash })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/v1/analytics/events")).StatusCode);
    }

    [PostgresFact]
    public async Task PasswordResetAndDisableInvalidateSessionsAcrossInstances_WithoutBootstrapResurrection()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var stale = Client(app); await Login(stale);
        const string replacement = "synthetic-replacement-password";
        using var second = db.App("Hosted", settings: Settings);
        _ = second.Services;
        await Operate(second, "reset-password", password: replacement);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/dashboard-api/session")).StatusCode);
        using var fresh = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(fresh, "/auth/login", new { username = "admin", password = Password })).StatusCode);
        await Login(fresh, password: replacement);
        Assert.False(await Provision(app, "admin"));
        await Operate(second, "disable");
        using var disabled = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(disabled, "/auth/login", new { username = "admin", password = replacement })).StatusCode);
        await Operate(second, "enable");
        // Even a cookie not presented while disabled cannot become valid again after enable.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/dashboard-api/session")).StatusCode);
        await Login(fresh, password: replacement);
        Assert.Equal(3, await db.Scalar("SELECT session_version FROM dashboard_users WHERE username='admin'"));
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM audit_records WHERE details::text LIKE '%password%'"));
        Assert.Equal(3, await db.Scalar("SELECT count(*) FROM audit_records WHERE actor='operator:test-operator'"));
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackKeyRotation_AndAccountRecovery()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var admin = Client(app); await Login(admin); await Operate(app, "grant-admin");
        var issued = await (await Post(admin, "/projects/a/admin/keys", new { permissions = new[] { "read" } })).Content.ReadFromJsonAsync<JsonElement>();
        long keyCount = await db.Scalar("SELECT count(*) FROM credentials");
        await db.Execute("CREATE FUNCTION reject_test_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic audit failure'; END $$; CREATE TRIGGER reject_test_audit BEFORE INSERT ON audit_records FOR EACH ROW EXECUTE FUNCTION reject_test_audit()");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(admin, "/projects/a/admin/keys/rotate", new { keyHash = issued.GetProperty("keyHash").GetString() })).StatusCode);
        Assert.Equal(keyCount, await db.Scalar("SELECT count(*) FROM credentials"));
        using var reader = Client(app).WithKey(issued.GetProperty("key").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/analytics/events")).StatusCode);
        await Assert.ThrowsAsync<PostgresException>(() => Operate(app, "reset-password", password: "synthetic-password-should-rollback"));
        Assert.Equal(0, await db.Scalar("SELECT session_version FROM dashboard_users WHERE username='admin'"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/dashboard-api/session")).StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentAdministratorsCannotRemoveEachOtherAndLeaveNoAdmin()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var first = Client(app); await Login(first); await Operate(app, "grant-admin");
        Assert.True(await Provision(app, "second-admin")); await Operate(app, "grant-admin", "second-admin");
        using var second = Client(app); await Login(second, "second-admin");
        var responses = await Task.WhenAll(
            Post(first, "/projects/a/admin/members", new { username = "second-admin", role = "remove" }),
            Post(second, "/projects/a/admin/members", new { username = "admin", role = "remove" }));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Forbidden);
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM project_memberships WHERE project_id='a' AND can_manage"));
    }

    [PostgresFact]
    public async Task AdministrativeLimitsAreEnforcedUnderConcurrentKeyIssuance()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var first = Client(app); await Login(first); await Operate(app, "grant-admin");
        using var second = Client(app); await Login(second);
        // Three active fixture keys already exist for project a.
        await db.Execute("INSERT INTO credentials(key_hash,project_id,permissions,revoked) SELECT lpad(to_hex(n),64,'0'),'a',ARRAY['read'],false FROM generate_series(1,16) n");
        long auditCount = await db.Scalar("SELECT count(*) FROM audit_records");
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(first, "/projects/a/admin/keys", new { permissions = new[] { "admin" } })).StatusCode);
        Assert.Equal(auditCount, await db.Scalar("SELECT count(*) FROM audit_records"));
        var responses = await Task.WhenAll(
            Post(first, "/projects/a/admin/keys", new { permissions = new[] { "ingest" } }),
            Post(second, "/projects/a/admin/keys", new { permissions = new[] { "ingest" } }));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(20, await db.Scalar("SELECT count(*) FROM credentials WHERE project_id='a' AND NOT revoked"));
        // Rotation replaces one active credential and remains possible at the limit.
        string existingHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TestProjects.ReadA)));
        Assert.Equal(HttpStatusCode.OK, (await Post(first, "/projects/a/admin/keys/rotate", new { keyHash = existingHash })).StatusCode);
        Assert.Equal(20, await db.Scalar("SELECT count(*) FROM credentials WHERE project_id='a' AND NOT revoked"));
        Assert.True(await Provision(app, "full-account", string.Join(',', Enumerable.Range(1, 20).Select(i => "quota-" + i))));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(first, "/projects/a/admin/members", new { username = "full-account", role = "viewer" })).StatusCode);
        Assert.Equal(20, await db.Scalar("SELECT count(*) FROM project_memberships m JOIN dashboard_users u ON u.id=m.user_id WHERE u.username='full-account'"));
        await Operate(app, "disable", "full-account");
        Assert.Equal(HttpStatusCode.Conflict, (await Post(first, "/projects/a/admin/members", new { username = "full-account", role = "admin" })).StatusCode);
    }

    [PostgresFact]
    public async Task MigrationPreservesExistingMembershipsWithoutGrantingAdministrativeAccess()
    {
        await using var db = await PostgresTestDatabase.Create();
        await using (var context = new TrackingDbContext(new DbContextOptionsBuilder<TrackingDbContext>().UseNpgsql(db.ConnectionString).Options))
        {
            var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260917143254_SharedDataProtectionKeys");
            await db.Execute("INSERT INTO projects(id,name,event_count,stored_bytes) VALUES('legacy','Legacy',0,0); INSERT INTO dashboard_users(id,username,password_hash,disabled) VALUES('00000000-0000-0000-0000-000000000001','legacy','unused',false); INSERT INTO project_memberships(user_id,project_id,can_demo) VALUES('00000000-0000-0000-0000-000000000001','legacy',true)");
            await context.Database.MigrateAsync();
        }
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM project_memberships WHERE can_demo AND NOT can_manage"));
        Assert.Equal(0, await db.Scalar("SELECT session_version FROM dashboard_users WHERE username='legacy'"));
    }
}
