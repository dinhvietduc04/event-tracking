namespace EventTracking.Api.Models;

public sealed record TrackedEvent(
    Guid Id,
    string EventType,
    string? UserId,
    DateTimeOffset OccurredAt,
    DateTimeOffset IngestedAt);
