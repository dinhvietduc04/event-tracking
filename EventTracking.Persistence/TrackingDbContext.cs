using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;

namespace EventTracking.Persistence;

public sealed class ProjectRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long EventCount { get; set; }
    public long StoredBytes { get; set; }
}

public sealed class CredentialRecord
{
    public string KeyHash { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string[] Permissions { get; set; } = [];
    public bool Revoked { get; set; }
}

public sealed class EventIdentity
{
    public string ProjectId { get; set; } = "";
    public Guid EventId { get; set; }
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public long StoredBytes { get; set; }
}

public sealed class InboxRecord
{
    public string ProjectId { get; set; } = "";
    public Guid EventId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

public sealed class EventRecord
{
    public string ProjectId { get; set; } = "";
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string? UserId { get; set; }
    public string? AnonymousId { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Properties { get; set; } = "{}";
}

public sealed class StorageState
{
    public int Id { get; set; }
    public string Profile { get; set; } = "";
}

public sealed class DashboardUserRecord
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Disabled { get; set; }
    public int SessionVersion { get; set; }
}

public sealed class ProjectMembership
{
    public Guid UserId { get; set; }
    public string ProjectId { get; set; } = "";
    public bool CanDemo { get; set; }
    public bool CanManage { get; set; }
}

public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Actor { get; set; } = "";
    public string? ProjectId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Details { get; set; } = "{}";
}

public sealed class LoginRateLimit
{
    public string Bucket { get; set; } = "";
    public DateTimeOffset WindowStart { get; set; }
    public int Attempts { get; set; }
}

public sealed class SavedQueryView
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = "";
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventSchemaRecord
{
    public string ProjectId { get; set; } = "";
    public string EventType { get; set; } = "";
    public int Version { get; set; }
    public string Definition { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TrackingDbContext(DbContextOptions<TrackingDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<CredentialRecord> Credentials => Set<CredentialRecord>();
    public DbSet<EventIdentity> Identities => Set<EventIdentity>();
    public DbSet<InboxRecord> Inbox => Set<InboxRecord>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<StorageState> State => Set<StorageState>();
    public DbSet<SavedQueryView> SavedViews => Set<SavedQueryView>();
    public DbSet<EventSchemaRecord> EventSchemas => Set<EventSchemaRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DataProtectionKey>().ToTable("data_protection_keys");
        model.Entity<ProjectRecord>(e => { e.ToTable("projects"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.Name).HasMaxLength(100).HasDefaultValue(""); });
        model.Entity<DashboardUserRecord>(e =>
        {
            e.ToTable("dashboard_users"); e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(60); e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.SessionVersion).HasDefaultValue(0);
        });
        model.Entity<ProjectMembership>(e =>
        {
            e.ToTable("project_memberships"); e.HasKey(x => new { x.UserId, x.ProjectId });
            e.Property(x => x.CanManage).HasDefaultValue(false);
            e.HasOne<DashboardUserRecord>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AuditRecord>(e =>
        {
            e.ToTable("audit_records"); e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200); e.Property(x => x.ProjectId).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(100); e.Property(x => x.Target).HasMaxLength(200);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProjectId, x.OccurredAt, x.Id });
        });
        model.Entity<LoginRateLimit>(e =>
        {
            e.ToTable("login_rate_limits"); e.HasKey(x => x.Bucket); e.Property(x => x.Bucket).HasMaxLength(20);
        });
        model.Entity<SavedQueryView>(e =>
        {
            e.ToTable("saved_query_views"); e.HasKey(x => x.Id);
            e.Property(x => x.ProjectId).HasMaxLength(100); e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProjectId, x.UserId, x.Name }).IsUnique();
            e.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DashboardUserRecord>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<EventSchemaRecord>(e =>
        {
            e.ToTable("event_schemas"); e.HasKey(x => new { x.ProjectId, x.EventType, x.Version });
            e.Property(x => x.ProjectId).HasMaxLength(100); e.Property(x => x.EventType).HasMaxLength(100);
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<CredentialRecord>(e =>
        {
            e.ToTable("credentials"); e.HasKey(x => x.KeyHash); e.Property(x => x.KeyHash).HasMaxLength(64);
            e.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<EventIdentity>(e =>
        {
            e.ToTable("event_identity"); e.HasKey(x => new { x.ProjectId, x.EventId });
            e.Property(x => x.PayloadHash).HasMaxLength(64); e.HasIndex(x => x.OccurredAt);
            e.HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<InboxRecord>(e =>
        {
            e.ToTable("inbox"); e.HasKey(x => new { x.ProjectId, x.EventId }); e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.ReceivedAt).HasFilter("processed_at IS NULL");
            e.HasOne<EventIdentity>().WithOne().HasForeignKey<InboxRecord>(x => new { x.ProjectId, x.EventId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<EventRecord>(e =>
        {
            e.ToTable("events"); e.HasKey(x => new { x.ProjectId, x.EventId });
            e.Property(x => x.EventType).HasMaxLength(100); e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.AnonymousId).HasMaxLength(200); e.Property(x => x.SessionId).HasMaxLength(200);
            e.Property(x => x.Properties).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProjectId, x.OccurredAt, x.EventId });
            e.HasIndex(x => new { x.ProjectId, x.EventType, x.OccurredAt });
            e.HasIndex(x => new { x.ProjectId, x.UserId, x.OccurredAt, x.EventId });
            e.HasIndex(x => x.Properties).HasMethod("gin");
            e.HasOne<EventIdentity>().WithOne().HasForeignKey<EventRecord>(x => new { x.ProjectId, x.EventId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<StorageState>(e => { e.ToTable("storage_state"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); });
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(string.Concat(property.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}

public sealed class TrackingDbContextFactory : IDesignTimeDbContextFactory<TrackingDbContext>
{
    public TrackingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TrackingDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Tracking")
            ?? "Host=localhost;Database=event_tracking;Username=event_tracking").Options);
}
