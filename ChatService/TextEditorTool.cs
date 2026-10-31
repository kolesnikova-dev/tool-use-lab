using System.Text;

namespace ToolUseLab;

/// <summary>
/// An expected, model-facing failure (bad path, no match, ...). The message is sent
/// back to Claude as an <c>is_error</c> tool result, so it should explain how to recover.
/// </summary>
internal sealed class TextEditorException(string message) : Exception(message);

/// <summary>
/// A sandboxed file editor backing Anthropic's built-in text editor tool
/// (<c>text_editor_20250728</c>): view, str_replace, create and insert.
/// Every path is confined to <c>baseDir</c>, secrets and build output are off-limits,
/// and every modification is backed up first so the user (not Claude) can undo it.
/// </summary>
internal sealed class TextEditorTool
{
    internal const string BackupDirName = ".backups";

    private const long MaxFileBytes = 1_000_000;
    private const int PreviewMaxChars = 300;

    // Path segments Claude may never touch. ".env*" is matched by prefix (see IsDenied).
    private static readonly string[] DeniedSegments = [".git", BackupDirName, "bin", "obj"];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _root;
    private readonly string _backupDir;
    private readonly int _maxBackupsPerFile;
    private readonly Func<string, bool>? _confirmWrite;

    /// <param name="baseDir">Sandbox root. Defaults to the current working directory.</param>
    /// <param name="maxBackupsPerFile">How many backups to keep per file; older ones are pruned.</param>
    /// <param name="confirmWrite">
    /// Called with a human-readable summary after an edit has been validated but before it is
    /// written. Return false to veto it. When null, writes are applied without asking.
    /// </param>
    public TextEditorTool(string? baseDir = null, int maxBackupsPerFile = 5, Func<string, bool>? confirmWrite = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBackupsPerFile, 1);

        _root = Path.GetFullPath(baseDir ?? Directory.GetCurrentDirectory());
        _backupDir = Path.Combine(_root, BackupDirName);
        _maxBackupsPerFile = maxBackupsPerFile;
        _confirmWrite = confirmWrite;
    }

    // ---------- commands Claude can call ----------

    public string View(string path, int[]? viewRange = null)
    {
        string abs = Resolve(path);

        if (Directory.Exists(abs))
            return ListDirectory(abs);

        RequireFile(abs);
        List<string> lines = SplitLines(ReadFile(abs).Text);
        if (lines.Count == 0)
            return "(file is empty)";

        int start = 1;
        int end = lines.Count;
        if (viewRange is not null)
        {
            if (viewRange.Length != 2)
                throw new TextEditorException("view_range must be [start, end]; use -1 as end for end of file.");

            start = viewRange[0];
            end = viewRange[1] == -1 ? lines.Count : viewRange[1];

            if (start < 1 || start > lines.Count)
                throw new TextEditorException($"Start line {start} is out of range. File has {lines.Count} lines.");
            if (end < start)
                throw new TextEditorException($"End line {end} is before start line {start}.");

            end = Math.Min(end, lines.Count);
        }

        return NumberLines(lines, start, end);
    }

    public string StrReplace(string path, string oldStr, string newStr)
    {
        string abs = Resolve(path);
        RequireFile(abs);

        if (string.IsNullOrEmpty(oldStr))
            throw new TextEditorException("old_str must not be empty.");

        FileContent file = ReadFile(abs);
        string oldText = NormalizeNewLines(oldStr);
        string newText = NormalizeNewLines(newStr);

        int matches = CountOccurrences(file.Text, oldText);
        if (matches == 0)
            throw new TextEditorException("No match found for replacement. Check old_str (including whitespace) and try again.");
        if (matches > 1)
            throw new TextEditorException($"Found {matches} matches for old_str. Provide more surrounding context to make it unique.");

        int index = file.Text.IndexOf(oldText, StringComparison.Ordinal);
        int line = file.Text.AsSpan(0, index).Count('\n') + 1;

        Approve($"str_replace in {Display(abs)} at line {line}:\n{Preview(oldText, "- ")}\n{Preview(newText, "+ ")}");
        Backup(abs);

        string updated = string.Concat(file.Text.AsSpan(0, index), newText, file.Text.AsSpan(index + oldText.Length));
        WriteFile(abs, file with { Text = updated });

        return $"Successfully replaced text at line {line}.";
    }

    public string Create(string path, string fileText)
    {
        string abs = Resolve(path);

        if (File.Exists(abs) || Directory.Exists(abs))
            throw new TextEditorException("Path already exists. Use str_replace or insert to modify an existing file.");
        if (fileText.Length > MaxFileBytes)
            throw new TextEditorException($"File text is too large (limit is {MaxFileBytes:N0} characters).");

        Approve($"create {Display(abs)} ({SplitLines(NormalizeNewLines(fileText)).Count} lines):\n{Preview(fileText, "+ ")}");

        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, fileText, StrictUtf8);

        return $"Successfully created {Display(abs)}.";
    }

    public string Insert(string path, int insertLine, string text)
    {
        string abs = Resolve(path);
        RequireFile(abs);

        FileContent file = ReadFile(abs);
        List<string> lines = SplitLines(file.Text);

        if (insertLine < 0 || insertLine > lines.Count)
            throw new TextEditorException($"Line number {insertLine} is out of range. File has {lines.Count} lines (use 0 to insert at the top).");

        string newText = NormalizeNewLines(text);
        if (newText.EndsWith('\n'))
            newText = newText[..^1]; // we terminate lines ourselves; avoids a stray blank line

        Approve($"insert into {Display(abs)} after line {insertLine}:\n{Preview(newText, "+ ")}");
        Backup(abs);

        lines.Insert(insertLine, newText);
        string updated = string.Join('\n', lines);
        if (file.EndsWithNewLine || file.Text.Length == 0)
            updated += '\n';

        WriteFile(abs, file with { Text = updated });

        return $"Successfully inserted text after line {insertLine}.";
    }

    // ---------- user-facing maintenance (not exposed to Claude) ----------

    /// <summary>Restores the newest backup of a file and consumes it, so repeated calls walk further back.</summary>
    public string UndoEdit(string path)
    {
        string abs = Resolve(path);

        string? latest = ListBackups(abs).FirstOrDefault();
        if (latest is null)
            throw new TextEditorException($"No backups found for {Display(abs)}.");

        File.Copy(latest, abs, overwrite: true);
        File.Delete(latest);

        return $"Restored {Display(abs)} from backup ({ListBackups(abs).Count()} older backup(s) remain).";
    }

    /// <summary>Deletes every backup and returns how many files were removed.</summary>
    public int ClearBackups()
    {
        if (!Directory.Exists(_backupDir))
            return 0;

        int count = Directory.EnumerateFiles(_backupDir).Count();
        Directory.Delete(_backupDir, recursive: true);
        return count;
    }

    // ---------- sandboxing ----------

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new TextEditorException("path is required.");

        string abs = Path.GetFullPath(path, _root);
        string relative = Path.GetRelativePath(_root, abs);

        // On a different drive GetRelativePath returns the absolute path, hence IsPathRooted.
        bool escapes = Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (escapes)
            throw new TextEditorException($"Access denied: '{path}' is outside the allowed directory.");

        if (IsDenied(relative))
            throw new TextEditorException($"Access denied: '{path}' is not available to this tool.");

        return abs;
    }

    private static bool IsDenied(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.StartsWith(".env", PathComparison)
                || DeniedSegments.Any(denied => segment.Equals(denied, PathComparison)));

    private string Display(string abs) =>
        Path.GetRelativePath(_root, abs).Replace(Path.DirectorySeparatorChar, '/');

    private static void RequireFile(string abs)
    {
        if (Directory.Exists(abs))
            throw new TextEditorException("Path is a directory, not a file.");
        if (!File.Exists(abs))
            throw new TextEditorException("File not found.");
    }

    private void Approve(string summary)
    {
        if (_confirmWrite is not null && !_confirmWrite(summary))
            throw new TextEditorException("The user declined this edit. Do not retry it; ask what they would like instead.");
    }

    // ---------- reading and writing ----------

    /// <summary>File text with line endings normalised to '\n', plus what we need to write it back unchanged.</summary>
    private sealed record FileContent(string Text, string NewLine, bool HasBom)
    {
        public bool EndsWithNewLine => Text.EndsWith('\n');
    }

    private static FileContent ReadFile(string abs)
    {
        if (new FileInfo(abs).Length > MaxFileBytes)
            throw new TextEditorException($"File is too large (limit is {MaxFileBytes:N0} bytes).");

        byte[] bytes = File.ReadAllBytes(abs);
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new TextEditorException("File contains non-text content and cannot be displayed.");

        bool hasBom = bytes.AsSpan().StartsWith(Utf8WithBom.Preamble);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, hasBom ? Utf8WithBom.Preamble.Length : 0, bytes.Length - (hasBom ? Utf8WithBom.Preamble.Length : 0));
        }
        catch (DecoderFallbackException)
        {
            throw new TextEditorException("File is not valid UTF-8 text.");
        }

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new FileContent(NormalizeNewLines(text), newLine, hasBom);
    }

    private static void WriteFile(string abs, FileContent file)
    {
        string text = file.NewLine == "\n" ? file.Text : file.Text.Replace("\n", file.NewLine, StringComparison.Ordinal);
        File.WriteAllText(abs, text, file.HasBom ? Utf8WithBom : StrictUtf8);
    }

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static List<string> SplitLines(string normalizedText)
    {
        if (normalizedText.Length == 0)
            return [];

        List<string> lines = [.. normalizedText.Split('\n')];
        if (lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1); // the empty piece after a trailing newline is not a line
        return lines;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string NumberLines(List<string> lines, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i <= end; i++)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(i).Append(": ").Append(lines[i - 1]);
        }
        return sb.ToString();
    }

    private string ListDirectory(string abs)
    {
        IEnumerable<string> entries = Directory.EnumerateFileSystemEntries(abs)
            .Where(entry => !IsDenied(Path.GetRelativePath(_root, entry)))
            .Select(entry => Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : ""))
            .Order(StringComparer.OrdinalIgnoreCase);

        string listing = string.Join('\n', entries);
        return listing.Length == 0 ? "(directory is empty)" : listing;
    }

    private static string Preview(string text, string prefix)
    {
        string shown = text.Length > PreviewMaxChars ? text[..PreviewMaxChars] + "…" : text;
        return string.Join('\n', NormalizeNewLines(shown).Split('\n').Select(line => prefix + line));
    }

    // ---------- backups ----------
    // Flat files named "<escaped relative path>.<utc ticks>.bak": no collisions between
    // a/x.txt and b/x.txt, and the zero-padded ticks sort chronologically as plain strings.

    private void Backup(string abs)
    {
        Directory.CreateDirectory(_backupDir);

        string name = $"{BackupKey(abs)}.{DateTime.UtcNow.Ticks:D19}.bak";
        File.Copy(abs, Path.Combine(_backupDir, name), overwrite: true);

        foreach (string stale in ListBackups(abs).Skip(_maxBackupsPerFile))
            File.Delete(stale);
    }

    /// <summary>Backups of one file, newest first.</summary>
    private IEnumerable<string> ListBackups(string abs)
    {
        if (!Directory.Exists(_backupDir))
            return [];

        string prefix = BackupKey(abs) + ".";
        return Directory.EnumerateFiles(_backupDir, "*.bak")
            .Where(file => IsBackupOf(Path.GetFileName(file), prefix))
            .OrderByDescending(file => file, StringComparer.Ordinal)
            .ToList();
    }

    // "a.txt.5.<ticks>.bak" must not count as a backup of "a.txt": the remainder after the
    // prefix has to be exactly "<digits>.bak".
    private static bool IsBackupOf(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> rest = fileName.AsSpan(prefix.Length);
        return rest.EndsWith(".bak", StringComparison.Ordinal)
            && rest[..^4].Length > 0
            && rest[..^4].IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private string BackupKey(string abs)
    {
        string relative = Path.GetRelativePath(_root, abs);
        if (!OperatingSystem.IsWindows())
            return Uri.EscapeDataString(relative);

        return Uri.EscapeDataString(relative.ToLowerInvariant()); // Windows paths are case-insensitive
    }
}
