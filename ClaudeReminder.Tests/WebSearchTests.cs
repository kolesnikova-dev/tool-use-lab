using System.Text.Json;
using Anthropic.Models.Messages;

namespace ClaudeReminder.Tests;

public class WebSearchOptionsTests
{
    private static Func<string, string?> EnvOf(params (string Key, string Value)[] vars) =>
        key => vars.Where(v => v.Key == key).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void Defaults_are_valid_and_cap_the_session()
    {
        var options = WebSearchOptions.FromEnvironment(EnvOf());

        Assert.Equal(WebSearchOptions.DefaultDomains, options.AllowedDomains);
        Assert.Equal(3, options.MaxUsesPerRequest);
        Assert.Equal(20, options.SessionSearchLimit);
    }

    [Fact]
    public void Environment_overrides_the_default_domains_and_limits()
    {
        var options = WebSearchOptions.FromEnvironment(EnvOf(
            ("WEB_SEARCH_ALLOWED_DOMAINS", " Example.COM ; docs.example.org/Blog,example.net/* "),
            ("WEB_SEARCH_MAX_USES", "2"), ("WEB_SEARCH_SESSION_LIMIT", "5")));

        Assert.Equal(["example.com", "docs.example.org/Blog", "example.net/*"], options.AllowedDomains); // host lower-cased, path untouched
        Assert.Equal(2, options.MaxUsesPerRequest);
        Assert.Equal(5, options.SessionSearchLimit);
    }

    [Fact]
    public void Duplicate_domains_are_collapsed()
    {
        var options = new WebSearchOptions(["a.com", "A.com", "b.com"]).Validated();
        Assert.Equal(["a.com", "b.com"], options.AllowedDomains);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("*.example.com")]
    [InlineData("ex*.com")]
    [InlineData("localhost")]
    [InlineData("exa mple.com")]
    [InlineData("example.com/a b")]
    [InlineData("-bad.example.com")]
    [InlineData("")]
    public void Malformed_domains_are_rejected(string domain) =>
        Assert.Throws<FormatException>(() => new WebSearchOptions([domain]).Validated());

    [Fact]
    public void Non_ascii_homograph_domains_are_rejected()
    {
        string homograph = (char)0x430 + "mazon.com"; // Cyrillic "a" that looks like Latin "a"
        Assert.Throws<FormatException>(() => new WebSearchOptions([homograph]).Validated());
    }

    [Theory]
    [InlineData("example.com/blog")]
    [InlineData("example.com/*/articles")]
    public void Paths_and_path_wildcards_are_allowed(string domain) =>
        Assert.Equal([domain], new WebSearchOptions([domain]).Validated().AllowedDomains);

    [Fact]
    public void Empty_and_oversized_lists_are_rejected()
    {
        Assert.Throws<FormatException>(() => new WebSearchOptions([]).Validated());
        var tooMany = Enumerable.Range(0, 51).Select(i => $"site{i}.com").ToList();
        Assert.Throws<FormatException>(() => new WebSearchOptions(tooMany).Validated());
    }

    [Theory]
    [InlineData("WEB_SEARCH_MAX_USES", "0")]
    [InlineData("WEB_SEARCH_MAX_USES", "11")]
    [InlineData("WEB_SEARCH_MAX_USES", "many")]
    [InlineData("WEB_SEARCH_SESSION_LIMIT", "0")]
    [InlineData("WEB_SEARCH_SESSION_LIMIT", "1001")]
    public void Bad_limits_are_rejected(string key, string value) =>
        Assert.Throws<FormatException>(() => WebSearchOptions.FromEnvironment(EnvOf((key, value))));

    [Fact]
    public void Tool_sends_only_an_allowlist_and_never_a_location_or_blocklist()
    {
        var tool = new WebSearchOptions(["a.com"], MaxUsesPerRequest: 3).ToTool(remainingSearches: 10);

        Assert.Equal(["a.com"], tool.AllowedDomains);
        Assert.Equal(3, tool.MaxUses);
        Assert.Null(tool.BlockedDomains);
        Assert.Null(tool.UserLocation);
    }

    [Fact]
    public void Tool_is_capped_by_the_remaining_session_budget() =>
        Assert.Equal(1, new WebSearchOptions(["a.com"], 3).ToTool(remainingSearches: 1).MaxUses);
}

public class WebSearchSessionTests
{
    private static Message WithSearches(int n) => Fixtures.Msg("end_turn", """[{"type":"text","text":"x"}]""", n);

    [Fact]
    public void Search_is_off_until_enabled()
    {
        var session = new WebSearchSession(new WebSearchOptions(WebSearchOptions.DefaultDomains));
        Assert.Null(session.Tool());
        session.Enabled = true;
        Assert.NotNull(session.Tool());
    }

    [Fact]
    public void Every_response_adds_to_the_count_and_the_budget_runs_out()
    {
        var session = new WebSearchSession(new WebSearchOptions(["a.com"], 3, SessionSearchLimit: 4)) { Enabled = true };

        session.Record(WithSearches(3));
        Assert.Equal(1, session.Remaining);
        Assert.Equal(1, session.Tool()!.MaxUses);

        session.Record(WithSearches(1));
        Assert.Equal(0, session.Remaining);
        Assert.Null(session.Tool());
        Assert.Equal("Searches used: 4/4.", session.Status);
    }
}

public class ConsoleTextTests
{
    [Fact]
    public void Terminal_escape_and_control_characters_are_removed()
    {
        string hostile = $"a{(char)27}[2Jb{(char)13}c{(char)0x9B}d{(char)0}e{(char)0x7F}f";
        Assert.Equal("a[2Jbcdef", ConsoleText.Sanitize(hostile)); // ESC gone, so "[2J" is inert text
    }

    [Fact]
    public void Bidi_overrides_and_line_separators_are_removed()
    {
        string hostile = $"x{(char)0x202E}y{(char)0x2066}z{(char)0x2028}w";
        Assert.Equal("xyzw", ConsoleText.Sanitize(hostile));
    }

    [Fact]
    public void Normal_text_newlines_tabs_accents_and_emoji_survive()
    {
        string text = "café ☕\n\tline two 🍵";
        Assert.Equal(text, ConsoleText.Sanitize(text));
    }

    [Fact]
    public void Null_becomes_empty() => Assert.Equal("", ConsoleText.Sanitize(null));

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void Only_http_and_https_urls_are_shown(string? url, string? expected) =>
        Assert.Equal(expected, ConsoleText.SafeUrl(url));
}

/// <summary>Builds <see cref="Message"/> objects from JSON, the way the API returns them.</summary>
internal static class Fixtures
{
    internal const string Search = """
        [
          {"type":"text","text":"I will search."},
          {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"claude shannon birth date"}},
          {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
            {"type":"web_search_result","url":"https://en.wikipedia.org/wiki/Claude_Shannon","title":"Claude Shannon - Wikipedia","encrypted_content":"ENC123","page_age":"April 30, 2025"}]},
          {"type":"text","text":"Born in 1916","citations":[
            {"type":"web_search_result_location","url":"https://en.wikipedia.org/wiki/Claude_Shannon","title":"Claude Shannon - Wikipedia","encrypted_index":"IDX1","cited_text":"Claude Elwood Shannon (April 30, 1916)"}]}
        ]
        """;

    internal static Message Msg(string stopReason, string contentJson, int searches = 0) =>
        Message.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-4-5","content":CONTENT,
             "stop_reason":"STOP","stop_sequence":null,
             "usage":{"input_tokens":1,"output_tokens":1,"server_tool_use":{"web_search_requests":SEARCHES}}}
            """.Replace("CONTENT", contentJson).Replace("STOP", stopReason).Replace("SEARCHES", searches.ToString()))!);

    internal static string Json(object value) => JsonSerializer.Serialize(value);
}

public class ResponseRendererTests
{
    [Fact]
    public void Search_response_shows_query_marker_and_sources()
    {
        string text = ResponseRenderer.Render(Fixtures.Msg("end_turn", Fixtures.Search, 1));

        Assert.Contains("Searched: \"claude shannon birth date\"", text);
        Assert.Contains("Born in 1916[1]", text);
        Assert.Contains("Sources:", text);
        Assert.Contains("[1] Claude Shannon - Wikipedia - https://en.wikipedia.org/wiki/Claude_Shannon", text);
    }

    [Fact]
    public void Repeated_citations_of_one_page_share_a_number()
    {
        string cite(string url) => $$"""{"type":"web_search_result_location","url":"{{url}}","title":"T","encrypted_index":"i","cited_text":"c"}""";
        string json = $$"""
            [{"type":"text","text":"A","citations":[{{cite("https://a.com/")}},{{cite("https://a.com/")}}]},
             {"type":"text","text":"B","citations":[{{cite("https://b.com/")}},{{cite("https://a.com/")}}]}]
            """;

        string text = ResponseRenderer.Render(Fixtures.Msg("end_turn", json));

        Assert.Contains("A[1]B[2][1]", text);
        Assert.Single(text.Split('\n'), line => line.StartsWith("[1] "));
    }

    [Fact]
    public void Citations_with_non_http_urls_are_dropped()
    {
        string json = """[{"type":"text","text":"x","citations":[{"type":"web_search_result_location","url":"javascript:alert(1)","title":"T","encrypted_index":"i","cited_text":"c"}]}]""";

        string text = ResponseRenderer.Render(Fixtures.Msg("end_turn", json));

        Assert.Equal("x", text.Trim());
    }

    [Fact]
    public void Hostile_text_titles_and_queries_are_sanitised()
    {
        string esc = ((char)27).ToString();
        string json = Fixtures.Json(new object[]
        {
            new { type = "server_tool_use", id = "s1", name = "web_search", input = new { query = $"q{esc}[2J" } },
            new { type = "text", text = $"hello{esc}[31m", citations = new[]
                { new { type = "web_search_result_location", url = "https://a.com/", title = $"T{esc}]0;x", encrypted_index = "i", cited_text = "c" } } },
        });

        string text = ResponseRenderer.Render(Fixtures.Msg("end_turn", json));

        Assert.DoesNotContain(text, c => char.IsControl(c) && c is not ('\n' or '\r'));
        Assert.Contains("hello", text);
    }

    [Theory]
    [InlineData("too_many_requests", "rate limit")]
    [InlineData("max_uses_exceeded", "search limit")]
    [InlineData("query_too_long", "too long")]
    [InlineData("invalid_tool_input", "invalid")]
    [InlineData("request_too_large", "too large")]
    [InlineData("unavailable", "unavailable")]
    public void Search_errors_are_explained(string code, string expected)
    {
        string json = """[{"type":"web_search_tool_result","tool_use_id":"s1","content":{"type":"web_search_tool_result_error","error_code":"CODE"}}]""".Replace("CODE", code);

        string text = ResponseRenderer.Render(Fixtures.Msg("end_turn", json));

        Assert.Contains("Web search failed", text);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Web_content_is_detected_only_when_pages_came_back()
    {
        Assert.True(ResponseRenderer.HasWebContent(Fixtures.Msg("end_turn", Fixtures.Search)));
        Assert.False(ResponseRenderer.HasWebContent(Fixtures.Msg("end_turn", """[{"type":"web_search_tool_result","tool_use_id":"s1","content":[]}]""")));
        Assert.False(ResponseRenderer.HasWebContent(Fixtures.Msg("end_turn", """[{"type":"text","text":"hi"}]""")));
    }
}
