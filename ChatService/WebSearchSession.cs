using Anthropic.Models.Messages;

namespace ClaudeReminder;

/// <summary>Per-session on/off switch and search budget. max_uses only caps one request, so the session counts every response.</summary>
internal sealed class WebSearchSession(WebSearchOptions options)
{
    public WebSearchOptions Options { get; } = options;
    public bool Enabled { get; set; }
    public int SearchesUsed { get; private set; }

    public int Remaining => Math.Max(0, Options.SessionSearchLimit - SearchesUsed);
    public string Status => $"Searches used: {SearchesUsed}/{Options.SessionSearchLimit}.";

    /// <summary>Adds the searches the API reports for this response (pause_turn continuations included).</summary>
    public void Record(Message response) => SearchesUsed += (int)(response.Usage?.ServerToolUse?.WebSearchRequests ?? 0);

    /// <summary>The tool to send, or null when search is off or the budget is spent.</summary>
    public WebSearchTool20250305? Tool() => Enabled && Remaining > 0 ? Options.ToTool(Remaining) : null;
}
