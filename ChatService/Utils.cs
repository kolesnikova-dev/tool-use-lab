using System.Text;
using System.Text.Json;

namespace ClaudeReminder;

internal static class Utils
{
    internal static string? GetString(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double GetDouble(IReadOnlyDictionary<string, JsonElement> input, string key, double fallback = 0) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    // Accepts 3 and 3.0 (models occasionally send whole numbers as floats); null for anything else.
    internal static int? GetInt(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Number)
            return null;
        if (v.TryGetInt32(out int whole))
            return whole;
        return v.TryGetDouble(out double d) && d == Math.Truncate(d) && Math.Abs(d) <= int.MaxValue ? (int)d : null;
    }

    internal static int[]? GetIntArray(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<int>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int n))
                return null;
            result.Add(n);
        }
        return result.ToArray();
    }

}