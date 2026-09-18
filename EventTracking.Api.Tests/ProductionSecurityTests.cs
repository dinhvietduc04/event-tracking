using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EventTracking.Api.Dashboard;
using EventTracking.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class ProductionSecurityTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static Dictionary<string, string?> Safe => new()
    {
        ["AllowedHosts"] = "analytics.example.test",
        ["ConnectionStrings:Tracking"] = "Host=database.example.test;Database=tracking;Username=runtime;SSL Mode=VerifyFull;GSS Encryption Mode=Disable"
    };

    [Theory]
    [InlineData("Storage:MigrateOnStartup", "true")]
    [InlineData("Storage:AllowProfileTransition", "true")]
    [InlineData("ConnectionStrings:Operator", "operator-secret-must-not-leak")]
    [InlineData("Dashboard:Bootstrap:Password", "bootstrap-secret-must-not-leak")]
    [InlineData("Administration:Password", "recovery-secret-must-not-leak")]
    [InlineData("ProjectAccess:Keys:0:KeyHash", "legacy-seed")]
    [InlineData("DemoShop:Enabled", "true")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("ReverseProxy:TrustForwardedProto", "true")]
    public void ProductionRejectsUnsafeRuntimeConfiguration(string setting, string value)
    {
        var values = Safe; values[setting] = value;
        var config = Config(values);
        var error = Assert.Throws<InvalidOperationException>(() => ProductionSecurity.Validate(config,
            config.GetSection("Storage").Get<StorageOptions>() ?? new(), strict: true, operatorMode: false, api: true));
        Assert.DoesNotContain("secret-must-not-leak", error.ToString());
    }

    [Theory]
    [InlineData("Prefer")]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("Disable")]
    public void ProductionRequiresHostnameVerifiedTls(string ssl)
    {
        var values = Safe;
        values["ConnectionStrings:Tracking"] = $"Host=database.example.test;Username=runtime;SSL Mode={ssl};GSS Encryption Mode=Disable";
        Assert.Throws<InvalidOperationException>(() => ProductionSecurity.Connection(Config(values), strict: true, operatorMode: false));
        Assert.NotEmpty(ProductionSecurity.Connection(Config(Safe), strict: true, operatorMode: false));
    }

    [Fact]
    public void ProductionRequiresDurability_AndOperatorConnectionNeverFallsBackToRuntime()
    {
        Assert.True(ProductionSecurity.Required("Production")); Assert.True(ProductionSecurity.Required("Staging"));
        Assert.False(ProductionSecurity.Required("Testing")); Assert.False(ProductionSecurity.Required("Development"));
        Assert.Throws<InvalidOperationException>(() => ProductionSecurity.Validate(Config(Safe), new() { Profile = "Volatile" }, true, false, true));
        Assert.Throws<InvalidOperationException>(() => ProductionSecurity.Connection(Config(Safe), true, true));
        var values = Safe;
        values["ConnectionStrings:Operator"] = values["ConnectionStrings:Tracking"];
        values["Administration:ExpectedDatabase"] = "wrong-target";
        Assert.Throws<InvalidOperationException>(() => ProductionSecurity.Connection(Config(values), true, true));
        values["Administration:ExpectedDatabase"] = "tracking";
        values["Administration:ExpectedHost"] = "database.example.test";
        Assert.NotEmpty(ProductionSecurity.Connection(Config(values), true, true));
        Assert.Throws<InvalidOperationException>(() => ProtectionCertificates.Load(Config(Safe), required: true));
        values["DataProtection:Certificate:Base64"] = "invalid-private-certificate-secret";
        var error = Assert.Throws<InvalidOperationException>(() => ProtectionCertificates.Load(Config(values), true));
        Assert.DoesNotContain("invalid-private-certificate-secret", error.ToString());
    }

    [PostgresFact]
    public async Task RuntimeRolesAllowTheirWork_ButRejectMigrationRecoveryAndAuditTampering()
    {
        foreach (string profile in new[] { "Hosted", "Distributed" })
        {
            await using var db = await PostgresTestDatabase.Create();
            using var seed = db.App(profile, settings: new()
            {
                ["Dashboard:Bootstrap:Username"] = "security-test", ["Dashboard:Bootstrap:Password"] = "synthetic-security-password",
                ["Dashboard:Bootstrap:Projects"] = "a"
            });
            var owner = seed.Services.GetRequiredService<NpgsqlDataSource>();
            await using var api = await RuntimeRole.Create(db, owner, "Api");
            await RuntimeDatabaseAccess.Verify(api.Source, "Api");
            // An owner password with a restricted effective role can RESET ROLE back to owner.
            await using (var disguisedOwner = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString)
                { Options = "-c role=" + api.Name }.ConnectionString))
                await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDatabaseAccess.Verify(disguisedOwner, "Api"));
            using var app = db.App(profile, seed: false, settings: new()
            {
                ["ConnectionStrings:Tracking"] = api.ConnectionString, ["Storage:MigrateOnStartup"] = "false", ["Dashboard:Enabled"] = "true"
            });
            using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") }).WithKey(TestProjects.BothA);
            var payload = new V1EventRequest(Guid.NewGuid(), "restricted_role", 1, DateTimeOffset.UtcNow.AddMinutes(-1));
            (await client.PostAsJsonAsync("/v1/events", payload)).EnsureSuccessStatusCode();
            if (profile == "Distributed")
            {
                await using var worker = await RuntimeRole.Create(db, owner, "Worker");
                await RuntimeDatabaseAccess.Verify(worker.Source, "Worker");
                var store = new PostgresStore(worker.Source, new() { Profile = profile }, NullLogger<PostgresStore>.Instance);
                Assert.Equal(1, await store.ProcessBatchAsync(default));
                await Denied(worker.Source, "SELECT password_hash FROM dashboard_users");
                await Denied(worker.Source, "SELECT xml FROM data_protection_keys");
                await Denied(worker.Source, "UPDATE inbox SET payload='{}'");
                await Denied(worker.Source, "UPDATE storage_state SET profile='Hosted'");
            }
            Assert.Equal(1, await db.Scalar("SELECT count(*) FROM events"));
            (await client.GetAsync("/v1/analytics/events")).EnsureSuccessStatusCode();
            // This also exercises data-protection sequence grants and shared login counters.
            var csrf = await client.GetFromJsonAsync<JsonElement>("/dashboard-api/auth/token");
            using var login = new HttpRequestMessage(HttpMethod.Post, "/dashboard-api/auth/login")
                { Content = JsonContent.Create(new { username = "security-test", password = "synthetic-security-password" }) };
            login.Headers.Add("X-CSRF-Token", csrf.GetProperty("token").GetString());
            (await client.SendAsync(login)).EnsureSuccessStatusCode();
            var projects = app.Services.GetRequiredService<DashboardAccounts>();
            Guid user;
            await using (var connection = await owner.OpenConnectionAsync())
            await using (var query = DatabaseSql.Command(connection, null, "SELECT id FROM dashboard_users WHERE username='security-test'")) user = (Guid)(await query.ExecuteScalarAsync())!;
            Assert.NotNull(await projects.CreateProject(user, "Restricted creator", default));
            await Denied(api.Source, "CREATE TABLE forbidden(id int)");
            await Denied(api.Source, "UPDATE dashboard_users SET password_hash='tampered'");
            await Denied(api.Source, "UPDATE dashboard_users SET disabled=true");
            await Denied(api.Source, "DELETE FROM audit_records");
            await Denied(api.Source, "UPDATE audit_records SET actor='tampered'");
            await Denied(api.Source, "UPDATE data_protection_keys SET xml='tampered'");
            await Denied(api.Source, "DELETE FROM event_identity");
            await Denied(api.Source, "UPDATE storage_state SET profile='Volatile'");
            // Detect and then remove accidental column grants as well as table grants.
            await db.Execute($"GRANT UPDATE(password_hash) ON dashboard_users TO {api.Name}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDatabaseAccess.Verify(api.Source, "Api"));
            await RuntimeDatabaseAccess.Grant(owner, api.Name, "Api");
            await RuntimeDatabaseAccess.Verify(api.Source, "Api");
            await Denied(api.Source, "UPDATE dashboard_users SET password_hash='tampered'");
        }
    }

    [PostgresFact]
    public async Task SharedLoginBudgetSurvivesInstanceChangesAndUsesBoundedStorage()
    {
        await using var db = await PostgresTestDatabase.Create();
        using var app = db.App("Hosted");
        using var second = db.App("Hosted", seed: false);
        var a = app.Services.GetRequiredService<SharedLoginLimiter>();
        var b = second.Services.GetRequiredService<SharedLoginLimiter>();
        // Each 60-second window starts on its first attempt, independent of wall-clock minute boundaries.
        for (int attempt = 0; attempt < 10; attempt++) Assert.True(await (attempt % 2 == 0 ? a : b).Acquire("Case-Normalized", default));
        Assert.False(await b.Acquire("  case-normalized  ", default));
        using var restarted = db.App("Hosted", seed: false);
        Assert.False(await restarted.Services.GetRequiredService<SharedLoginLimiter>().Acquire("CASE-NORMALIZED", default));
        await db.Execute("UPDATE login_rate_limits SET window_start=window_start-interval '2 minutes'");
        Assert.True(await b.Acquire("case-normalized", default));
        Assert.Equal(2, await db.Scalar("SELECT count(*) FROM login_rate_limits"));
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM login_rate_limits WHERE bucket LIKE '%normalized%'"));
        await db.Execute("UPDATE login_rate_limits SET attempts=120 WHERE bucket='global'");
        Assert.False(await a.Acquire("a-new-account", default));
        Assert.Equal(2, await db.Scalar("SELECT count(*) FROM login_rate_limits"));
        await db.Execute("ALTER TABLE login_rate_limits RENAME TO unavailable_limits");
        await Assert.ThrowsAsync<PostgresException>(() => b.Acquire("case-normalized", default));
    }

    [PostgresFact]
    public async Task ProtectionKeysCanBeEncrypted_RotatedAndRecoveredWithoutChangingCookies()
    {
        await using var db = await PostgresTestDatabase.Create();
        string original;
        using (var plaintext = db.App("Hosted"))
        {
            original = plaintext.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("recovery-test").Protect("synthetic-session");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProtectionCertificates.VerifyStoredKeys(plaintext.Services));
        }
        using var firstCertificate = Certificate("first");
        using var secondCertificate = Certificate("second");
        var firstSettings = CertificateSettings(firstCertificate);
        using (var wrap = db.App("Hosted", seed: false, settings: firstSettings))
            Assert.True(await ProtectionCertificates.ProtectStoredKeys(wrap.Services) > 0);
        Assert.Equal(0, await db.Scalar("SELECT count(*) FROM data_protection_keys WHERE xml LIKE '%<value>%'"));
        using (var restart = db.App("Hosted", seed: false, settings: firstSettings))
        {
            await ProtectionCertificates.VerifyStoredKeys(restart.Services);
            Assert.Equal("synthetic-session", restart.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("recovery-test").Unprotect(original));
        }
        var rotation = CertificateSettings(secondCertificate);
        rotation["DataProtection:PreviousCertificates:0:Base64"] = firstSettings["DataProtection:Certificate:Base64"];
        rotation["DataProtection:PreviousCertificates:0:Password"] = "synthetic-pfx-password";
        using (var rotate = db.App("Hosted", seed: false, settings: rotation))
        {
            Assert.Equal("synthetic-session", rotate.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("recovery-test").Unprotect(original));
            Assert.True(await ProtectionCertificates.ProtectStoredKeys(rotate.Services) > 0);
        }
        using (var recovery = db.App("Hosted", seed: false, settings: CertificateSettings(secondCertificate)))
        {
            await ProtectionCertificates.VerifyStoredKeys(recovery.Services);
            Assert.Equal("synthetic-session", recovery.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("recovery-test").Unprotect(original));
        }
        using var wrong = db.App("Hosted", seed: false, settings: firstSettings);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => ProtectionCertificates.VerifyStoredKeys(wrong.Services));
        Assert.Equal(2, await db.Scalar("SELECT count(*) FROM audit_records WHERE action='protection.keys-wrapped'"));
    }

    private static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        return new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
    }
    private static Dictionary<string, string?> CertificateSettings(X509Certificate2 certificate) => new()
    {
        ["DataProtection:Certificate:Base64"] = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "synthetic-pfx-password")),
        ["DataProtection:Certificate:Password"] = "synthetic-pfx-password"
    };
    private static async Task Denied(NpgsqlDataSource source, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = DatabaseSql.Command(connection, null, sql);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private sealed class RuntimeRole(PostgresTestDatabase db, string name, NpgsqlDataSource source, string connectionString) : IAsyncDisposable
    {
        public string Name => name;
        public NpgsqlDataSource Source => source;
        public string ConnectionString => connectionString;
        public static async Task<RuntimeRole> Create(PostgresTestDatabase db, NpgsqlDataSource owner, string kind)
        {
            string name = "m5_role_" + Guid.NewGuid().ToString("N"), password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            await db.Execute($"CREATE ROLE {name} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
            string connection = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = name, Password = password }.ConnectionString;
            var role = new RuntimeRole(db, name, NpgsqlDataSource.Create(connection), connection);
            try { await RuntimeDatabaseAccess.Grant(owner, name, kind); return role; }
            catch { await role.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await db.Execute($"DROP OWNED BY {name}; DROP ROLE {name}");
        }
    }
}
