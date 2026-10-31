using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace ToolUseLab;

internal static class ToolNames
{
    public const string SetReminder = "set_reminder";
    public const string AddDurationToDatetime = "add_duration_to_datetime";
    public const string GetCurrentDatetime = "get_current_datetime";
    public const string TextEditor = "str_replace_based_edit_tool";
}

internal sealed record ToolResult
{
    public const string BlockType = "tool_result";

    [JsonPropertyName("type")]
    public string Type => BlockType;

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    public static ToolResult Success(string toolUseId, string content) =>
        new() { ToolUseId = toolUseId, Content = content };

    public static ToolResult Failure(string toolUseId, string error) =>
        new() { ToolUseId = toolUseId, Content = error, IsError = true };
}

internal readonly record struct DurationResult(bool Success, DateTime? NewDatetime, string? Error)
{
    public static DurationResult Ok(DateTime dt) => new(true, dt, null);
    public static DurationResult Fail(string error) => new(false, null, error);
}

internal enum TimeUnit { Seconds, Minutes, Hours, Days, Weeks, Months, Years }

internal static class Tools
{
    // The one output format the tool promises. Culture-explicit so day/month
    // names are English regardless of host locale (matches Python's C locale).
    internal const string OutputFormat = "dddd, MMMM dd, yyyy hh:mm:ss tt";
    private static readonly CultureInfo ToolCulture = CultureInfo.InvariantCulture;

    // ---------- set_reminder ----------

    internal static void SetReminder(string content, string timestamp)
    {
        Console.WriteLine($"----\nSetting the following reminder for {ConsoleText.Sanitize(timestamp)}:\n{ConsoleText.Sanitize(content)}\n----");
    }

    // ---------- add_duration_to_datetime ----------

    internal static DurationResult AddDurationToDatetime(
        string? datetimeStr,
        double duration = 0,
        string? unit = "days")
    {
        if (string.IsNullOrWhiteSpace(datetimeStr))
            return DurationResult.Fail("Datetime is required.");
        if (!double.IsFinite(duration))
            return DurationResult.Fail("Duration must be a finite number.");
        if (!TryParseUnit(unit, out var parsedUnit, out var unitError))
            return DurationResult.Fail(unitError!);

        if (!TryParseToolDatetime(datetimeStr, out var datetime))
            return DurationResult.Fail(
                $"Invalid datetime '{datetimeStr}'. Use ISO 8601, e.g. '2025-04-03' or '2025-04-03T10:30:00'.");

        if (parsedUnit is TimeUnit.Months or TimeUnit.Years && duration != Math.Truncate(duration))
            return DurationResult.Fail($"Duration for '{unit}' must be a whole number.");

        try
        {
            var newDatetime = parsedUnit switch
            {
                TimeUnit.Seconds => datetime.AddSeconds(duration),
                TimeUnit.Minutes => datetime.AddMinutes(duration),
                TimeUnit.Hours => datetime.AddHours(duration),
                TimeUnit.Days => datetime.AddDays(duration),
                TimeUnit.Weeks => datetime.AddDays(duration * 7),
                TimeUnit.Months => datetime.AddMonths((int)duration),
                TimeUnit.Years => datetime.AddYears((int)duration),
                _ => throw new ArgumentOutOfRangeException(nameof(unit))
            };
            return DurationResult.Ok(newDatetime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DurationResult.Fail("Resulting datetime is out of range.");
        }
    }

    internal static string FormatForDisplay(DurationResult result) =>
        result.Success
            ? $"New datetime: {result.NewDatetime!.Value.ToString(OutputFormat, ToolCulture)}"
            : result.Error!;

    // ---------- helpers ----------

    private static bool TryParseUnit(string? unit, out TimeUnit parsed, out string? error)
    {
        parsed = default;
        error = null;

        if (string.IsNullOrWhiteSpace(unit))
        {
            error = "Unit is required.";
            return false;
        }

        // Reject numeric strings ("42" parses to (TimeUnit)42 without IsDefined).
        if (Enum.TryParse<TimeUnit>(unit, ignoreCase: true, out parsed)
            && Enum.IsDefined(parsed))
        {
            return true;
        }

        error = $"Invalid unit '{unit}'. Use one of: {string.Join(", ", Enum.GetNames<TimeUnit>())}.";
        return false;
    }

    // Accept the shapes an LLM realistically sends, without asking it to speak
    // strftime or .NET custom formats. TryParseExact first (strict), then
    // TryParse as a forgiving fallback.
    private static bool TryParseToolDatetime(string input, out DateTime result)
    {
        string[] exactFormats =
        {
            "yyyy-MM-dd",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd HH:mm:ss",
        };

        if (DateTime.TryParseExact(input, exactFormats, ToolCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result))
        {
            return true;
        }

        return DateTime.TryParse(input, ToolCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    internal static string GetCurrentDatetime(string? dateFormat = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;          // local time with offset
        var tz = TimeZoneInfo.Local;

        if (string.IsNullOrWhiteSpace(dateFormat))
        {
            string zoneName = tz.IsDaylightSavingTime(now) ? tz.DaylightName : tz.StandardName;
            return $"{now.ToString(OutputFormat, CultureInfo.InvariantCulture)}, {zoneName}";
        }

        return now.ToString(dateFormat, CultureInfo.InvariantCulture);
    }
}