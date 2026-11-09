using System.Text.Json;
using Anthropic.Models.Messages;

namespace ToolUseLab.Tests;

/// <summary>
/// The few places web search touches the existing chat code. They read the console, so they share one
/// class (xUnit runs a class's tests one at a time) and swap Console.In/Out for the duration of each test.
/// </summary>
public sealed class WebSearchHookTests : IDisposable
{
    private readonly TextReader _in = Console.In;
    private readonly TextWriter _out = Console.Out;
    private readonly StringWriter _output = new();

    public WebSearchHookTests() => Console.SetOut(_output);

    public void Dispose()
    {
        Console.SetIn(_in);
        Console.SetOut(_out);
    }

    private static WebSearchSession NewWeb(bool tainted = false) =>
        new(new WebSearchOptions(["learn.microsoft.com"])) { Enabled = true, WebContentSeen = tainted };

    private static IReadOnlyList<ContentBlockParam> Blocks(MessageParam message) =>
        message.Content.TryPickContentBlockParams(out var blocks) ? blocks : throw new InvalidOperationException("not blocks");

    private const string SearchThenTool = """
        [{"type":"text","text":"Searching."},
         {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"q"}},
         {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
           {"type":"web_search_result","url":"https://learn.microsoft.com/dotnet","title":"News","encrypted_content":"ENC123","page_age":"today"}]},
         {"type":"tool_use","id":"toolu_1","name":"get_current_datetime","input":{}}]
        """;

    // ---------- HandleToolUse ----------

    [Fact]
    public async Task Tool_turns_keep_search_blocks_verbatim_next_to_the_tool_call()
    {
        var messages = new List<MessageParam>();

        await ChatService.HandleToolUse(messages, Fixtures.Msg("tool_use", SearchThenTool), new TextEditorTool(), NewWeb());

        Assert.Equal(2, messages.Count); // assistant echo, then the tool results
        var echoed = Blocks(messages[0]).Select(b => b.Json.GetRawText()).ToList();
        Assert.Equal(4, echoed.Count);
        Assert.Contains(echoed, b => b.Contains("server_tool_use") && b.Contains("srvtoolu_1"));
        Assert.Contains(echoed, b => b.Contains("web_search_tool_result") && b.Contains("ENC123")); // encrypted content untouched
        var results = Blocks(messages[1]);
        Assert.Single(results);
        Assert.Contains("tool_result", results[0].Json.GetRawText());
    }

    [Fact]
    public async Task Tool_turns_without_search_are_echoed_exactly_as_before()
    {
        const string plain = """[{"type":"text","text":"Checking."},{"type":"tool_use","id":"toolu_1","name":"get_current_datetime","input":{}}]""";
        var messages = new List<MessageParam>();

        await ChatService.HandleToolUse(messages, Fixtures.Msg("tool_use", plain), new TextEditorTool(), web: null);

        Assert.Equal(2, Blocks(messages[0]).Count);
    }

    [Fact]
    public void ToParam_round_trips_search_content_unchanged()
    {
        var echoed = ChatService.ToParam(Fixtures.Msg("end_turn", Fixtures.Search)).Select(b => b.Json.GetRawText()).ToList();

        Assert.Equal(4, echoed.Count);
        Assert.Contains(echoed, b => b.Contains("ENC123"));
        Assert.Contains(echoed, b => b.Contains("IDX1")); // the citation's encrypted_index
    }

    // ---------- reminders and confirmations after web content ----------

    private static Dictionary<string, JsonElement> ReminderInput() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"content":"Call mum","timestamp":"2026-11-01T10:00:00"}""")!;

    [Fact]
    public async Task Reminders_run_without_a_prompt_until_web_content_is_present()
    {
        Console.SetIn(new StringReader("")); // a prompt would read EOF and decline

        var (content, isError) = await ChatService.ExecuteTool(ToolNames.SetReminder, ReminderInput(), new TextEditorTool(), NewWeb(tainted: false));

        Assert.False(isError);
        Assert.Equal("Reminder set.", content);
        Assert.DoesNotContain("Apply this", _output.ToString());
    }

    [Fact]
    public async Task Reminders_after_web_content_ask_first_and_can_be_declined()
    {
        Console.SetIn(new StringReader("n\n"));

        var (content, isError) = await ChatService.ExecuteTool(ToolNames.SetReminder, ReminderInput(), new TextEditorTool(), NewWeb(tainted: true));

        Assert.True(isError);
        Assert.Contains("declined", content);
        Assert.Contains("WARNING", _output.ToString());
        Assert.DoesNotContain("Setting the following reminder", _output.ToString());
    }

    [Fact]
    public async Task Reminders_after_web_content_run_when_approved()
    {
        Console.SetIn(new StringReader("y\n"));

        var (content, isError) = await ChatService.ExecuteTool(ToolNames.SetReminder, ReminderInput(), new TextEditorTool(), NewWeb(tainted: true));

        Assert.False(isError);
        Assert.Equal("Reminder set.", content);
    }

    [Fact]
    public void Confirmation_warns_only_after_web_content_and_strips_control_codes()
    {
        Console.SetIn(new StringReader("y\n"));
        string summary = $"edit{(char)27}[2J";

        Assert.True(ChatService.ConfirmWrite(summary, web: null));
        Assert.DoesNotContain("WARNING", _output.ToString());

        Console.SetIn(new StringReader("y\n"));
        Assert.True(ChatService.ConfirmWrite(summary, NewWeb(tainted: true)));

        string shown = _output.ToString();
        Assert.Contains("WARNING", shown);
        Assert.Contains("edit[2J", shown);
        Assert.DoesNotContain(shown, c => char.IsControl(c) && c is not ('\n' or '\r'));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("maybe")]
    public void Anything_but_yes_declines(string answer)
    {
        Console.SetIn(new StringReader(answer + "\n"));
        Assert.False(ChatService.ConfirmWrite("edit", NewWeb()));
    }

    // ---------- /web ----------

    [Fact]
    public void Web_command_toggles_search_and_reports_usage()
    {
        var web = new WebSearchSession(new WebSearchOptions(["a.com"], SessionSearchLimit: 7));

        Assert.Contains("OFF", ChatService.WebCommand(web, "status"));
        Assert.Contains("ON", ChatService.WebCommand(web, "on"));
        Assert.True(web.Enabled);
        Assert.Contains("0/7", ChatService.WebCommand(web, "status"));
        Assert.Equal("Web search OFF.", ChatService.WebCommand(web, "off"));
        Assert.False(web.Enabled);
        Assert.StartsWith("Usage", ChatService.WebCommand(web, "nonsense"));
    }

    [Fact]
    public void Web_command_says_so_when_search_is_unavailable() =>
        Assert.Contains("unavailable", ChatService.WebCommand(null, "on"));

    // ---------- tool list ----------

    [Fact]
    public void The_search_tool_is_declared_only_when_given()
    {
        int baseCount = ToolSchemas.All.Count;

        Assert.Equal(baseCount, ToolSchemas.Build(null).Count);

        var tools = ToolSchemas.Build(new WebSearchOptions(["a.com"]).ToTool(5));
        Assert.Equal(baseCount + 1, tools.Count);
        Assert.Contains("web_search_20250305", tools[^1].Json.GetRawText());
    }
}
