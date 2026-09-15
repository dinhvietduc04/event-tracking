using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTracking.Api.Models;

namespace EventTracking.Api.Persistence;

public sealed record CanonicalEvent(V1EventRequest Event, string Payload, string Hash, long StoredBytes)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static DateTimeOffset Microseconds(DateTimeOffset time) => new(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero);

    public static CanonicalEvent Create(V1EventRequest request)
    {
        var normalized = request with { EventType = request.EventType!.Trim(),
            OccurredAt = Microseconds(request.OccurredAt!.Value),
            Properties = request.Properties is { ValueKind: JsonValueKind.Object } p ? p : JsonSerializer.SerializeToElement(new { }) };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, JsonSerializer.SerializeToElement(normalized, JsonOptions));
        byte[] bytes = stream.ToArray();
        return new(normalized, Encoding.UTF8.GetString(bytes), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length * 2L + 1024);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetDecimal().ToString("G29", CultureInfo.InvariantCulture)); break;
            default: element.WriteTo(writer); break;
        }
    }
}
