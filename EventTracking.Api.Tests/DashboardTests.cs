using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTracking.Api.Dashboard;
using EventTracking.Api.Persistence;
using EventTracking.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace EventTracking.Api.Tests;

public sealed class DashboardTests
{
    private const string Password = "synthetic-dashboard-test-password";
    private static Dictionary<string, string?> Settings => new()
    {
        ["Dashboard:Enabled"] = "true", ["Dashboard:Bootstrap:Username"] = "analyst",
        ["Dashboard:Bootstrap:Password"] = Password, ["Dashboard:Bootstrap:Projects"] = "a"
    };
    private static HttpClient Client(WebApplicationFactory<Program> app) => app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object value)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/dashboard-api/auth/token");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/dashboard-api" + path) { Content = JsonContent.Create(value) };
        message.Headers.Add("X-CSRF-Token", token.GetProperty("token").GetString());
        return await client.SendAsync(message);
    }
    private static async Task Login(HttpClient client) =>
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/auth/login", new { username = "analyst", password = Password })).StatusCode);
    private static string Window => $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";
    private static V1EventRequest Event(string type = "page_view") => new(Guid.NewGuid(), type, 1, DateTimeOffset.UtcNow.AddMinutes(-1), "demo-user-001",
        SessionId: "session-demo", Properties: JsonSerializer.SerializeToElement(new { source = "browser", demo = true }));

    [PostgresFact]
    public async Task SharedKeys_ValidateLoginSessionsAndCsrfAcrossInstancesAndRestart()
    {
        foreach (string profile in new[] { "Hosted", "Distributed" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using (var seed = db.App(profile, settings: Settings))
            using (var client = Client(seed))
                (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();

            var settings = new Dictionary<string, string?>
            {
                ["Dashboard:Enabled"] = "true", ["ReverseProxy:TrustForwardedProto"] = "true"
            };
            using var appA = db.App(profile, seed: false, settings: settings);
            // Distinct deployment paths must not change the application's protection purposes.
            using var appB = db.App(profile, seed: false, settings: settings)
                .WithWebHostBuilder(builder => builder.UseContentRoot(AppContext.BaseDirectory));
            using var a = ProxyClient(appA);
            using var b = ProxyClient(appB);
            Assert.Equal("EventTracking", appA.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
            Assert.NotEqual(appA.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
                appB.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath);

            using var tokenResponse = await a.GetAsync("/dashboard-api/auth/token");
            tokenResponse.EnsureSuccessStatusCode();
            string csrfCookie = Cookie(tokenResponse);
            string anonymousToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            // Initialize the other instance's key cache before receiving the login.
            (await b.GetAsync("/dashboard-api/auth/token")).EnsureSuccessStatusCode();
            using var login = await Send(b, HttpMethod.Post, "/auth/login", csrfCookie, anonymousToken,
                new { username = "analyst", password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            string sessionCookie = Cookie(login);
            string cookies = $"{csrfCookie}; {sessionCookie}";
            for (int i = 0; i < 12; i++)
            {
                using var session = await Send(i % 2 == 0 ? a : b, HttpMethod.Get, "/session", cookies);
                Assert.Equal(HttpStatusCode.OK, session.StatusCode);
                var user = await session.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("analyst", user.GetProperty("username").GetString());
                Assert.Equal("a", Assert.Single(user.GetProperty("projects").EnumerateArray()).GetProperty("id").GetString());
            }
            using var authenticatedTokenResponse = await Send(a, HttpMethod.Get, "/auth/token", cookies);
            authenticatedTokenResponse.EnsureSuccessStatusCode();
            string authenticatedToken = (await authenticatedTokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(b, HttpMethod.Post, "/auth/logout", cookies)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(b, HttpMethod.Post, "/auth/logout", cookies, authenticatedToken + "invalid")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Send(b, HttpMethod.Post, "/auth/logout", cookies, authenticatedToken)).StatusCode);
            long persistedKeys = await db.Scalar("SELECT count(*) FROM data_protection_keys");
            Assert.True(persistedKeys > 0);
            appA.Dispose();
            appB.Dispose();

            using var restarted = db.App(profile, seed: false, settings: settings);
            using var c = ProxyClient(restarted);
            Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Get, "/session", cookies)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Send(c, HttpMethod.Post, "/auth/logout", cookies, authenticatedToken)).StatusCode);
            Assert.Equal(persistedKeys, await db.Scalar("SELECT count(*) FROM data_protection_keys"));
        }
    }

    private static HttpClient ProxyClient(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    private static string Cookie(HttpResponseMessage response)
    {
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie.Split(';')[0];
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        string cookies, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, "/dashboard-api" + path);
        request.Headers.Add("Cookie", cookies);
        if (token is not null) request.Headers.Add("X-CSRF-Token", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    [PostgresFact]
    public async Task NonDevelopmentLoginBehindHttpsProxy_KeepsSecureCookiesAndAuthenticates()
    {
        await using var db = await PostgresTestDatabase.Create();
        var settings = Settings;
        settings["ReverseProxy:TrustForwardedProto"] = "true";
        using var app = db.App("Hosted", seed: false, settings: settings);
        using var client = app.CreateClient(new()
        {
            BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false, HandleCookies = false
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        await app.Services.GetRequiredService<DashboardAccounts>().Provision(
            app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());

        var tokenResponse = await client.GetAsync("/dashboard-api/auth/token");
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        string csrfCookie = Assert.Single(tokenResponse.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/dashboard-api/auth/login")
        { Content = JsonContent.Create(new { username = "analyst", password = Password }) };
        login.Headers.Add("Cookie", csrfCookie.Split(';')[0]);
        login.Headers.Add("X-CSRF-Token", token.GetProperty("token").GetString());
        var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sessionCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        using var session = new HttpRequestMessage(HttpMethod.Get, "/dashboard-api/session");
        session.Headers.Add("Cookie", sessionCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(session)).StatusCode);
    }

    [PostgresFact]
    public async Task LoginRequiresCsrf_ProjectsEnforceMembership_AndLogoutRevokesCookie()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var client = Client(app);
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/dashboard/", home.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/dashboard/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard-api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/dashboard-api/auth/login", new { username = "analyst", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "/auth/login", new { username = "analyst", password = "incorrect" })).StatusCode);
        await Login(client);
        var session = await client.GetFromJsonAsync<JsonElement>("/dashboard-api/session");
        Assert.Equal("a", Assert.Single(session.GetProperty("projects").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("password", session.GetRawText(), StringComparison.OrdinalIgnoreCase);
        foreach (var endpoint in new[] { "overview", "events" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/dashboard-api/projects/b/{endpoint}?{Window}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/dashboard-api/projects/a/{endpoint}?{Window}&projectId=b")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, "/projects/b/demo/events", new { events = new[] { Event() } })).StatusCode);
        // A valid dashboard cookie cannot replace a server API key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/analytics/events")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/dashboard-api/projects", new { name = "Forged" })).StatusCode);
        var created = await (await Post(client, "/projects", new { name = "My new project" })).Content.ReadFromJsonAsync<DashboardProject>();
        Assert.NotNull(created); Assert.True(created.CanDemo);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/dashboard-api/projects/{created.Id}/overview?{Window}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard-api/session")).StatusCode);
        Assert.Equal(1, await db.Scalar("SELECT count(*) FROM dashboard_users WHERE password_hash NOT LIKE '%synthetic%'"));
    }

    [PostgresFact]
    public async Task DemoActionsReachDashboard_WithStablePurchaseRetriesAndScopedFilters()
    {
        foreach (string profile in new[] { "Hosted", "Distributed" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using var app = db.App(profile, settings: Settings);
            using var client = Client(app); await Login(client);
            var events = new[] { Event(), Event("login") };
            var batch = await Post(client, "/projects/a/demo/events", new { events });
            Assert.Equal(profile == "Hosted" ? HttpStatusCode.OK : HttpStatusCode.Accepted, batch.StatusCode);
            var purchase = new DemoPurchase(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "session-demo", "mug", 2);
            (await Post(client, "/projects/a/demo/purchase", purchase)).EnsureSuccessStatusCode();
            // Repeated client IDs and confirmed mock orders never add another event.
            (await Post(client, "/projects/a/demo/events", new { events })).EnsureSuccessStatusCode();
            var retry = await (await Post(client, "/projects/a/demo/purchase", purchase)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(retry.GetProperty("receipt").GetProperty("alreadyAccepted").GetBoolean());
            Assert.Equal(4800, retry.GetProperty("amountMinor").GetInt32());
            Assert.Equal(HttpStatusCode.Conflict, (await Post(client, "/projects/a/demo/purchase", purchase with { Quantity = 3 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "/projects/a/demo/purchase", purchase with { ProductId = "unknown" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "/projects/a/demo/events", new { events = new[] { Event("purchase") } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "/projects/a/demo/events", new { events = new[] { Event() with { Properties = JsonSerializer.SerializeToElement(new { source = "server" }) } } })).StatusCode);
            var window = Window;
            if (profile == "Distributed")
            {
                var pending = await client.GetFromJsonAsync<JsonElement>($"/dashboard-api/projects/a/overview?{window}");
                Assert.Equal(3, pending.GetProperty("status").GetProperty("pending").GetInt64());
                Assert.Empty(pending.GetProperty("summary").EnumerateArray());
                await app.Services.GetRequiredService<PostgresStore>().ProcessBatchAsync(default);
            }
            var overview = await client.GetFromJsonAsync<JsonElement>($"/dashboard-api/projects/a/overview?{window}");
            Assert.Equal(3, overview.GetProperty("summary").EnumerateArray().Sum(e => e.GetProperty("count").GetInt32()));
            Assert.Equal(1, overview.GetProperty("users").GetProperty("identifiedUsers").GetInt32());
            Assert.Equal(0, overview.GetProperty("status").GetProperty("pending").GetInt32());
            if (profile == "Distributed") Assert.True(overview.GetProperty("status").GetProperty("processingP95Seconds").GetDouble() >= 0);
            var first = (await client.GetFromJsonAsync<TimelinePage>($"/dashboard-api/projects/a/events?{window}&limit=1"))!;
            Assert.Single(first.Events); Assert.NotNull(first.NextCursor);
            var second = (await client.GetFromJsonAsync<TimelinePage>($"/dashboard-api/projects/a/events?{window}&limit=1&cursor={Uri.EscapeDataString(first.NextCursor)}"))!;
            Assert.NotEqual(first.Events[0].EventId, second.Events[0].EventId);
            var filtered = (await client.GetFromJsonAsync<TimelinePage>($"/dashboard-api/projects/a/events?{window}&userId=demo-user-001&propertyName=source&propertyValue=%22server%22"))!;
            Assert.Equal("purchase", Assert.Single(filtered.Events).EventType);
            Assert.Equal(4800, filtered.Events[0].Properties.GetProperty("amountMinor").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/dashboard-api/projects/a/events?{window}&eventType=login&cursor={Uri.EscapeDataString(first.NextCursor)}")).StatusCode);
        }
    }

    [PostgresFact]
    public async Task MembershipRemovalAndAccountDisablingTakeEffectOnExistingSessions()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var client = Client(app); await Login(client);
        await db.Execute("UPDATE project_memberships SET can_demo=false");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, "/projects/a/demo/events", new { events = new[] { Event() } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, "/projects/a/demo/purchase", new DemoPurchase(Guid.NewGuid(), DateTimeOffset.UtcNow, "s", "mug", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/dashboard-api/projects/a/events?{Window}")).StatusCode);
        await db.Execute("DELETE FROM project_memberships");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/dashboard-api/projects/a/events?{Window}")).StatusCode);
        await db.Execute("UPDATE dashboard_users SET disabled=true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard-api/session")).StatusCode);
    }

    [PostgresFact]
    public async Task NonDevelopmentDashboardIsOptIn_AndBootstrapDoesNotResetStoredAccounts()
    {
        await using var db = await PostgresTestDatabase.Create();
        using (var app = db.App("Hosted", settings: Settings))
        using (var client = Client(app)) { await Login(client); }
        await db.Execute("UPDATE dashboard_users SET disabled=true; DELETE FROM project_memberships");
        using (var restarted = db.App("Hosted", settings: Settings))
        using (var client = Client(restarted))
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "/auth/login", new { username = "analyst", password = Password })).StatusCode);
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM project_memberships"));
        using var production = db.App("Hosted", seed: false);
        using var prodClient = Client(production);
        Assert.Equal(HttpStatusCode.NotFound, (await prodClient.GetAsync("/dashboard-api/auth/token")).StatusCode);
        using var enabled = db.App("Hosted", seed: false, settings: new() { ["Dashboard:Enabled"] = "true" });
        using var enabledClient = Client(enabled);
        var home = await enabledClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/dashboard/", home.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await enabledClient.GetAsync("/dashboard/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await enabledClient.GetAsync("/dashboard-api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await enabledClient.GetAsync("/shop/index.html")).StatusCode);
    }

    [PostgresFact]
    public async Task LoginAttemptsAreRateLimited()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted", settings: Settings);
        using var client = Client(app);
        for (int attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "/auth/login", new { username = "missing", password = "incorrect" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(client, "/auth/login", new { username = "analyst", password = Password })).StatusCode);
    }
}
