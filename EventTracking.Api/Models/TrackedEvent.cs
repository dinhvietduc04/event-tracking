namespace EventTracking.Api.Models;

public sealed record TrackedEvent(
    Guid Id,
    string EventType,
    string? UserId,
    DateTimeOffset OccurredAt,
    DateTimeOffset IngestedAt,
    string Source = "api",
    string? SessionId = null,
    IReadOnlyDictionary<string, object?>? Properties = null,
    string ProjectId = "demo-shop",
    string? AnonymousId = null,
    int SchemaVersion = 1);
