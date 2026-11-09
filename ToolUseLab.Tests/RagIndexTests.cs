using System.Text.Json;

namespace ToolUseLab.Tests;

/// <summary>Maps text to a 3-d vector by keyword, so "apple" and "banana" questions land on different chunks.</summary>
internal sealed class FakeEmbedder : IEmbedder
{
    public List<string> Embedded { get; } = [];
    public bool Fail { get; set; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind, CancellationToken ct = default)
    {
        if (Fail)
            throw new RagException("embedder down");
        Embedded.AddRange(texts);
        return Task.FromResult(texts.Select(Vector).ToArray());
    }

    private static float[] Vector(string t) =>
    [
        t.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
        t.Contains("banana", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
        0.1f,
    ];
}

/// <summary>A temp docs folder and cache location, deleted afterwards.</summary>
internal sealed class RagWorkspace : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rag-tests-" + Guid.NewGuid().ToString("N"));
    public string Docs => Path.Combine(_root, "docs");
    public string Cache => Path.Combine(_root, "cache", "index.json");

    public RagWorkspace() => Directory.CreateDirectory(Docs);

    public RagOptions Options(Func<RagOptions, RagOptions>? tweak = null)
    {
        var options = new RagOptions { ApiKey = "k", DocsPath = Docs, CachePath = Cache };
        return tweak is null ? options : tweak(options);
    }

    public string Write(string name, string text)
    {
        string path = Path.Combine(Docs, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

public class VectorIndexTests
{
    private static IndexedChunk Chunk(string text, params float[] v) => new(new DocChunk("a.md", "", text), v);

    [Fact]
    public void Results_are_ranked_by_similarity_and_limited_to_top_k()
    {
        var index = new VectorIndex([Chunk("far", 0, 1), Chunk("close", 1, 0.1f), Chunk("exact", 1, 0)]);

        var hits = index.Search([1, 0], topK: 2, minScore: 0);

        Assert.Equal(["exact", "close"], hits.Select(h => h.Chunk.Text));
        Assert.Equal(1f, hits[0].Score, 3);
    }

    [Fact]
    public void Hits_below_the_minimum_score_are_dropped()
    {
        var index = new VectorIndex([Chunk("far", 0, 1), Chunk("exact", 1, 0)]);

        Assert.Equal(["exact"], index.Search([1, 0], 5, minScore: 0.5f).Select(h => h.Chunk.Text));
    }

    [Fact]
    public void An_empty_index_finds_nothing() => Assert.Empty(new VectorIndex([]).Search([1, 0], 3, 0));

    [Fact]
    public void A_dimension_mismatch_asks_for_a_reindex()
    {
        var index = new VectorIndex([Chunk("x", 1, 0, 0)]);

        var ex = Assert.Throws<RagException>(() => index.Search([1, 0], 3, 0));
        Assert.Contains("/rag reindex", ex.Message);
    }
}

public class DocumentIndexerTests
{
    [Fact]
    public async Task Indexes_md_and_txt_files_and_ignores_the_rest()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# Apples\napple facts");
        ws.Write("sub/b.txt", "Bananas are yellow. They are long.");
        ws.Write("c.pdf", "ignored");
        var embedder = new FakeEmbedder();

        var build = await DocumentIndexer.BuildAsync(ws.Options(), embedder);

        Assert.Equal(2, build.Files);
        Assert.Equal(2, build.Chunks);
        Assert.Equal(2, build.Embedded);
        Assert.Empty(build.Warnings);
        Assert.Contains("Apples\n\napple facts", embedder.Embedded); // the heading is part of what is embedded
    }

    [Fact]
    public async Task A_second_run_uses_the_cache_and_embeds_nothing()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());
        var second = new FakeEmbedder();

        var build = await DocumentIndexer.BuildAsync(ws.Options(), second);

        Assert.Empty(second.Embedded);
        Assert.Equal(0, build.Embedded);
        Assert.Equal(1, build.Chunks);
        Assert.Single(build.Index.Search([1, 0, 0.1f], 1, 0.5f)); // cached vectors still answer queries
    }

    [Fact]
    public async Task Only_the_changed_file_is_embedded_again()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        string b = ws.Write("b.md", "# B\nbanana");
        await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());
        File.WriteAllText(b, "# B\nbanana split");
        var embedder = new FakeEmbedder();

        var build = await DocumentIndexer.BuildAsync(ws.Options(), embedder);

        Assert.Equal(["B\n\nbanana split"], embedder.Embedded);
        Assert.Equal(2, build.Chunks);
    }

    [Fact]
    public async Task Deleted_files_leave_the_index()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        string b = ws.Write("b.md", "# B\nbanana");
        await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());
        File.Delete(b);

        var build = await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());

        Assert.Equal(1, build.Files);
        Assert.Empty(build.Index.Search([0, 1, 0.1f], 5, 0.9f));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("chunking")]
    [InlineData("size")]
    public async Task Changing_the_model_or_chunking_settings_invalidates_the_cache(string change)
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());
        var embedder = new FakeEmbedder();
        var changed = ws.Options(o => change switch
        {
            "model" => o with { Model = "other-model" },
            "chunking" => o with { Chunking = ChunkingStrategy.Size },
            _ => o with { ChunkChars = 900 },
        });

        var build = await DocumentIndexer.BuildAsync(changed, embedder);

        Assert.Equal(build.Chunks, build.Embedded);
    }

    [Fact]
    public async Task A_corrupt_cache_is_ignored_with_a_warning()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.Cache)!);
        File.WriteAllText(ws.Cache, "{ not json");

        var build = await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());

        Assert.Equal(1, build.Embedded);
        Assert.Contains(build.Warnings, w => w.Contains("cache"));
        using var rewritten = JsonDocument.Parse(File.ReadAllText(ws.Cache)); // and replaced by a valid one
    }

    [Fact]
    public async Task An_unwritable_cache_location_is_only_a_warning()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        string blocker = Path.Combine(Path.GetDirectoryName(ws.Docs)!, "blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");

        var build = await DocumentIndexer.BuildAsync(ws.Options(o => o with { CachePath = Path.Combine(blocker, "index.json") }), new FakeEmbedder());

        Assert.Equal(1, build.Chunks);
        Assert.Contains(build.Warnings, w => w.Contains("save"));
    }

    [Fact]
    public async Task Unreadable_and_oversized_files_are_skipped_with_a_warning_naming_them()
    {
        using var ws = new RagWorkspace();
        ws.Write("good.md", "# Good\napple");
        File.WriteAllBytes(Path.Combine(ws.Docs, "binary.txt"), [0xFF, 0xFE, 0xFD, 0x80]);
        ws.Write("huge.md", new string('x', (int)DocumentIndexer.MaxFileBytes + 1));

        var build = await DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder());

        Assert.Equal(1, build.Files);
        Assert.Contains(build.Warnings, w => w.Contains("binary.txt") && w.Contains("UTF-8"));
        Assert.Contains(build.Warnings, w => w.Contains("huge.md"));
    }

    [Fact]
    public async Task A_missing_docs_folder_is_a_readable_error()
    {
        using var ws = new RagWorkspace();

        var ex = await Assert.ThrowsAsync<RagException>(() =>
            DocumentIndexer.BuildAsync(ws.Options(o => o with { DocsPath = Path.Combine(ws.Docs, "nope") }), new FakeEmbedder()));

        Assert.Contains("Docs folder not found", ex.Message);
    }

    [Fact]
    public async Task A_folder_without_documents_is_a_readable_error()
    {
        using var ws = new RagWorkspace();
        ws.Write("image.png", "x");

        var ex = await Assert.ThrowsAsync<RagException>(() => DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder()));

        Assert.Contains("No .md or .txt content", ex.Message);
    }

    [Fact]
    public async Task An_embedder_failure_propagates_and_writes_no_cache()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");

        await Assert.ThrowsAsync<RagException>(() => DocumentIndexer.BuildAsync(ws.Options(), new FakeEmbedder { Fail = true }));

        Assert.False(File.Exists(ws.Cache));
    }
}

public class RagSessionTests
{
    private static async Task<(RagSession Rag, RagWorkspace Ws, FakeEmbedder Embedder)> Ready()
    {
        var ws = new RagWorkspace();
        ws.Write("fruit.md", "# Apples\nAll about apple trees.\n\n# Bananas\nAll about banana plants.");
        var embedder = new FakeEmbedder();
        var rag = new RagSession(ws.Options(o => o with { MinScore = 0.8f, TopK = 1 }), embedder);
        await rag.ReindexAsync();
        return (rag, ws, embedder);
    }

    [Fact]
    public async Task Search_returns_the_best_passage_with_its_citation()
    {
        var (rag, ws, _) = await Ready();
        using var _ws = ws;

        string result = await rag.SearchAsync("Tell me about apple trees");

        Assert.StartsWith("[1] fruit.md > Apples (score ", result);
        Assert.Contains("All about apple trees.", result);
        Assert.DoesNotContain("banana", result);
    }

    [Fact]
    public async Task No_passage_above_the_cutoff_says_so_instead_of_returning_noise()
    {
        var (rag, ws, _) = await Ready();
        using var _ws = ws;

        Assert.Equal("No relevant passages found in the documents.", await rag.SearchAsync("quantum chromodynamics"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_query_is_an_error(string? query)
    {
        var (rag, ws, _) = await Ready();
        using var _ws = ws;

        await Assert.ThrowsAsync<RagException>(() => rag.SearchAsync(query));
    }

    [Fact]
    public async Task An_overlong_query_is_an_error()
    {
        var (rag, ws, embedder) = await Ready();
        using var _ws = ws;
        int before = embedder.Embedded.Count;

        await Assert.ThrowsAsync<RagException>(() => rag.SearchAsync(new string('q', RagSession.MaxQueryChars + 1)));

        Assert.Equal(before, embedder.Embedded.Count); // rejected before any network call
    }

    [Fact]
    public async Task Searching_before_indexing_is_an_error()
    {
        using var ws = new RagWorkspace();
        var rag = new RagSession(ws.Options(), new FakeEmbedder());

        var ex = await Assert.ThrowsAsync<RagException>(() => rag.SearchAsync("apple"));

        Assert.Contains("/rag on", ex.Message);
    }

    [Fact]
    public async Task A_failed_reindex_keeps_the_previous_index()
    {
        var (rag, ws, embedder) = await Ready();
        using var _ws = ws;
        var before = rag.Current;
        ws.Write("new.md", "# New\nnew apple text");
        embedder.Fail = true;

        await Assert.ThrowsAsync<RagException>(() => rag.ReindexAsync());

        Assert.Same(before, rag.Current);
        embedder.Fail = false;
        Assert.Contains("fruit.md", await rag.SearchAsync("apple trees"));
    }
}

public class RagCommandAndToolTests
{
    private static RagSession Session(RagWorkspace ws, FakeEmbedder? embedder = null) =>
        new(ws.Options(o => o with { MinScore = 0.8f }), embedder ?? new FakeEmbedder());

    private static Dictionary<string, JsonElement> Query(string q) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { query = q }))!;

    [Fact]
    public async Task On_builds_the_index_and_enables_search_then_off_disables_it()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        var rag = Session(ws);

        Assert.Contains("OFF", await ChatService.RagCommand(rag, "status"));
        string on = await ChatService.RagCommand(rag, "on");

        Assert.True(rag.Enabled);
        Assert.Contains("RAG ON", on);
        Assert.Contains("Voyage", on); // the user is told their text leaves the machine
        Assert.Contains("Indexed 1 file(s)", on);
        Assert.Contains("ON", await ChatService.RagCommand(rag, "status"));
        Assert.Equal("RAG OFF.", await ChatService.RagCommand(rag, "off"));
        Assert.False(rag.Enabled);
    }

    [Fact]
    public async Task On_twice_does_not_embed_again()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        var embedder = new FakeEmbedder();
        var rag = Session(ws, embedder);
        await ChatService.RagCommand(rag, "on");
        int embedded = embedder.Embedded.Count;

        await ChatService.RagCommand(rag, "off");
        await ChatService.RagCommand(rag, "on");

        Assert.Equal(embedded, embedder.Embedded.Count);
    }

    [Fact]
    public async Task Reindex_picks_up_a_changed_file_and_reports_warnings()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        var rag = Session(ws);
        await ChatService.RagCommand(rag, "on");
        ws.Write("a.md", "# A\napple pie");
        File.WriteAllBytes(Path.Combine(ws.Docs, "bad.txt"), [0xFF, 0xFE, 0x80]);

        string report = await ChatService.RagCommand(rag, "reindex");

        Assert.Contains("1 embedded", report);
        Assert.Contains("Warning: Skipped bad.txt", report);
    }

    [Fact]
    public async Task A_failed_on_leaves_rag_off_and_explains_why()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        var rag = Session(ws, new FakeEmbedder { Fail = true });

        string result = await ChatService.RagCommand(rag, "on");

        Assert.False(rag.Enabled);
        Assert.Contains("RAG error: embedder down", result);
    }

    [Fact]
    public async Task A_failed_reindex_reports_that_the_old_index_is_still_in_use()
    {
        using var ws = new RagWorkspace();
        ws.Write("a.md", "# A\napple");
        var embedder = new FakeEmbedder();
        var rag = Session(ws, embedder);
        await ChatService.RagCommand(rag, "on");
        ws.Write("a.md", "# A\napple changed");
        embedder.Fail = true;

        string result = await ChatService.RagCommand(rag, "reindex");

        Assert.Contains("previous index is still in use", result);
        Assert.True(rag.Enabled);
    }

    [Fact]
    public async Task Usage_and_unavailable_messages()
    {
        using var ws = new RagWorkspace();

        Assert.StartsWith("Usage", await ChatService.RagCommand(Session(ws), "nonsense"));
        Assert.Contains("unavailable", await ChatService.RagCommand(null, "on"));
    }

    [Fact]
    public async Task The_tool_returns_cited_passages_when_rag_is_on()
    {
        using var ws = new RagWorkspace();
        ws.Write("fruit.md", "# Apples\nAll about apple trees.");
        var rag = Session(ws);
        await ChatService.RagCommand(rag, "on");

        var (content, isError) = await ChatService.ExecuteTool(ToolNames.SearchDocuments, Query("apple trees"), new TextEditorTool(), web: null, rag);

        Assert.False(isError);
        Assert.Contains("[1] fruit.md > Apples", content);
        Assert.Contains("All about apple trees.", content);
    }

    [Fact]
    public async Task The_tool_reports_no_match_as_a_normal_result()
    {
        using var ws = new RagWorkspace();
        ws.Write("fruit.md", "# Apples\napple");
        var rag = Session(ws);
        await ChatService.RagCommand(rag, "on");

        var (content, isError) = await ChatService.ExecuteTool(ToolNames.SearchDocuments, Query("unrelated"), new TextEditorTool(), web: null, rag);

        Assert.False(isError);
        Assert.Contains("No relevant passages", content);
    }

    [Fact]
    public async Task The_tool_errors_for_an_empty_query_an_embedder_failure_or_when_off()
    {
        using var ws = new RagWorkspace();
        ws.Write("fruit.md", "# Apples\napple");
        var embedder = new FakeEmbedder();
        var rag = Session(ws, embedder);
        var editor = new TextEditorTool();

        var off = await ChatService.ExecuteTool(ToolNames.SearchDocuments, Query("apple"), editor, web: null, rag);
        Assert.True(off.isError);
        Assert.Contains("/rag on", off.content);
        Assert.True((await ChatService.ExecuteTool(ToolNames.SearchDocuments, Query("apple"), editor, web: null, rag: null)).isError);

        await ChatService.RagCommand(rag, "on");
        Assert.True((await ChatService.ExecuteTool(ToolNames.SearchDocuments, Query("  "), editor, web: null, rag)).isError);

        embedder.Fail = true;
        var failed = await ChatService.ExecuteTool(ToolNames.SearchDocuments, Query("apple"), editor, web: null, rag);
        Assert.True(failed.isError);
        Assert.Contains("embedder down", failed.content);
    }

    [Fact]
    public void The_search_tool_is_declared_only_when_asked_for()
    {
        int baseCount = ToolSchemas.All.Count;

        Assert.Equal(baseCount, ToolSchemas.Build(null).Count);
        var tools = ToolSchemas.Build(null, documentSearch: true);
        Assert.Equal(baseCount + 1, tools.Count);
        Assert.Contains("search_documents", tools[^1].Json.GetRawText());
    }
}
