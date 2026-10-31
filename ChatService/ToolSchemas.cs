using System.Text.Json;
using Anthropic.Models.Messages;

namespace ClaudeReminder;

internal static class ToolSchemas
{
    /// <summary>The always-on tools plus, when given, Anthropic's web search.</summary>
    internal static List<ToolUnion> Build(WebSearchTool20250305? webSearch)
    {
        var tools = All;
        if (webSearch is not null)
            tools.Add(new ToolUnion(webSearch));
        return tools;
    }

    internal static List<ToolUnion> All => new()
    {
        new Tool
        {
            Name = "add_duration_to_datetime",
            Description = "Adds a duration to a datetime and returns the resulting datetime formatted as 'Thursday, April 03, 2025 10:30:00 AM'. Supports seconds, minutes, hours, days, weeks, months, and years. Month and year arithmetic handles variable month lengths and leap years. Negative durations move into the past. Defaults: duration=0, unit='days'.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["datetime_str"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "The datetime to add to, in ISO 8601. Accepted shapes: 'YYYY-MM-DD' (e.g. '2025-04-03'), 'YYYY-MM-DDTHH:MM:SS' (e.g. '2025-04-03T10:30:00'), or 'YYYY-MM-DD HH:MM:SS'. If no timezone is given, UTC is assumed." }),
                    ["duration"] = JsonSerializer.SerializeToElement(
                        new { type = "number", description = "Amount to add. Positive for future, negative for past. Fractional values are allowed for seconds/minutes/hours/days/weeks; months and years require whole numbers. Defaults to 0.", @default = 0 }),
                    ["unit"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Time unit of the duration. Defaults to 'days'.", @enum = new[] { "seconds", "minutes", "hours", "days", "weeks", "months", "years" }, @default = "days" })
                },
                Required = ["datetime_str"]
            }
        },
        new Tool
        {
            Name = "set_reminder",
            Description = "Creates a timed reminder that notifies the user at the given timestamp with the given content. Use when the user wants to be reminded about something at a specific future time. The reminder is persisted and triggered through the user's notification channels. The system handles timezone internally.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["content"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Message shown in the notification, e.g. 'Take medication' or 'Join video call with team'." }),
                    ["timestamp"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "When to trigger the reminder, as ISO 8601 (YYYY-MM-DDTHH:MM:SS) or a Unix timestamp. Timezone is handled internally." })
                },
                Required = ["content", "timestamp"]
            }
        },
        new Tool
        {
            Name = "batch_tool",
            Description = "Invoke multiple other tool calls in a single step. Use when several independent tool calls are needed at once. Each invocation names a tool and provides its arguments as a JSON-encoded string.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["invocations"] = JsonSerializer.SerializeToElement(
                        new
                        {
                            type = "array",
                            description = "The tool calls to invoke.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "Name of the tool to invoke." },
                                    arguments = new { type = "string", description = "Arguments for that tool, encoded as a JSON string. Must conform to that tool's input_schema." }
                                },
                                required = new[] { "name", "arguments" }
                            }
                        })
                },
                Required = ["invocations"]
            }
        },
        new Tool
        {
            Name = "get_current_datetime",
            Description = "Returns the current time in user requested format, if user does not specify format, a default format is used(yyyy-MM-dd'T'HH:mm:ssK). Use when the user asks for the current time or when a tool needs to know the current time.",
            InputSchema = new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>{
                    ["date_format"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = ".NET custom date format string, e.g. 'yyyy-MM-dd' or 'yyyy-MM-dd HH:mm:ss'. Case-sensitive: yyyy year, MM month, dd day, HH 24-hour hour, mm minute, ss second." }),
                },
                Required = new List<string>()
            }
        },
        // Anthropic's built-in text editor: the schema is defined server-side, we only declare it.
        new ToolTextEditor20250728()
    };
}