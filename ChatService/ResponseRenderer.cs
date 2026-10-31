using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;

namespace ToolUseLab;

/// <summary>What web search adds to a response, as plain text. Used as a footer so it works the same whether or not the answer was streamed.</summary>
internal static class ResponseRenderer
{
    /// <summary>The queries Claude ran, any search errors, and the cited pages (http/https only, de-duplicated). Empty when there is nothing to show.</summary>
    internal static string Footer(Message response)
    {
        var sb = new StringBuilder();
        var sources = new OrderedDictionary<string, string>(); // url -> title

        foreach (var block in response.Content)
        {
            if (block.TryPickServerToolUse(out var use) && use.Input.TryGetValue("query", out var query) && query.ValueKind == JsonValueKind.String)
                sb.AppendLine($"Searched: \"{ConsoleText.Sanitize(query.GetString())}\"");
            else if (block.TryPickWebSearchToolResult(out var result) && result.Content.TryPickError(out var error))
                sb.AppendLine($"Web search failed: {Describe(error.ErrorCode.Raw())}.");
            else if (block.TryPickText(out var text))
                foreach (var citation in text.Citations ?? [])
                    if (citation.TryPickCitationsWebSearchResultLocation(out var page) && ConsoleText.SafeUrl(page.Url) is { } url)
                        sources.TryAdd(url, ConsoleText.Sanitize(page.Title));
        }

        if (sources.Count > 0)
        {
            sb.AppendLine("Sources:");
            foreach (var (url, title) in sources)
                sb.AppendLine($"- {title} - {url}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>True when a search actually returned pages, i.e. untrusted web text is now in the conversation.</summary>
    internal static bool HasWebContent(Message response) =>
        response.Content.Any(b => b.TryPickWebSearchToolResult(out var r)
            && r.Content.TryPickWebSearchResultBlocks(out var hits) && hits.Count > 0);

    /// <summary>True when the response contains search blocks that must be echoed back unchanged on the next request.</summary>
    internal static bool HasWebBlocks(Message response) =>
        response.Content.Any(b => b.TryPickServerToolUse(out _) || b.TryPickWebSearchToolResult(out _));

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
