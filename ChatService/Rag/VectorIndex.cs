using System.Numerics.Tensors;

namespace ToolUseLab;

internal sealed record IndexedChunk(DocChunk Chunk, float[] Vector);

internal sealed record SearchHit(DocChunk Chunk, float Score);

/// <summary>Brute-force cosine search. A few thousand chunks take well under a millisecond, so no ANN structure is needed.</summary>
internal sealed class VectorIndex(IReadOnlyList<IndexedChunk> chunks)
{
    public int Count => chunks.Count;

    public IReadOnlyList<SearchHit> Search(float[] query, int topK, float minScore)
    {
        var hits = new List<SearchHit>();
        foreach (var indexed in chunks)
        {
            if (indexed.Vector.Length != query.Length)
                throw new RagException("The index was built with a different embedding size. Run /rag reindex.");

            float score = TensorPrimitives.CosineSimilarity(indexed.Vector, query);
            if (score >= minScore)
                hits.Add(new SearchHit(indexed.Chunk, score));
        }
        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }
}
