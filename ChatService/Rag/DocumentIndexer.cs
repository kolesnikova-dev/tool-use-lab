using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToolUseLab;

/// <summary>The outcome of one indexing run. <see cref="Embedded"/> counts chunks sent to Voyage; the rest came from the cache.</summary>
internal sealed record IndexBuild(VectorIndex Index, int Files, int Embedded, IReadOnlyList<string> Warnings)
{
    public int Chunks => Index.Count;
    public string Summary => $"Indexed {Files} file(s), {Chunks} chunk(s) ({Embedded} embedded, {Chunks - Embedded} from cache).";
}

/// <summary>
/// Scans the docs folder, chunks each .md/.txt file and embeds it. A file whose SHA-256 is unchanged
/// reuses its cached vectors, so a re-run only pays for what changed.
/// </summary>
internal static class DocumentIndexer
{
    internal const long MaxFileBytes = 1_000_000;
    private const int CacheVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    internal static async Task<IndexBuild> BuildAsync(RagOptions options, IEmbedder embedder, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(options.DocsPath);
        if (!Directory.Exists(root))
            throw new RagException($"Docs folder not found: {root}");

        var warnings = new List<string>();
        var cache = LoadCache(options, warnings);
        var results = new List<(string Path, string Hash, List<DocChunk> Chunks, float[][]? Vectors)>();
        var toEmbed = new List<DocChunk>();

        foreach (string path in FindFiles(root))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string text;
            byte[] bytes;
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    warnings.Add($"Skipped {relative}: larger than {MaxFileBytes / 1000} KB.");
                    continue;
                }
                bytes = await File.ReadAllBytesAsync(path, ct);
                text = StrictUtf8.GetString(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                warnings.Add($"Skipped {relative}: {(ex is DecoderFallbackException ? "not valid UTF-8" : "cannot be read")}.");
                continue;
            }

            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (cache.TryGetValue(relative, out var cached) && cached.Hash == hash)
            {
                var chunks = cached.Chunks.Select(c => new DocChunk(relative, c.Heading, c.Text)).ToList();
                results.Add((relative, hash, chunks, cached.Chunks.Select(c => c.Vector).ToArray()));
                continue;
            }

            var fresh = ChunkerFactory.For(options.Chunking, path, options.ChunkChars, options.ChunkOverlap).Chunk(relative, text);
            toEmbed.AddRange(fresh);
            results.Add((relative, hash, fresh, null));
        }

        if (results.Sum(r => r.Chunks.Count) == 0)
            throw new RagException($"No .md or .txt content found in {root}.");

        float[][] embedded = toEmbed.Count == 0
            ? []
            : await embedder.EmbedAsync(toEmbed.Select(c => c.EmbeddingText).ToList(), EmbedKind.Document, ct);
        if (embedded.Length != toEmbed.Count)
            throw new RagException("The embedder returned the wrong number of vectors.");

        // Stitch cached and fresh vectors back together in file order.
        var indexed = new List<IndexedChunk>();
        var files = new List<CachedFile>();
        int next = 0;
        foreach (var (path, hash, chunks, vectors) in results)
        {
            var fileVectors = vectors ?? embedded[next..(next += chunks.Count)];
            indexed.AddRange(chunks.Zip(fileVectors, (c, v) => new IndexedChunk(c, v)));
            files.Add(new CachedFile(path, hash, chunks.Zip(fileVectors, (c, v) => new CachedChunk(c.Heading, c.Text, v)).ToList()));
        }

        if (embedded.Length > 0 || files.Count != cache.Count)
            SaveCache(options, files, warnings);

        return new IndexBuild(new VectorIndex(indexed), results.Count, embedded.Length, warnings);
    }

    // Sorted for a stable order. Reparse points (symlinks, junctions) are skipped so nothing outside the folder is read.
    private static IEnumerable<string> FindFiles(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        })
        .Where(p => Path.GetExtension(p) is { } ext && (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        .Order(StringComparer.Ordinal);

    // The cache is only a speed-up: anything wrong with it means "start from scratch", never a failure.
    private static Dictionary<string, CachedFile> LoadCache(RagOptions options, List<string> warnings)
    {
        if (!File.Exists(options.CachePath))
            return [];
        try
        {
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(options.CachePath));
            if (cache is null || cache.Version != CacheVersion || cache.Model != options.Model
                || cache.Chunking != options.Chunking.ToString() || cache.ChunkChars != options.ChunkChars || cache.ChunkOverlap != options.ChunkOverlap)
                return []; // settings changed: the old vectors no longer match
            return cache.Files.ToDictionary(f => f.Path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
        {
            warnings.Add("Ignored an unreadable index cache; re-embedding everything.");
            return [];
        }
    }

    // Written to a temp file and moved into place, so a crash never leaves a half-written cache.
    private static void SaveCache(RagOptions options, List<CachedFile> files, List<string> warnings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(options.CachePath));
            if (dir is not null)
                Directory.CreateDirectory(dir);
            var cache = new CacheFile(CacheVersion, options.Model, options.Chunking.ToString(), options.ChunkChars, options.ChunkOverlap, files);
            string temp = options.CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache));
            File.Move(temp, options.CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("Could not save the index cache; the next run will re-embed.");
        }
    }

    private sealed record CacheFile(int Version, string Model, string Chunking, int ChunkChars, int ChunkOverlap, List<CachedFile> Files);
    private sealed record CachedFile(string Path, string Hash, List<CachedChunk> Chunks);
    private sealed record CachedChunk(string Heading, string Text, float[] Vector);
}
