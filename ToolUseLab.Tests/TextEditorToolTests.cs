using System.Text.Json;
using ToolUseLab;

namespace ToolUseLab.Tests;

public sealed class TextEditorToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "text-editor-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _confirmations = [];
    private bool _approve = true;

    public TextEditorToolTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private TextEditorTool NewEditor(int maxBackups = 5) =>
        new(_root, maxBackups, summary => { _confirmations.Add(summary); return _approve; });

    private string PathOf(string name) => Path.Combine(_root, name);
    private string Write(string name, string content, bool createDir = false)
    {
        string path = PathOf(name);
        if (createDir) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------- sandbox ----------

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("..")]
    public void Paths_escaping_the_root_are_rejected(string path)
    {
        var ex = Assert.Throws<TextEditorException>(() => NewEditor().View(path));
        Assert.Contains("outside the allowed directory", ex.Message);
    }

    [Fact]
    public void Absolute_path_outside_the_root_is_rejected()
    {
        string outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");
        Assert.Throws<TextEditorException>(() => NewEditor().View(outside));
    }

    [Fact]
    public void Sibling_directory_sharing_the_root_prefix_is_rejected()
    {
        // The notebook's startswith() check would let "<root>-evil" through.
        string sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "x");
        try
        {
            Assert.Throws<TextEditorException>(() => NewEditor().View(Path.Combine(sibling, "secret.txt")));
        }
        finally { Directory.Delete(sibling, recursive: true); }
    }

    [Fact]
    public void Absolute_path_inside_the_root_is_allowed()
    {
        string file = Write("a.txt", "hello");
        Assert.Equal("1: hello", NewEditor().View(file));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("sub/.env")]
    [InlineData(".git/config")]
    [InlineData("bin/app.dll")]
    [InlineData("obj/x.cs")]
    [InlineData(".backups/anything.bak")]
    public void Denied_paths_are_rejected_for_every_command(string path)
    {
        Write(path, "secret", createDir: true);
        var editor = NewEditor();

        Assert.Throws<TextEditorException>(() => editor.View(path));
        Assert.Throws<TextEditorException>(() => editor.StrReplace(path, "secret", "x"));
        Assert.Throws<TextEditorException>(() => editor.Insert(path, 0, "x"));
        Assert.Throws<TextEditorException>(() => editor.Create(path + ".new", "x"));
        Assert.Equal("secret", File.ReadAllText(PathOf(path)));
    }

    [Fact]
    public void Environment_like_names_that_are_not_dotenv_are_allowed()
    {
        Write("environment.txt", "ok");
        Assert.Equal("1: ok", NewEditor().View("environment.txt"));
    }

    [Fact]
    public void Blank_path_is_rejected() =>
        Assert.Throws<TextEditorException>(() => NewEditor().View("  "));

    // ---------- view ----------

    [Fact]
    public void View_numbers_every_line_and_ignores_the_trailing_newline()
    {
        Write("a.txt", "one\ntwo\nthree\n");
        Assert.Equal("1: one\n2: two\n3: three", NewEditor().View("a.txt"));
    }

    [Fact]
    public void View_range_returns_inclusive_lines_with_original_numbers()
    {
        Write("a.txt", "one\ntwo\nthree\nfour");
        Assert.Equal("2: two\n3: three", NewEditor().View("a.txt", [2, 3]));
    }

    [Fact]
    public void View_range_with_minus_one_runs_to_the_end()
    {
        Write("a.txt", "one\ntwo\nthree");
        Assert.Equal("2: two\n3: three", NewEditor().View("a.txt", [2, -1]));
    }

    [Fact]
    public void View_range_end_past_the_file_is_clamped()
    {
        Write("a.txt", "one\ntwo");
        Assert.Equal("1: one\n2: two", NewEditor().View("a.txt", [1, 99]));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(5, 6)]
    [InlineData(3, 2)]
    public void Invalid_view_ranges_are_rejected(int start, int end)
    {
        Write("a.txt", "one\ntwo\nthree");
        Assert.Throws<TextEditorException>(() => NewEditor().View("a.txt", [start, end]));
    }

    [Fact]
    public void View_range_must_have_two_elements()
    {
        Write("a.txt", "one");
        Assert.Throws<TextEditorException>(() => NewEditor().View("a.txt", [1]));
    }

    [Fact]
    public void View_of_an_empty_file_says_so()
    {
        Write("empty.txt", "");
        Assert.Equal("(file is empty)", NewEditor().View("empty.txt"));
    }

    [Fact]
    public void View_of_a_directory_lists_entries_and_hides_denied_ones()
    {
        Write("b.txt", "x");
        Write("a.txt", "x");
        Directory.CreateDirectory(PathOf("docs"));
        Write(".env", "secret");
        Directory.CreateDirectory(PathOf(".git"));
        Directory.CreateDirectory(PathOf("bin"));

        Assert.Equal("a.txt\nb.txt\ndocs/", NewEditor().View("."));
    }

    [Fact]
    public void View_of_a_missing_file_fails() =>
        Assert.Throws<TextEditorException>(() => NewEditor().View("nope.txt"));

    [Fact]
    public void View_of_a_binary_file_fails()
    {
        File.WriteAllBytes(PathOf("data.bin"), [0x50, 0x00, 0x4B]);
        var ex = Assert.Throws<TextEditorException>(() => NewEditor().View("data.bin"));
        Assert.Contains("non-text", ex.Message);
    }

    [Fact]
    public void View_of_invalid_utf8_fails()
    {
        File.WriteAllBytes(PathOf("latin1.txt"), [0x63, 0x61, 0x66, 0xE9]); // "café" in Latin-1
        Assert.Throws<TextEditorException>(() => NewEditor().View("latin1.txt"));
    }

    // ---------- str_replace ----------

    [Fact]
    public void StrReplace_replaces_a_unique_match_and_reports_the_line()
    {
        string file = Write("a.txt", "alpha\nbeta\ngamma\n");

        string result = NewEditor().StrReplace("a.txt", "beta", "BETA");

        Assert.Equal("alpha\nBETA\ngamma\n", File.ReadAllText(file));
        Assert.Contains("line 2", result);
    }

    [Fact]
    public void StrReplace_with_no_match_fails_and_leaves_the_file_alone()
    {
        string file = Write("a.txt", "alpha");

        var ex = Assert.Throws<TextEditorException>(() => NewEditor().StrReplace("a.txt", "zzz", "x"));

        Assert.Contains("No match", ex.Message);
        Assert.Equal("alpha", File.ReadAllText(file));
        Assert.Empty(_confirmations); // validation fails before the user is bothered
    }

    [Fact]
    public void StrReplace_with_several_matches_fails_and_reports_the_count()
    {
        string file = Write("a.txt", "x y x y x");

        var ex = Assert.Throws<TextEditorException>(() => NewEditor().StrReplace("a.txt", "x", "z"));

        Assert.Contains("3 matches", ex.Message);
        Assert.Equal("x y x y x", File.ReadAllText(file));
    }

    [Fact]
    public void StrReplace_rejects_empty_old_str()
    {
        Write("a.txt", "alpha");
        Assert.Throws<TextEditorException>(() => NewEditor().StrReplace("a.txt", "", "x"));
    }

    [Fact]
    public void StrReplace_with_empty_new_str_deletes_the_match()
    {
        string file = Write("a.txt", "keep REMOVE keep");
        NewEditor().StrReplace("a.txt", " REMOVE", "");
        Assert.Equal("keep keep", File.ReadAllText(file));
    }

    [Fact]
    public void StrReplace_treats_dollar_and_backslash_literally()
    {
        string file = Write("a.txt", "price");
        NewEditor().StrReplace("a.txt", "price", @"$1 \n $&");
        Assert.Equal(@"$1 \n $&", File.ReadAllText(file));
    }

    [Fact]
    public void StrReplace_matches_lf_input_against_a_crlf_file_and_keeps_crlf()
    {
        string file = Write("crlf.txt", "one\r\ntwo\r\nthree\r\n");

        NewEditor().StrReplace("crlf.txt", "one\ntwo", "ONE\nTWO");

        Assert.Equal("ONE\r\nTWO\r\nthree\r\n", File.ReadAllText(file));
    }

    [Fact]
    public void StrReplace_preserves_a_utf8_bom()
    {
        string file = PathOf("bom.txt");
        File.WriteAllText(file, "hello", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        NewEditor().StrReplace("bom.txt", "hello", "world");

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("world", File.ReadAllText(file));
    }

    [Fact]
    public void StrReplace_handles_non_ascii_text()
    {
        string file = Write("a.txt", "café ☕");
        NewEditor().StrReplace("a.txt", "☕", "🍵");
        Assert.Equal("café 🍵", File.ReadAllText(file));
    }

    // ---------- create ----------

    [Fact]
    public void Create_writes_the_file_and_makes_parent_directories()
    {
        NewEditor().Create("deep/er/new.txt", "hi\n");
        Assert.Equal("hi\n", File.ReadAllText(PathOf("deep/er/new.txt")));
    }

    [Fact]
    public void Create_refuses_to_overwrite_an_existing_file()
    {
        string file = Write("a.txt", "original");
        Assert.Throws<TextEditorException>(() => NewEditor().Create("a.txt", "new"));
        Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public void Create_refuses_to_replace_a_directory()
    {
        Directory.CreateDirectory(PathOf("dir"));
        Assert.Throws<TextEditorException>(() => NewEditor().Create("dir", "x"));
    }

    // ---------- insert ----------

    [Theory]
    [InlineData(0, "NEW\none\ntwo\n")]
    [InlineData(1, "one\nNEW\ntwo\n")]
    [InlineData(2, "one\ntwo\nNEW\n")]
    public void Insert_places_text_after_the_given_line(int line, string expected)
    {
        string file = Write("a.txt", "one\ntwo\n");
        NewEditor().Insert("a.txt", line, "NEW");
        Assert.Equal(expected, File.ReadAllText(file));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Insert_out_of_range_fails_without_creating_a_backup(int line)
    {
        Write("a.txt", "one\ntwo\n");
        Assert.Throws<TextEditorException>(() => NewEditor().Insert("a.txt", line, "NEW"));
        Assert.False(Directory.Exists(PathOf(TextEditorTool.BackupDirName)));
    }

    [Fact]
    public void Insert_at_the_end_of_a_file_without_trailing_newline_does_not_add_one()
    {
        string file = Write("a.txt", "one\ntwo");
        NewEditor().Insert("a.txt", 2, "three");
        Assert.Equal("one\ntwo\nthree", File.ReadAllText(file));
    }

    [Fact]
    public void Insert_does_not_leave_a_blank_line_when_the_text_ends_with_a_newline()
    {
        string file = Write("a.txt", "one\ntwo\n");
        NewEditor().Insert("a.txt", 1, "NEW\n");
        Assert.Equal("one\nNEW\ntwo\n", File.ReadAllText(file));
    }

    [Fact]
    public void Insert_into_an_empty_file_works()
    {
        string file = Write("a.txt", "");
        NewEditor().Insert("a.txt", 0, "first");
        Assert.Equal("first\n", File.ReadAllText(file));
    }

    [Fact]
    public void Insert_supports_multi_line_text_and_keeps_crlf()
    {
        string file = Write("a.txt", "one\r\ntwo\r\n");
        NewEditor().Insert("a.txt", 1, "x\ny");
        Assert.Equal("one\r\nx\r\ny\r\ntwo\r\n", File.ReadAllText(file));
    }

    // ---------- confirmation ----------

    [Fact]
    public void Declined_edits_change_nothing_and_say_so()
    {
        _approve = false;
        string file = Write("a.txt", "alpha");
        var editor = NewEditor();

        var replace = Assert.Throws<TextEditorException>(() => editor.StrReplace("a.txt", "alpha", "beta"));
        Assert.Throws<TextEditorException>(() => editor.Insert("a.txt", 0, "x"));
        Assert.Throws<TextEditorException>(() => editor.Create("new.txt", "x"));

        Assert.Contains("declined", replace.Message);
        Assert.Equal("alpha", File.ReadAllText(file));
        Assert.False(File.Exists(PathOf("new.txt")));
        Assert.False(Directory.Exists(PathOf(TextEditorTool.BackupDirName)));
    }

    [Fact]
    public void Confirmation_summary_describes_the_edit()
    {
        Write("a.txt", "alpha\nbeta");
        NewEditor().StrReplace("a.txt", "beta", "gamma");

        string summary = Assert.Single(_confirmations);
        Assert.Contains("a.txt", summary);
        Assert.Contains("- beta", summary);
        Assert.Contains("+ gamma", summary);
    }

    [Fact]
    public void View_never_asks_for_confirmation()
    {
        Write("a.txt", "alpha");
        NewEditor().View("a.txt");
        Assert.Empty(_confirmations);
    }

    [Fact]
    public void Editor_without_a_confirm_callback_applies_edits_directly()
    {
        string file = Write("a.txt", "alpha");
        new TextEditorTool(_root).StrReplace("a.txt", "alpha", "beta");
        Assert.Equal("beta", File.ReadAllText(file));
    }

    // ---------- backups and undo ----------

    [Fact]
    public void Undo_restores_the_previous_content_and_consumes_the_backup()
    {
        string file = Write("a.txt", "v1");
        var editor = NewEditor();
        editor.StrReplace("a.txt", "v1", "v2");
        editor.StrReplace("a.txt", "v2", "v3");

        editor.UndoEdit("a.txt");
        Assert.Equal("v2", File.ReadAllText(file));
        editor.UndoEdit("a.txt");
        Assert.Equal("v1", File.ReadAllText(file));

        Assert.Throws<TextEditorException>(() => editor.UndoEdit("a.txt"));
    }

    [Fact]
    public void Undo_without_any_backup_fails()
    {
        Write("a.txt", "v1");
        Assert.Throws<TextEditorException>(() => NewEditor().UndoEdit("a.txt"));
    }

    [Fact]
    public void Undo_does_not_mix_up_files_with_the_same_name_in_different_folders()
    {
        string x = Write("a/same.txt", "A1", createDir: true);
        string y = Write("b/same.txt", "B1", createDir: true);
        var editor = NewEditor();
        editor.StrReplace("a/same.txt", "A1", "A2");
        editor.StrReplace("b/same.txt", "B1", "B2");

        editor.UndoEdit("a/same.txt");

        Assert.Equal("A1", File.ReadAllText(x));
        Assert.Equal("B2", File.ReadAllText(y));
    }

    [Fact]
    public void Undo_does_not_pick_up_backups_of_a_file_with_a_similar_name()
    {
        Write("a.txt", "1");
        Write("a.txt.5", "other");
        var editor = NewEditor();
        editor.StrReplace("a.txt.5", "other", "changed");

        Assert.Throws<TextEditorException>(() => editor.UndoEdit("a.txt"));
    }

    [Fact]
    public void Only_the_newest_backups_are_kept()
    {
        Write("a.txt", "0");
        var editor = NewEditor(maxBackups: 2);
        for (int i = 0; i < 4; i++)
            editor.StrReplace("a.txt", i.ToString(), (i + 1).ToString());

        Assert.Equal(2, Directory.GetFiles(PathOf(TextEditorTool.BackupDirName)).Length);

        editor.UndoEdit("a.txt");
        editor.UndoEdit("a.txt");
        Assert.Equal("2", File.ReadAllText(PathOf("a.txt"))); // oldest two were pruned
        Assert.Throws<TextEditorException>(() => editor.UndoEdit("a.txt"));
    }

    [Fact]
    public void ClearBackups_removes_everything_and_reports_the_count()
    {
        Write("a.txt", "1");
        Write("b.txt", "1");
        var editor = NewEditor();
        editor.StrReplace("a.txt", "1", "2");
        editor.StrReplace("b.txt", "1", "2");

        Assert.Equal(2, editor.ClearBackups());
        Assert.False(Directory.Exists(PathOf(TextEditorTool.BackupDirName)));
        Assert.Equal(0, editor.ClearBackups());
    }

    [Fact]
    public void Maximum_backup_count_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextEditorTool(_root, maxBackupsPerFile: 0));

    // ---------- runner (JSON input -> editor) ----------

    private static Dictionary<string, JsonElement> Input(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Runner_dispatches_view_with_a_range()
    {
        Write("a.txt", "one\ntwo\nthree");

        var (content, isError) = TextEditorToolRunner.Execute(
            NewEditor(), Input("""{"command":"view","path":"a.txt","view_range":[2,3]}"""));

        Assert.False(isError);
        Assert.Equal("2: two\n3: three", content);
    }

    [Fact]
    public void Runner_dispatches_str_replace_create_and_insert()
    {
        var editor = NewEditor();

        Assert.False(TextEditorToolRunner.Execute(editor,
            Input("""{"command":"create","path":"a.txt","file_text":"one\ntwo\n"}""")).isError);
        Assert.False(TextEditorToolRunner.Execute(editor,
            Input("""{"command":"insert","path":"a.txt","insert_line":1,"insert_text":"mid"}""")).isError);
        Assert.False(TextEditorToolRunner.Execute(editor,
            Input("""{"command":"str_replace","path":"a.txt","old_str":"two","new_str":"TWO"}""")).isError);

        Assert.Equal("one\nmid\nTWO\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public void Runner_accepts_the_legacy_new_str_name_for_insert()
    {
        Write("a.txt", "one\n");

        var (_, isError) = TextEditorToolRunner.Execute(
            NewEditor(), Input("""{"command":"insert","path":"a.txt","insert_line":0,"new_str":"zero"}"""));

        Assert.False(isError);
        Assert.Equal("zero\none\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public void Runner_accepts_a_whole_number_sent_as_a_float()
    {
        Write("a.txt", "one\n");

        var (_, isError) = TextEditorToolRunner.Execute(
            NewEditor(), Input("""{"command":"insert","path":"a.txt","insert_line":1.0,"insert_text":"two"}"""));

        Assert.False(isError);
        Assert.Equal("one\ntwo\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Theory]
    [InlineData("""{"command":"str_replace","path":"a.txt","new_str":"x"}""", "old_str")]
    [InlineData("""{"command":"create","path":"b.txt"}""", "file_text")]
    [InlineData("""{"command":"insert","path":"a.txt","insert_text":"x"}""", "insert_line")]
    [InlineData("""{"command":"insert","path":"a.txt","insert_line":0}""", "new_str")]
    [InlineData("""{"command":"nonsense","path":"a.txt"}""", "Unknown command")]
    [InlineData("""{"path":"a.txt"}""", "Unknown command")]
    [InlineData("""{"command":"undo_edit","path":"a.txt"}""", "not supported")]
    [InlineData("""{"command":"view"}""", "path is required")]
    public void Runner_reports_bad_input_as_an_error_result(string json, string expectedFragment)
    {
        Write("a.txt", "one\n");

        var (content, isError) = TextEditorToolRunner.Execute(NewEditor(), Input(json));

        Assert.True(isError);
        Assert.Contains(expectedFragment, content);
    }

    [Fact]
    public void Runner_turns_a_declined_edit_into_an_error_result()
    {
        _approve = false;
        Write("a.txt", "one\n");

        var (content, isError) = TextEditorToolRunner.Execute(
            NewEditor(), Input("""{"command":"str_replace","path":"a.txt","old_str":"one","new_str":"two"}"""));

        Assert.True(isError);
        Assert.Contains("declined", content);
        Assert.Equal("one\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public void Runner_turns_sandbox_violations_into_error_results()
    {
        var (content, isError) = TextEditorToolRunner.Execute(
            NewEditor(), Input("""{"command":"view","path":"../../etc/passwd"}"""));

        Assert.True(isError);
        Assert.Contains("Access denied", content);
    }
}
