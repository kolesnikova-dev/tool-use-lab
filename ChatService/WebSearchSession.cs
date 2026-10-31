using Anthropic.Models.Messages;

namespace ToolUseLab;

/// <summary>Per-session on/off switch and search budget. max_uses only caps one request, so the session counts every response.</summary>
internal sealed class WebSearchSession(WebSearchOptions options)
{
    public WebSearchOptions Options { get; } = options;
    public bool Enabled { get; set; }
    public int SearchesUsed { get; private set; }

    /// <summary>Set once a search returned pages; from then on side effects get a warning (and reminders a prompt).</summary>
    public bool WebContentSeen { get; set; }

    public int Remaining => Math.Max(0, Options.SessionSearchLimit - SearchesUsed);
    public string Status => $"Searches used: {SearchesUsed}/{Options.SessionSearchLimit}.";

    /// <summary>
    /// Counts the searches in a response (pause_turn continuations included). Counting blocks rather than
    /// reading usage keeps it correct when the message was rebuilt from a stream.
    /// </summary>
    public void Record(Message response) => SearchesUsed += response.Content.Count(b => b.TryPickServerToolUse(out _));

    /// <summary>The tool to send, or null when search is off or the budget is spent.</summary>
    public WebSearchTool20250305? Tool() => Enabled && Remaining > 0 ? Options.ToTool(Remaining) : null;
}
