using System.Text.Json;

namespace ToolUseLab;

/// <summary>Translates a <c>str_replace_based_edit_tool</c> call into a <see cref="TextEditorTool"/> method call.</summary>
internal static class TextEditorToolRunner
{
    internal static (string content, bool isError) Execute(
        TextEditorTool editor, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            string path = Utils.GetString(input, "path") ?? "";

            string result = Utils.GetString(input, "command") switch
            {
                "view" => editor.View(path, Utils.GetIntArray(input, "view_range")),

                "str_replace" => editor.StrReplace(
                    path,
                    RequireString(input, "old_str"),
                    Utils.GetString(input, "new_str") ?? ""), // omitted new_str means "delete old_str"

                "create" => editor.Create(path, RequireString(input, "file_text")),

                // text_editor_20250728 sends "insert_text"; older schema versions sent "new_str".
                "insert" => editor.Insert(
                    path,
                    Utils.GetInt(input, "insert_line")
                        ?? throw new TextEditorException("Missing required parameter 'insert_line'."),
                    Utils.GetString(input, "insert_text")
                        ?? RequireString(input, "new_str")),

                "undo_edit" => throw new TextEditorException("undo_edit is not supported. Ask the user to undo with /undo."),

                var other => throw new TextEditorException(
                    $"Unknown command '{other}'. Use one of: view, str_replace, create, insert."),
            };

            return (result, false);
        }
        catch (TextEditorException ex)
        {
            return ($"Error: {ex.Message}", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"Error: Could not access the file ({ex.Message}).", true);
        }
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        Utils.GetString(input, key)
            ?? throw new TextEditorException($"Missing required parameter '{key}'.");
}
