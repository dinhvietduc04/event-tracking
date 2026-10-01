using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventTracking.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record V1EventRequest(
    Guid EventId,
    string? EventType,
    int SchemaVersion,
    DateTimeOffset? OccurredAt,
    string? UserId = null,
    string? AnonymousId = null,
    string? SessionId = null,
    JsonElement? Properties = null
);

public sealed record EventAcceptance(
    Guid EventId,
    string Status,
    DateTimeOffset ReceivedAt,
    string Durability,
    bool AlreadyAccepted = false
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BatchEventRequest(V1EventRequest?[]? Events);

public sealed record BatchAcceptance(IReadOnlyList<EventAcceptance> Events);
