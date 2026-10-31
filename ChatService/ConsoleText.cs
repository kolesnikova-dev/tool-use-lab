using System.Text;

namespace ClaudeReminder;

/// <summary>Guards the terminal against text that came from the model or the web.</summary>
internal static class ConsoleText
{
    /// <summary>
    /// Drops control characters (ESC, CR, C1, ...) and bidi/line-separator tricks that could redraw
    /// the screen or disguise what a confirmation prompt shows. Newline and tab are kept.
    /// </summary>
    internal static string Sanitize(string? text)
    {
        var sb = new StringBuilder(text?.Length ?? 0);
        foreach (char c in text ?? "")
            if (c is '\n' or '\t' || !IsUnsafe(c))
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>The normalised URL if it is an absolute http(s) URL, otherwise null. Printed as text, never opened.</summary>
    internal static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? Sanitize(uri.AbsoluteUri)
            : null;

    // Numeric on purpose: U+2028/U+2029 are line terminators and cannot safely appear as char literals in C# source.
    private static bool IsUnsafe(char c) =>
        char.IsControl(c) || (int)c is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x2028 or 0x2029;
}
