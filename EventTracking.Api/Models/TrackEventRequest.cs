namespace EventTracking.Api.Models;

public sealed record TrackEventRequest(
    string EventType,
    string? UserId,
    DateTimeOffset? OccurredAt
);
