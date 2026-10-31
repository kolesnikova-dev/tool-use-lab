using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;

namespace ClaudeReminder;

internal static class ChatService
{
    internal static async Task Chat(AnthropicClient client, ChatConfig config)
    {

        List<MessageParam> messages = new();
        var editor = new TextEditorTool(confirmWrite: ConfirmWrite);
        while (true)
        {
            Console.Write("you> ");
            string? userMessage = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(userMessage)) break;

            if (userMessage.StartsWith('/'))
            {
                HandleSlashCommand(editor, userMessage);
                continue; // local commands are never sent to Claude
            }

            AddUserMessage(messages, userMessage);

            while (true) // inner loop
            {
                // Prints Claude's text (live when streaming) and returns the complete message.
                var response = await GetResponse(client, config, messages);

                if (response.StopReason == StopReason.ToolUse)
                {
                    HandleToolUse(messages, response, editor);
                    continue; // adds assistant turn + tool_result turn
                }

                var assistantMessage = TextFromMessage(response);
                if (response.StopReason == StopReason.MaxTokens)
                    Console.WriteLine("(reply was cut off: max tokens reached)");

                // The API rejects empty text content, so only record a non-empty reply.
                if (assistantMessage.Length > 0)
                    AddAssistantMessage(messages, assistantMessage);
                break; // back to asking the user
            }
        }
    }

    private static bool ConfirmWrite(string summary)
    {
        Console.WriteLine($"----\n{summary}\n----");
        Console.Write("Apply this edit? [y/N] ");
        string? answer = Console.ReadLine()?.Trim();
        return answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static void HandleSlashCommand(TextEditorTool editor, string line)
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
                default:
                    Console.WriteLine("Commands: /undo <path>, /clean-backups");
                    break;
            }
        }
        catch (Exception ex) when (ex is TextEditorException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void HandleToolUse(List<MessageParam> messages, Message response, TextEditorTool editor)
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
        }
        AddAssistantMessage(messages, assistantBlocks);

        // 2. Execute each tool, collect results.
        var resultBlocks = new List<ContentBlockParam>();
        foreach (var tu in toolUses)
        {
            var (content, isError) = ExecuteTool(tu.Name, tu.Input, editor);
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
    private static (string content, bool isError) ExecuteTool(
        string toolName, IReadOnlyDictionary<string, JsonElement> input, TextEditorTool editor)
    {
        try
        {
            switch (toolName)
            {
                case ToolNames.TextEditor:
                    return TextEditorToolRunner.Execute(editor, input);

                case ToolNames.SetReminder:
                    Tools.SetReminder(
                        GetString(input, "content") ?? "",
                        GetString(input, "timestamp") ?? "");
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
        AnthropicClient client, ChatConfig config, List<MessageParam> messages)
    {
        var parameters = BuildParameters(config, messages);

        if (!config.IsChatStream)
        {
            var response = await client.Messages.Create(parameters);
            var text = TextFromMessage(response);
            if (text.Length > 0)
                Console.WriteLine($"claude> {text}");
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
                Console.Write(textDelta.Text);
            }
        }
        if (printedText)
            Console.WriteLine();

        return aggregator.Message();
    }

    private static MessageCreateParams BuildParameters(ChatConfig config, List<MessageParam> messages)
    {
        return new MessageCreateParams
        {
            MaxTokens = config.MaxTokens,
            Messages = messages,
            Model = config.Model,
            System = config.System,
            StopSequences = config.StopSequences,
            Tools = ToolSchemas.All,
        };
    }
}