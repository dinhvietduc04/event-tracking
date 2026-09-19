using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EventTracking.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")))
            Skip =
                "Set TEST_POSTGRES_CONNECTION to a local PostgreSQL admin connection. CI requires these real-service tests.";
    }
}

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    public string Name { get; } = "m2_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";
    private string _admin = "";

    public static async Task<PostgresTestDatabase> Create()
    {
        var database = new PostgresTestDatabase();
        database._admin = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")!;
        await using var connection = new NpgsqlConnection(database._admin);
        await connection.OpenAsync();
        // Identifier is generated above, never supplied by a caller.
        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE \"{database.Name}\"",
            connection
        );
        await command.ExecuteNonQueryAsync();
        database.ConnectionString = new NpgsqlConnectionStringBuilder(database._admin)
        {
            Database = database.Name,
            Timeout = 3,
            CommandTimeout = 5,
        }.ConnectionString;
        return database;
    }

    public WebApplicationFactory<Program> App(
        string profile = "Distributed",
        bool seed = true,
        Dictionary<string, string?>? settings = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (seed)
                TestProjects.Configure(builder);
            // Exercise non-development HTTP defaults without requiring production TLS/role secrets.
            // ProductionSecurityTests cover the strict deployment configuration separately.
            else
                builder.UseEnvironment("Testing");
            builder.UseSetting("Storage:Profile", profile);
            builder.UseSetting("Storage:MigrateOnStartup", "true");
            builder.UseSetting("ConnectionStrings:Tracking", ConnectionString);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            if (settings is not null)
                foreach (var setting in settings)
                    builder.UseSetting(setting.Key, setting.Value);
        });

    public async Task<long> Scalar(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE \"{Name}\" WITH (FORCE)",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }
}
