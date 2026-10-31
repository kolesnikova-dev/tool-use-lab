using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Messages;

namespace ClaudeReminder;

internal static class ChatService
{
    internal const int MaxContinuations = 5;

    internal static async Task Chat(AnthropicClient client, ChatConfig config)
    {

        List<MessageParam> messages = new();
        var web = config.WebSearch is null ? null : new WebSearchSession(config.WebSearch); // null: web search unavailable
        var editor = new TextEditorTool(confirmWrite: summary => ConfirmWrite(summary, web));
        while (true)
        {
            Console.Write("you> ");
            string? userMessage = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(userMessage)) break;

            if (userMessage.StartsWith('/'))
            {
                HandleSlashCommand(editor, web, userMessage);
                continue; // local commands are never sent to Claude
            }

            int turnStart = messages.Count;
            AddUserMessage(messages, userMessage);

            // Fixed for the whole turn: a paused turn may end on an unfinished search, and the
            // continuation must still declare the tool or the API answers 400.
            var webTool = web?.Tool();
            int pauses = 0;

            while (true) // inner loop
            {
                // Prints Claude's text (live when streaming) and returns the complete message.
                Message response;
                try
                {
                    response = await GetResponse(client, config, messages, webTool);
                }
                catch (AnthropicException ex)
                {
                    AbortTurn(messages, turnStart, ex, web);
                    break;
                }

                if (web is not null)
                {
                    web.Record(response);
                    web.WebContentSeen |= ResponseRenderer.HasWebContent(response);
                }

                if (response.StopReason == StopReason.ToolUse)
                {
                    HandleToolUse(messages, response, editor, web);
                    continue; // adds assistant turn + tool_result turn
                }

                if (response.StopReason == StopReason.PauseTurn)
                {
                    // A long search was paused by the API: send its content back unchanged to resume.
                    if (++pauses > MaxContinuations)
                    {
                        messages.RemoveRange(turnStart, messages.Count - turnStart);
                        Console.WriteLine("(stopped: the search ran too long; try a narrower question)");
                        break;
                    }
                    AddAssistantMessage(messages, ToParam(response));
                    continue;
                }

                var assistantMessage = TextFromMessage(response);
                if (response.StopReason == StopReason.MaxTokens)
                    Console.WriteLine("(reply was cut off: max tokens reached)");

                // The API rejects empty text content, so only record a non-empty reply. A reply that used web
                // search is recorded as its full blocks: the encrypted search content has to come back unchanged.
                if (assistantMessage.Length > 0)
                {
                    bool echoBlocks = ResponseRenderer.HasWebBlocks(response) && response.StopReason != StopReason.MaxTokens;
                    MessageParamContent recorded = echoBlocks ? ToParam(response) : assistantMessage;
                    AddAssistantMessage(messages, recorded);
                }
                break; // back to asking the user
            }
        }
    }

    // Drops everything added since the user's message so the history never holds a half-finished turn.
    private static void AbortTurn(List<MessageParam> messages, int turnStart, AnthropicException ex, WebSearchSession? web)
    {
        messages.RemoveRange(turnStart, messages.Count - turnStart);
        Console.WriteLine($"API error: {Brief(ex)}");

        // The organization (Console settings) can disable web search or narrow its domains.
        if (web is { Enabled: true } && ex is AnthropicBadRequestException
            && (ex.Message.Contains("web search", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("domain", StringComparison.OrdinalIgnoreCase)))
        {
            web.Enabled = false;
            Console.WriteLine("Web search was turned off because the API rejected it. Check your organization's web search settings.");
        }
    }

    // Status and message only: never headers, the request or the response body.
    private static string Brief(AnthropicException ex)
    {
        string message = ConsoleText.Sanitize(ex.Message);
        if (message.Length > 300) message = message[..300] + "...";
        return ex is AnthropicApiException api ? $"{(int)api.StatusCode} {message}" : message;
    }

    internal static bool ConfirmWrite(string summary, WebSearchSession? web)
    {
        // The summary is model-derived, so it is sanitised; once web pages were read, say so.
        string warning = web is { WebContentSeen: true }
            ? "WARNING: this conversation contains untrusted web content. Review this carefully.\n"
            : "";
        Console.WriteLine($"----\n{warning}{ConsoleText.Sanitize(summary)}\n----");
        Console.Write("Apply this edit? [y/N] ");
        string? answer = Console.ReadLine()?.Trim();
        return answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static void HandleSlashCommand(TextEditorTool editor, WebSearchSession? web, string line)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        try
        {
            switch (parts[0])
            {
                case "/undo" when parts.Length == 2 && parts[1].Length > 0:
                    Console.WriteLine(editor.UndoEdit(parts[1]));
                    break;
                case "/clean-backups":
                    Console.WriteLine($"Deleted {editor.ClearBackups()} backup file(s).");
                    break;
                case "/web":
                    Console.WriteLine(WebCommand(web, parts.Length == 2 ? parts[1] : ""));
                    break;
                default:
                    Console.WriteLine("Commands: /undo <path>, /clean-backups, /web on|off|status");
                    break;
            }
        }
        catch (Exception ex) when (ex is TextEditorException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    internal static string WebCommand(WebSearchSession? web, string arg)
    {
        if (web is null)
            return "Web search is unavailable: its configuration is missing or invalid.";

        switch (arg)
        {
            case "on":
                web.Enabled = true;
                return "Web search ON. Claude writes the queries and they leave your machine; each search costs about $0.01. " +
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

    internal static void HandleToolUse(List<MessageParam> messages, Message response, TextEditorTool editor, WebSearchSession? web)
    {
        // Collect any tool_use blocks in this response.
        var toolUses = new List<ToolUseBlock>();
        foreach (var block in response.Content)
            if (block.TryPickToolUse(out var tu))
                toolUses.Add(tu);
        // 1. Echo Claude's assistant turn, including every tool_use block.
        var assistantBlocks = new List<ContentBlockParam>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var tb))
                assistantBlocks.Add(new TextBlockParam { Text = tb.Text });
            else if (block.TryPickToolUse(out var tu))
                assistantBlocks.Add(new ToolUseBlockParam
                {
                    ID = tu.ID,
                    Name = tu.Name,
                    Input = tu.Input,
                });
            else if (block.TryPickServerToolUse(out _) || block.TryPickWebSearchToolResult(out _))
                assistantBlocks.Add(new ContentBlockParam(block.Json)); // search blocks must go back unchanged
        }
        AddAssistantMessage(messages, assistantBlocks);

        // 2. Execute each tool, collect results.
        var resultBlocks = new List<ContentBlockParam>();
        foreach (var tu in toolUses)
        {
            var (content, isError) = ExecuteTool(tu.Name, tu.Input, editor, web);
            // content is already a string, which is what tool_result expects, and the SDK
            // serializes this block to JSON. (A tool returning an object would need
            // JsonSerializer.Serialize first, the equivalent of Python's json.dumps.)
            resultBlocks.Add(new ToolResultBlockParam
            {
                ToolUseID = tu.ID,
                Content = content,
                IsError = isError,
            });
        }
        // 3. Add the tool results as a user turn, then loop to ask Claude again.
        AddUserMessage(messages, resultBlocks);
    }

    // input arrives already deserialized by the SDK (no json.loads equivalent needed);
    // the GetString/GetDouble helpers just read typed values out of it.
    internal static (string content, bool isError) ExecuteTool(
        string toolName, IReadOnlyDictionary<string, JsonElement> input, TextEditorTool editor, WebSearchSession? web)
    {
        try
        {
            switch (toolName)
            {
                case ToolNames.TextEditor:
                    return TextEditorToolRunner.Execute(editor, input);

                case ToolNames.SetReminder:
                    string reminder = Utils.GetString(input, "content") ?? "", when = Utils.GetString(input, "timestamp") ?? "";
                    // Once web pages were read, a page could have talked Claude into this: ask first.
                    if (web is { WebContentSeen: true } && !ConfirmWrite($"set_reminder for {when}:\n{reminder}", web))
                        return ("The user declined this reminder.", true);
                    Tools.SetReminder(reminder, when);
                    return ("Reminder set.", false);

                case ToolNames.AddDurationToDatetime:
                    var result = Tools.AddDurationToDatetime(
                        Utils.GetString(input, "datetime_str"),
                        Utils.GetDouble(input, "duration"),
                        Utils.GetString(input, "unit") ?? "days");
                    return result.Success
                        ? (Tools.FormatForDisplay(result), false)
                        : (result.Error!, true);

                case ToolNames.GetCurrentDatetime:
                    return (Tools.GetCurrentDatetime(Utils.GetString(input, "date_format")), false);

                default:
                    return ($"Error: Unknown tool '{toolName}'.", true);
            }
        }
        catch (Exception ex)
        {
            return ($"Error: {ex.Message}", true);
        }
    }

    internal static void AddUserMessage(List<MessageParam> messages, MessageParamContent message) =>
       messages.Add(new MessageParam { Role = Role.User, Content = message });

    internal static void AddAssistantMessage(List<MessageParam> messages, MessageParamContent message) =>
        messages.Add(new MessageParam { Role = Role.Assistant, Content = message });

    internal static string TextFromMessage(Message message)
    {
        var reply = new StringBuilder();
        foreach (var block in message.Content)
            if (block.TryPickText(out var tb))
                reply.Append(tb.Text);

        return reply.ToString();
    }

    // The only place that differs between streaming and non-streaming. Both paths print
    // Claude's text and return a complete Message, so the rest of the loop is identical.
    // The SDK serializes the typed request (messages, tools, system) to JSON and
    // deserializes the response, so no manual JSON handling is needed here.
    private static async Task<Message> GetResponse(
        AnthropicClient client, ChatConfig config, List<MessageParam> messages, WebSearchTool20250305? webTool)
    {
        var parameters = BuildParameters(config, messages, webTool);

        if (!config.IsChatStream)
        {
            var response = await client.Messages.Create(parameters);
            var text = TextFromMessage(response);
            if (text.Length > 0)
                Console.WriteLine($"claude> {ConsoleText.Sanitize(text)}");
            PrintWebFooter(response);
            return response;
        }

        // MessageContentAggregator rebuilds the full Message from the stream events
        // (including a tool call's partial JSON), while we print text deltas as they arrive.
        var aggregator = new MessageContentAggregator();
        bool printedText = false;
        await foreach (var ev in client.Messages.CreateStreaming(parameters).CollectAsync(aggregator))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var textDelta))
            {
                if (!printedText)
                {
                    Console.Write("claude> ");
                    printedText = true;
                }
                Console.Write(ConsoleText.Sanitize(textDelta.Text));
            }
        }
        if (printedText)
            Console.WriteLine();

        var message = aggregator.Message();
        PrintWebFooter(message);
        return message;
    }

    // The queries Claude ran and the sources it cited. Printed after the answer in both modes,
    // because a streamed answer is already on screen. Empty (so silent) when no search happened.
    private static void PrintWebFooter(Message response)
    {
        string footer = ResponseRenderer.Footer(response);
        if (footer.Length > 0)
            Console.WriteLine(footer);
    }

    internal static List<ContentBlockParam> ToParam(Message message) =>
        message.Content.Select(block => new ContentBlockParam(block.Json)).ToList();

    private static MessageCreateParams BuildParameters(ChatConfig config, List<MessageParam> messages, WebSearchTool20250305? webTool)
    {
        return new MessageCreateParams
        {
            MaxTokens = config.MaxTokens,
            Messages = messages,
            Model = config.Model,
            System = webTool is null ? config.System : $"{config.System}\n\n{WebSearchOptions.SystemPromptAddendum}",
            StopSequences = config.StopSequences,
            Tools = ToolSchemas.Build(webTool),
        };
    }
}