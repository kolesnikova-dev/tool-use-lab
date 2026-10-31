using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace ClaudeReminder;

/// <summary>Everything one conversation owns: history, tools and what has been seen so far.</summary>
internal sealed class ChatSession
{
    private readonly Func<string, bool> _prompt;

    public ChatSession(WebSearchOptions? webSearch, Func<string, bool> prompt)
    {
        _prompt = prompt;
        Web = webSearch is null ? null : new WebSearchSession(webSearch);
        Editor = new TextEditorTool(confirmWrite: Confirm);
    }

    public List<MessageParam> Messages { get; } = [];
    public TextEditorTool Editor { get; }
    public WebSearchSession? Web { get; }

    /// <summary>Set once any search returned pages; from then on side effects get a warning (and reminders a prompt).</summary>
    public bool WebContentSeen { get; set; }

    /// <summary>Asks the user to approve a side effect. The summary is model-derived, so it is sanitised first.</summary>
    public bool Confirm(string summary) => _prompt(
        (WebContentSeen ? "WARNING: this conversation contains untrusted web content. Review this carefully.\n" : "")
        + ConsoleText.Sanitize(summary));
}

internal static class ChatService
{
    internal const int MaxContinuations = 5;

    internal static async Task Chat(AnthropicClient client, ChatConfig config)
    {
        var session = new ChatSession(config.WebSearch, ConsolePrompt);
        while (true)
        {
            Console.Write("you> ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) break;

            if (input.StartsWith('/'))
                Console.WriteLine(HandleCommand(session, input)); // local commands are never sent to Claude
            else
                await ProcessUserTurnAsync(session, input, p => client.Messages.Create(p), config, Console.WriteLine);
        }
    }

    /// <summary>
    /// One user message and everything it triggers: tool rounds and pause_turn continuations.
    /// Any failure rolls the history back so it never holds a half-finished turn.
    /// </summary>
    internal static async Task ProcessUserTurnAsync(
        ChatSession session, string userText, Func<MessageCreateParams, Task<Message>> send, ChatConfig config, Action<string> write)
    {
        int turnStart = session.Messages.Count;
        // Fixed for the whole turn: a paused turn may end on an unfinished server tool call, and the
        // continuation must still declare that tool or the API answers 400.
        var webTool = session.Web?.Tool();

        void Abort(string reason)
        {
            session.Messages.RemoveRange(turnStart, session.Messages.Count - turnStart);
            write(reason);
        }

        AddUserMessage(session.Messages, userText);
        try
        {
            for (int pauses = 0; ;)
            {
                var response = await send(BuildParameters(config, session, webTool));
                session.Web?.Record(response);
                session.WebContentSeen |= ResponseRenderer.HasWebContent(response);

                string shown = ResponseRenderer.Render(response).Trim();
                if (shown.Length > 0) write($"claude> {shown}");

                // Echo every block exactly as received: search results carry encrypted content that later turns need.
                AddAssistantMessage(session.Messages, ToParam(response));

                if (response.StopReason == StopReason.ToolUse)
                    AddUserMessage(session.Messages, RunTools(session, response));
                else if (response.StopReason == StopReason.PauseTurn && ++pauses <= MaxContinuations)
                    continue;
                else if (response.StopReason == StopReason.PauseTurn || response.StopReason == StopReason.MaxTokens)
                {
                    Abort("claude> (stopped: the answer was too long or the search ran too long; try a narrower question)");
                    return;
                }
                else
                    return;
            }
        }
        catch (AnthropicException ex)
        {
            Abort($"API error: {Brief(ex)}");
            // The organization (Console settings) can disable web search or narrow its domains.
            if (ex is AnthropicBadRequestException && session.Web is { Enabled: true } web
                && (ex.Message.Contains("web search", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("domain", StringComparison.OrdinalIgnoreCase)))
            {
                web.Enabled = false;
                write("Web search was turned off because the API rejected it. Check your organization's web search settings.");
            }
        }
    }

    internal static string HandleCommand(ChatSession session, string line)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        string arg = parts.Length > 1 ? parts[1] : "";
        try
        {
            return parts[0] switch
            {
                "/undo" when arg.Length > 0 => session.Editor.UndoEdit(arg),
                "/clean-backups" => $"Deleted {session.Editor.ClearBackups()} backup file(s).",
                "/web" => WebCommand(session.Web, arg),
                _ => "Commands: /undo <path>, /clean-backups, /web on|off|status",
            };
        }
        catch (Exception ex) when (ex is TextEditorException or IOException or UnauthorizedAccessException)
        {
            return $"Error: {ConsoleText.Sanitize(ex.Message)}";
        }
    }

    private static string WebCommand(WebSearchSession? web, string arg)
    {
        if (web is null)
            return "Web search is unavailable: its configuration is missing or invalid.";

        switch (arg)
        {
            case "on":
                web.Enabled = true;
                return $"Web search ON. Claude writes the queries and they leave your machine; each search costs about $0.01. " +
                       $"Allowed domains: {string.Join(", ", web.Options.AllowedDomains)}. {web.Status}";
            case "off":
                web.Enabled = false;
                return "Web search OFF.";
            case "status":
                return $"Web search is {(web.Enabled ? "ON" : "OFF")}. {web.Status}";
            default:
                return "Usage: /web on|off|status";
        }
    }

    private static List<ContentBlockParam> RunTools(ChatSession session, Message response)
    {
        var results = new List<ContentBlockParam>();
        foreach (var block in response.Content)
        {
            if (!block.TryPickToolUse(out var toolUse))
                continue;

            // content is already a string, which is what tool_result expects; the SDK serialises the block.
            var (content, isError) = ExecuteTool(session, toolUse.Name, toolUse.Input);
            results.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = content, IsError = isError });
        }
        return results;
    }

    // input arrives already deserialized by the SDK; the Get* helpers read typed values out of it.
    private static (string content, bool isError) ExecuteTool(
        ChatSession session, string toolName, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            switch (toolName)
            {
                case ToolNames.TextEditor:
                    return TextEditorToolRunner.Execute(session.Editor, input);

                case ToolNames.SetReminder:
                    string reminder = GetString(input, "content") ?? "", when = GetString(input, "timestamp") ?? "";
                    if (session.WebContentSeen && !session.Confirm($"set_reminder for {when}:\n{reminder}"))
                        return ("The user declined this reminder.", true);
                    Tools.SetReminder(reminder, when);
                    return ("Reminder set.", false);

                case ToolNames.AddDurationToDatetime:
                    var result = Tools.AddDurationToDatetime(
                        GetString(input, "datetime_str"),
                        GetDouble(input, "duration"),
                        GetString(input, "unit") ?? "days");
                    return result.Success
                        ? (Tools.FormatForDisplay(result), false)
                        : (result.Error!, true);

                case ToolNames.GetCurrentDatetime:
                    return (Tools.GetCurrentDatetime(GetString(input, "date_format")), false);

                default:
                    return ($"Error: Unknown tool '{toolName}'.", true);
            }
        }
        catch (Exception ex)
        {
            return ($"Error: {ex.Message}", true);
        }
    }

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

    internal static void AddUserMessage(List<MessageParam> messages, MessageParamContent message) =>
        messages.Add(new MessageParam { Role = Role.User, Content = message });

    internal static void AddAssistantMessage(List<MessageParam> messages, MessageParamContent message) =>
        messages.Add(new MessageParam { Role = Role.Assistant, Content = message });

    internal static List<ContentBlockParam> ToParam(Message message) =>
        message.Content.Select(block => new ContentBlockParam(block.Json)).ToList();

    private static MessageCreateParams BuildParameters(ChatConfig config, ChatSession session, WebSearchTool20250305? webTool) =>
        new()
        {
            MaxTokens = config.MaxTokens,
            Messages = session.Messages,
            Model = config.Model,
            System = webTool is null ? config.System : $"{config.System}\n\n{WebSearchOptions.SystemPromptAddendum}",
            StopSequences = config.StopSequences,
            Tools = ToolSchemas.Build(webTool),
        };

    // Status and message only: never headers, the request or the response body.
    private static string Brief(AnthropicException ex)
    {
        string message = ConsoleText.Sanitize(ex.Message);
        if (message.Length > 300) message = message[..300] + "...";
        return ex is AnthropicApiException api ? $"{(int)api.StatusCode} {message}" : message;
    }

    private static bool ConsolePrompt(string summary)
    {
        Console.WriteLine($"----\n{summary}\n----");
        Console.Write("Apply this? [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }
}
