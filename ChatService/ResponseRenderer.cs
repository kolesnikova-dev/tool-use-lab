using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;

namespace ClaudeReminder;

/// <summary>Turns an API response into terminal text: queries, the answer with [n] citation markers, then a Sources list.</summary>
internal static class ResponseRenderer
{
    internal static string Render(Message response)
    {
        var sb = new StringBuilder();
        var sources = new List<(string Title, string Url)>();

        foreach (var block in response.Content)
        {
            if (block.TryPickServerToolUse(out var use) && use.Input.TryGetValue("query", out var query) && query.ValueKind == JsonValueKind.String)
                sb.AppendLine($"Searched: \"{ConsoleText.Sanitize(query.GetString())}\"");
            else if (block.TryPickWebSearchToolResult(out var result) && result.Content.TryPickError(out var error))
                sb.AppendLine($"Web search failed: {Describe(error.ErrorCode.Raw())}.");
            else if (block.TryPickText(out var text))
            {
                sb.Append(ConsoleText.Sanitize(text.Text));
                foreach (int n in CitationNumbers(text, sources).Distinct())
                    sb.Append($"[{n}]");
            }
        }

        if (sources.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine("Sources:");
            for (int i = 0; i < sources.Count; i++)
                sb.AppendLine($"[{i + 1}] {sources[i].Title} - {sources[i].Url}");
        }
        return sb.ToString();
    }

    /// <summary>True when a search actually returned pages, i.e. untrusted web text is now in the conversation.</summary>
    internal static bool HasWebContent(Message response) =>
        response.Content.Any(b => b.TryPickWebSearchToolResult(out var r)
            && r.Content.TryPickWebSearchResultBlocks(out var hits) && hits.Count > 0);

    // Numbers sources by first appearance; citations whose URL is not plain http(s) are dropped.
    private static IEnumerable<int> CitationNumbers(TextBlock text, List<(string Title, string Url)> sources)
    {
        foreach (var citation in text.Citations ?? [])
        {
            if (!citation.TryPickCitationsWebSearchResultLocation(out var location) || ConsoleText.SafeUrl(location.Url) is not { } url)
                continue;

            int index = sources.FindIndex(s => s.Url == url);
            if (index < 0)
            {
                sources.Add((ConsoleText.Sanitize(location.Title), url));
                index = sources.Count - 1;
            }
            yield return index + 1;
        }
    }

    private static string Describe(string? code) => code switch
    {
        "too_many_requests" => "rate limit reached, try again shortly",
        "max_uses_exceeded" => "search limit for this request reached",
        "query_too_long" => "the query was too long",
        "invalid_tool_input" => "the query was invalid",
        "request_too_large" => "the request was too large (check the domain list)",
        "unavailable" => "the search service is unavailable",
        _ => "unknown error",
    };
}
