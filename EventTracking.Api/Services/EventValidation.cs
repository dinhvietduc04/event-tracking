using System.Text.Json;
using EventTracking.Api.Models;
using EventTracking.Persistence;

namespace EventTracking.Api.Services;

public sealed class EventValidation
{
    public const int MaxEventBytes = 32 * 1024;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _maxFuture;
    private static readonly HashSet<string> RestrictedProperties = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "password",
        "token",
        "accessToken",
        "refreshToken",
        "authorization",
        "cookie",
        "secret",
        "apiKey",
    };

    public EventValidation(IConfiguration configuration)
    {
        double lateDays = configuration.GetValue("Ingestion:MaxLateDays", 7d);
        double futureMinutes = configuration.GetValue("Ingestion:MaxFutureMinutes", 5d);
        if (
            !double.IsFinite(lateDays)
            || lateDays is < 0 or > 365
            || !double.IsFinite(futureMinutes)
            || futureMinutes is < 0 or > 1440
        )
            throw new InvalidOperationException("Invalid ingestion timestamp windows.");
        _maxAge = TimeSpan.FromDays(lateDays);
        _maxFuture = TimeSpan.FromMinutes(futureMinutes);
    }

    public Dictionary<string, string[]> Validate(V1EventRequest request, DateTimeOffset now)
    {
        Dictionary<string, string[]> errors = [];
        if (request.EventId == Guid.Empty)
            errors["eventId"] = ["A nonempty UUID is required; reuse it on retries."];
        if (
            string.IsNullOrWhiteSpace(request.EventType)
            || request.EventType.Length > 100
            || request.EventType.Any(char.IsControl)
        )
            errors["eventType"] = ["Use 1–100 characters without control characters."];
        if (request.SchemaVersion != 1)
            errors["schemaVersion"] = ["Only schemaVersion 1 is supported."];
        if (
            request.OccurredAt is null
            || request.OccurredAt < now - _maxAge
            || request.OccurredAt > now + _maxFuture
        )
            errors["occurredAt"] =
            [
                "A timestamp within the configured lateness/future window is required.",
            ];
        CheckIdentity("userId", request.UserId);
        CheckIdentity("anonymousId", request.AnonymousId);
        CheckIdentity("sessionId", request.SessionId);
        if (request.Properties is { } properties && properties.ValueKind != JsonValueKind.Null)
        {
            if (properties.ValueKind != JsonValueKind.Object)
                errors["properties"] = ["Properties must be an object."];
            else
            {
                int count = 0;
                if (!CheckProperties(properties, 1, ref count))
                    errors["properties"] =
                    [
                        "Use at most 4 container levels and 64 total entries, unique 1–100 character keys, strings up to 2048 characters, exactly representable decimal numbers, and no restricted credential fields.",
                    ];
            }
        }
        return errors;

        void CheckIdentity(string name, string? value)
        {
            if (
                value is not null
                && (
                    string.IsNullOrWhiteSpace(value)
                    || value.Length > 200
                    || value.Any(char.IsControl)
                )
            )
                errors[name] = ["Use null or 1–200 characters without control characters."];
        }
    }

    private static bool CheckProperties(JsonElement value, int depth, ref int count)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && depth > 4)
            return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (
                    ++count > 64
                    || string.IsNullOrWhiteSpace(property.Name)
                    || property.Name.Length > 100
                    || property.Name.Any(char.IsControl)
                    || !names.Add(property.Name)
                    || RestrictedProperties.Contains(property.Name)
                    || !CheckProperties(property.Value, depth + 1, ref count)
                )
                    return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (++count > 64 || !CheckProperties(item, depth + 1, ref count))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 2048)
            return false;
        else if (value.ValueKind == JsonValueKind.Number && !JsonNumbers.IsExactDecimal(value))
            return false;
        return true;
    }
}
