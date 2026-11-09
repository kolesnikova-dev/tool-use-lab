using System.Text;

namespace ToolUseLab;

internal enum ChunkingStrategy { Auto, Markdown, Sentence, Size }

/// <summary>Raised for every expected RAG failure (config, files, Voyage, index). The message is safe to show.</summary>
internal sealed class RagException(string message) : Exception(message);

/// <summary>
/// Settings for document search. Read from the environment like <see cref="WebSearchOptions"/>; an invalid
/// value throws <see cref="FormatException"/> with a readable reason and RAG stays unavailable.
/// </summary>
internal sealed record RagOptions
{
    public required string ApiKey { get; init; }
    public string DocsPath { get; init; } = "docs";
    public int TopK { get; init; } = 4;
    public ChunkingStrategy Chunking { get; init; } = ChunkingStrategy.Auto;
    public int ChunkChars { get; init; } = 1500;
    public int ChunkOverlap { get; init; } = 200;
    public string Model { get; init; } = "voyage-3.5";
    /// <summary>Cosine similarity below this is treated as "not relevant".</summary>
    public float MinScore { get; init; } = 0.3f;
    public string CachePath { get; init; } = Path.Combine(".rag-cache", "index.json");

    // The generated record ToString would print the API key.
    private bool PrintMembers(StringBuilder builder) => false;

    /// <summary>Reads VOYAGE_API_KEY, VOYAGE_MODEL, RAG_DOCS_PATH, RAG_TOP_K, RAG_CHUNKING, RAG_CHUNK_CHARS, RAG_CHUNK_OVERLAP, RAG_MIN_SCORE.</summary>
    internal static RagOptions FromEnvironment(Func<string, string?> getEnv)
    {
        var defaults = new RagOptions { ApiKey = "" };
        return new RagOptions
        {
            ApiKey = getEnv("VOYAGE_API_KEY")?.Trim() ?? "",
            Model = Text(getEnv, "VOYAGE_MODEL", defaults.Model),
            DocsPath = Text(getEnv, "RAG_DOCS_PATH", defaults.DocsPath),
            TopK = WebSearchOptions.ReadInt(getEnv, "RAG_TOP_K", defaults.TopK),
            Chunking = ReadChunking(getEnv("RAG_CHUNKING")),
            ChunkChars = WebSearchOptions.ReadInt(getEnv, "RAG_CHUNK_CHARS", defaults.ChunkChars),
            ChunkOverlap = WebSearchOptions.ReadInt(getEnv, "RAG_CHUNK_OVERLAP", defaults.ChunkOverlap),
            MinScore = ReadScore(getEnv("RAG_MIN_SCORE"), defaults.MinScore),
        }.Validated();
    }

    internal RagOptions Validated()
    {
        if (ApiKey.Length == 0)
            throw new FormatException("VOYAGE_API_KEY is not set.");
        if (ApiKey.Any(c => c <= ' ' || c > '~'))
            throw new FormatException("VOYAGE_API_KEY contains spaces or non-ASCII characters.");
        if (Model.Any(c => c <= ' ' || c > '~'))
            throw new FormatException("VOYAGE_MODEL must be a plain ASCII model name such as voyage-3.5.");
        if (TopK is < 1 or > 10)
            throw new FormatException("RAG_TOP_K must be 1-10.");
        if (ChunkChars is < 200 or > 8000)
            throw new FormatException("RAG_CHUNK_CHARS must be 200-8000.");
        if (ChunkOverlap < 0 || ChunkOverlap >= ChunkChars / 2)
            throw new FormatException("RAG_CHUNK_OVERLAP must be 0 or more and less than half of RAG_CHUNK_CHARS.");
        if (MinScore is < 0 or > 1)
            throw new FormatException("RAG_MIN_SCORE must be between 0 and 1.");
        return this;
    }

    private static string Text(Func<string, string?> getEnv, string key, string fallback) =>
        getEnv(key) is { Length: > 0 } value ? value.Trim() : fallback;

    private static ChunkingStrategy ReadChunking(string? value) =>
        string.IsNullOrWhiteSpace(value) ? ChunkingStrategy.Auto
        : Enum.TryParse<ChunkingStrategy>(value.Trim(), ignoreCase: true, out var strategy) && Enum.IsDefined(strategy) ? strategy
        : throw new FormatException("RAG_CHUNKING must be auto, markdown, sentence or size.");

    private static float ReadScore(string? value, float fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback
        : float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float score) ? score
        : throw new FormatException("RAG_MIN_SCORE must be a number such as 0.3.");
}
