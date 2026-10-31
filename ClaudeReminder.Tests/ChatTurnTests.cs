using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace ClaudeReminder.Tests;

public class ChatTurnTests
{
    private const string Paused = """
        [{"type":"text","text":"Searching."},
         {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"dotnet news"}},
         {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
           {"type":"web_search_result","url":"https://learn.microsoft.com/dotnet","title":"News","encrypted_content":"ENC123","page_age":"today"}]}]
        """;

    private const string Done = """[{"type":"text","text":"All done."}]""";

    private readonly List<string> _prompts = [];
    private readonly List<string> _output = [];
    private bool _approve = true;

    private ChatSession NewSession(bool web = true)
    {
        var session = new ChatSession(web ? new WebSearchOptions(["learn.microsoft.com"], SessionSearchLimit: 10) : null,
            summary => { _prompts.Add(summary); return _approve; });
        if (session.Web is { } w) w.Enabled = true;
        return session;
    }

    private static ChatConfig Config => new() { System = "base" };

    /// <summary>Scripted API: returns the queued replies in order (the last one repeats) and records what was sent.</summary>
    private sealed class FakeApi(params Message[] replies)
    {
        private int _next;
        public List<(string[] Tools, string System)> Requests { get; } = [];

        public Task<Message> Send(MessageCreateParams p)
        {
            Requests.Add((p.Tools?.Select(t => t.Json.GetRawText()).ToArray() ?? [], p.System?.Value?.ToString() ?? ""));
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }
    }

    private Task Run(ChatSession session, FakeApi api, string text = "hi") =>
        ChatService.ProcessUserTurnAsync(session, text, api.Send, Config, _output.Add);

    private static IReadOnlyList<ContentBlockParam> Blocks(MessageParam message) =>
        message.Content.TryPickContentBlockParams(out var blocks) ? blocks : throw new InvalidOperationException("not blocks");

    private static bool HasWebTool(string[] tools) => tools.Any(t => t.Contains("web_search_20250305"));

    [Fact]
    public async Task Pause_turn_continues_with_the_same_tools_and_echoes_search_blocks_unchanged()
    {
        var session = NewSession();
        var api = new FakeApi(Fixtures.Msg("pause_turn", Paused, 1), Fixtures.Msg("end_turn", Done));

        await Run(session, api);

        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, r => Assert.True(HasWebTool(r.Tools)));
        Assert.Equal(3, session.Messages.Count); // user, paused assistant, final assistant
        var echoed = Blocks(session.Messages[1]).Select(b => b.Json.GetRawText()).ToList();
        Assert.Equal(3, echoed.Count);
        Assert.Contains(echoed, b => b.Contains("ENC123") && b.Contains("web_search_tool_result"));
        Assert.Contains(echoed, b => b.Contains("srvtoolu_1") && b.Contains("server_tool_use"));
        Assert.Equal(1, session.Web!.SearchesUsed);
    }

    [Fact]
    public async Task Endless_pause_turn_stops_at_the_cap_and_rolls_the_turn_back()
    {
        var session = NewSession();
        var api = new FakeApi(Fixtures.Msg("pause_turn", Paused));

        await Run(session, api);

        Assert.Equal(1 + ChatService.MaxContinuations, api.Requests.Count);
        Assert.Empty(session.Messages);
        Assert.Contains(_output, o => o.Contains("stopped"));
    }

    [Fact]
    public async Task Max_tokens_rolls_the_turn_back()
    {
        var session = NewSession();

        await Run(session, new FakeApi(Fixtures.Msg("max_tokens", Done)));

        Assert.Empty(session.Messages);
        Assert.Contains(_output, o => o.Contains("stopped"));
    }

    [Fact]
    public async Task Api_errors_roll_the_turn_back_without_ending_the_app()
    {
        var session = NewSession();
        Task<Message> Fail(MessageCreateParams _) => throw new AnthropicIOException("network down", new HttpRequestException());

        await ChatService.ProcessUserTurnAsync(session, "hi", Fail, Config, _output.Add);

        Assert.Empty(session.Messages);
        Assert.Contains(_output, o => o.StartsWith("API error: network down"));
    }

    [Fact]
    public async Task Mixed_server_and_client_tool_turn_answers_with_only_tool_results()
    {
        const string mixed = """
            [{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"q"}},
             {"type":"tool_use","id":"toolu_1","name":"get_current_datetime","input":{}}]
            """;
        var session = NewSession();

        await Run(session, new FakeApi(Fixtures.Msg("tool_use", mixed), Fixtures.Msg("end_turn", Done)));

        Assert.Equal(4, session.Messages.Count);
        var assistant = Blocks(session.Messages[1]);
        Assert.Contains(assistant, b => b.Json.GetRawText().Contains("server_tool_use")); // the unfinished server call is kept
        var results = Blocks(session.Messages[2]);
        Assert.Single(results);
        Assert.Contains("tool_result", results[0].Json.GetRawText());
    }

    [Fact]
    public async Task Web_tool_and_prompt_addendum_are_sent_only_while_search_is_active()
    {
        var off = new FakeApi(Fixtures.Msg("end_turn", Done));
        await Run(NewSession(web: false), off);
        Assert.False(HasWebTool(off.Requests[0].Tools));
        Assert.Equal("base", off.Requests[0].System);

        var on = new FakeApi(Fixtures.Msg("end_turn", Done));
        await Run(NewSession(), on);
        Assert.True(HasWebTool(on.Requests[0].Tools));
        Assert.Contains("untrusted", on.Requests[0].System);
    }

    [Fact]
    public async Task Tool_is_dropped_once_the_session_budget_is_spent()
    {
        var session = NewSession();
        session.Web!.Record(Fixtures.Msg("end_turn", Done, searches: 10));
        var api = new FakeApi(Fixtures.Msg("end_turn", Done));

        await Run(session, api);

        Assert.False(HasWebTool(api.Requests[0].Tools));
    }

    [Fact]
    public async Task Web_content_taints_the_session_and_adds_a_warning_to_confirmations()
    {
        var session = NewSession();
        session.Confirm("before");
        Assert.DoesNotContain("WARNING", _prompts[0]);

        await Run(session, new FakeApi(Fixtures.Msg("end_turn", Fixtures.Search, 1)));
        session.Confirm("after");

        Assert.True(session.WebContentSeen);
        Assert.StartsWith("WARNING", _prompts[1]);
    }

    [Fact]
    public void Confirmation_summaries_are_sanitised()
    {
        NewSession().Confirm($"edit{(char)27}[2J");
        Assert.Equal("edit[2J", _prompts[0]);
    }

    private const string Reminder = """[{"type":"tool_use","id":"toolu_1","name":"set_reminder","input":{"content":"Call mum","timestamp":"2026-11-01T10:00:00"}}]""";

    [Fact]
    public async Task Reminders_need_no_prompt_until_web_content_is_present()
    {
        var session = NewSession();

        await Run(session, new FakeApi(Fixtures.Msg("tool_use", Reminder), Fixtures.Msg("end_turn", Done)));

        Assert.Empty(_prompts);
        Assert.DoesNotContain("\"is_error\":true", Blocks(session.Messages[2])[0].Json.GetRawText());
    }

    [Fact]
    public async Task Reminders_are_blocked_when_the_user_declines_after_web_content()
    {
        var session = NewSession();
        session.WebContentSeen = true;
        _approve = false;

        await Run(session, new FakeApi(Fixtures.Msg("tool_use", Reminder), Fixtures.Msg("end_turn", Done)));

        Assert.Single(_prompts);
        Assert.StartsWith("WARNING", _prompts[0]);
        string result = Blocks(session.Messages[2])[0].Json.GetRawText();
        Assert.Contains("\"is_error\":true", result);
        Assert.Contains("declined", result);
    }

    [Fact]
    public void Web_command_toggles_search_and_reports_usage()
    {
        var session = NewSession();
        session.Web!.Enabled = false;

        Assert.Contains("ON", ChatService.HandleCommand(session, "/web on"));
        Assert.True(session.Web.Enabled);
        Assert.Contains("0/10", ChatService.HandleCommand(session, "/web status"));
        Assert.Equal("Web search OFF.", ChatService.HandleCommand(session, "/web off"));
        Assert.False(session.Web.Enabled);
        Assert.StartsWith("Usage", ChatService.HandleCommand(session, "/web nonsense"));
    }

    [Fact]
    public void Web_command_explains_when_search_is_unavailable() =>
        Assert.Contains("unavailable", ChatService.HandleCommand(NewSession(web: false), "/web on"));
}
