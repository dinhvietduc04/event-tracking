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
    public bool Durable => Profile != "Volatile";

    public void Validate(IConfiguration configuration)
    {
        double maxLate = configuration.GetValue("Ingestion:MaxLateDays", 7d);
        if (Profile is not ("Volatile" or "Distributed" or "Hosted")
            || WorkerBatchSize is < 1 or > 1000 || PollIntervalMilliseconds is < 50 or > 60000
            || RetentionDays is < 1 or > 3650 || RetentionDays <= maxLate || IdentityRetentionDays < RetentionDays
            || IdentityRetentionDays <= maxLate || IdentityRetentionDays > 3650
            || MaxProjectEvents < 1 || MaxProjectBytes < 1 || MaxDatabaseBytes < 1)
            throw new InvalidOperationException("Invalid storage profile, limits, or retention (event retention must exceed allowed lateness; identity retention must cover event retention).");
    }
}
