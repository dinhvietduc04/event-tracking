using System.Globalization;
using System.Text.Json;

namespace EventTracking.Api.Services;

public static class JsonNumbers
{
    // TryGetDecimal can silently round very small or very precise JSON numbers. Reject that loss
    // so fingerprint equality never merges two distinct numeric property values.
    public static bool IsExactDecimal(JsonElement value) =>
        value.TryGetDecimal(out var number)
        && Normalize(value.GetRawText()) is { } original
        && original == Normalize(number.ToString("G29", CultureInfo.InvariantCulture));

    private static string? Normalize(string text)
    {
        var parts = text.ToLowerInvariant().Split('e');
        int exponent = 0;
        if (
            parts.Length == 2
            && !int.TryParse(
                parts[1],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out exponent
            )
        )
            return null;
        string mantissa = parts[0];
        bool negative = mantissa.StartsWith('-');
        if (negative)
            mantissa = mantissa[1..];
        int dot = mantissa.IndexOf('.');
        long scale = (long)exponent - (dot < 0 ? 0 : mantissa.Length - dot - 1);
        string digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0)
            return "0";
        int trailing = digits.Length - digits.TrimEnd('0').Length;
        return (negative ? "-" : "")
            + digits[..(digits.Length - trailing)]
            + "e"
            + (scale + trailing).ToString(CultureInfo.InvariantCulture);
    }
}
