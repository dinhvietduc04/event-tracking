using Microsoft.Extensions.Configuration;

namespace EventTracking.Persistence;

public sealed class StorageOptions
{
    public string Profile { get; set; } = "Distributed";
    public bool MigrateOnStartup { get; set; }
    public bool AllowProfileTransition { get; set; }
    public int WorkerBatchSize { get; set; } = 100;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int RetentionDays { get; set; } = 30;
    public int IdentityRetentionDays { get; set; } = 30;
    public long MaxProjectEvents { get; set; } = 100_000;
    public long MaxProjectBytes { get; set; } = 100 * 1024 * 1024;
    public long MaxDatabaseBytes { get; set; } = 400 * 1024 * 1024;
    public int MaxOutboxPending { get; set; } = 10_000;
    public int WorkerShutdownTimeoutSeconds { get; set; } = 30;
    public bool Durable => Profile != "Volatile";

    public void Validate(IConfiguration configuration)
    {
        double maxLate = configuration.GetValue("Ingestion:MaxLateDays", 7d);
        if (Profile is not ("Volatile" or "Distributed" or "Hosted")
            || WorkerBatchSize is < 1 or > 1000 || PollIntervalMilliseconds is < 50 or > 60000
            || RetentionDays is < 1 or > 3650 || RetentionDays <= maxLate || IdentityRetentionDays < RetentionDays
            || IdentityRetentionDays <= maxLate || IdentityRetentionDays > 3650
            || MaxProjectEvents < 1 || MaxProjectBytes < 1 || MaxDatabaseBytes < 1 || MaxOutboxPending < 1
            || WorkerShutdownTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Invalid storage profile, limits, or retention (event retention must exceed allowed lateness; identity retention must cover event retention).");
    }
}

public sealed class ClickHouseOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://localhost:8123";
    public string Database { get; set; } = "event_tracking";
    public string Username { get; set; } = "default";
    public string? Password { get; set; }

    public void Validate()
    {
        if (!Enabled) return;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(Database) || Database.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new InvalidOperationException("ClickHouse requires an absolute HTTP(S) URL and a simple database name.");
    }
}

public sealed class RabbitMqOptions
{
    public bool Enabled { get; set; }
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";
    public string Exchange { get; set; } = "event-tracking.v1";
    public string Queue { get; set; } = "event-tracking.v1.events";
    public string DeadLetterExchange { get; set; } = "event-tracking.v1.dlx";
    public string DeadLetterQueue { get; set; } = "event-tracking.v1.dead-letters";
    public int MaxAttempts { get; set; } = 8;
    public int Prefetch { get; set; } = 32;

    public void Validate()
    {
        if (!Enabled) return;
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps")
            || string.IsNullOrWhiteSpace(Exchange) || string.IsNullOrWhiteSpace(Queue)
            || string.IsNullOrWhiteSpace(DeadLetterExchange) || string.IsNullOrWhiteSpace(DeadLetterQueue)
            || MaxAttempts is < 1 or > 100 || Prefetch is < 1 or > 1000)
            throw new InvalidOperationException("RabbitMQ requires a valid AMQP URI, topology names, attempts and prefetch limits.");
    }
}
