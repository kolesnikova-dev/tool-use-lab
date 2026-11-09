using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ToolUseLab;

internal enum EmbedKind { Document, Query }

internal interface IEmbedder
{
    /// <summary>One vector per text, in order. Throws <see cref="RagException"/> on any failure.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind, CancellationToken ct = default);
}

/// <summary>Voyage AI embeddings over plain HTTP. The API key only ever travels in the Authorization header.</summary>
internal sealed class VoyageEmbedder(HttpClient http, string apiKey, string model, TimeSpan? retryDelay = null) : IEmbedder
{
    internal const string Endpoint = "https://api.voyageai.com/v1/embeddings";
    internal const int BatchSize = 64;
    internal const int MaxRetries = 3;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _baseDelay = retryDelay ?? TimeSpan.FromSeconds(1);

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind, CancellationToken ct = default)
    {
        var vectors = new List<float[]>(texts.Count);
        for (int i = 0; i < texts.Count; i += BatchSize)
            vectors.AddRange(await EmbedBatchAsync(texts.Skip(i).Take(BatchSize).ToArray(), kind, ct));
        return vectors.ToArray();
    }

    private async Task<float[][]> EmbedBatchAsync(string[] batch, EmbedKind kind, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new { input = batch, model, input_type = kind == EmbedKind.Query ? "query" : "document" });

        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }
            catch (HttpRequestException)
            {
                throw new RagException("Could not reach Voyage. Check your network connection.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RagException("Voyage did not answer in time.");
            }

            TimeSpan wait;
            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return Parse(await response.Content.ReadAsStringAsync(ct), batch.Length);

                int status = (int)response.StatusCode;
                if (!(status is 429 or >= 500 && attempt < MaxRetries))
                    throw new RagException(Describe(status, await ReadDetail(response, ct)));

                wait = response.Headers.RetryAfter?.Delta is { } asked ? TimeSpan.FromTicks(Math.Min(asked.Ticks, MaxWait.Ticks)) : Backoff(attempt);
            }
            await Task.Delay(wait, ct);
        }
    }

    private TimeSpan Backoff(int attempt) => _baseDelay * Math.Pow(2, attempt);

    private static string Describe(int status, string detail) => status switch
    {
        401 or 403 => $"Voyage rejected the API key (HTTP {status}). Check VOYAGE_API_KEY.",
        429 => "Voyage rate limit reached. Wait a minute and try again.",
        _ => $"Voyage error (HTTP {status}){(detail.Length > 0 ? ": " + detail : "")}",
    };

    // Voyage errors look like {"detail":"..."}; show only that, short and safe for the terminal.
    private static async Task<string> ReadDetail(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string detail = doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            detail = ConsoleText.Sanitize(detail);
            return detail.Length > 200 ? detail[..200] + "..." : detail;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    // Checks the shape we rely on: one non-empty vector per input, all the same length.
    private static float[][] Parse(string json, int expected)
    {
        var vectors = new float[expected][];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() != expected)
                throw new RagException("Voyage returned the wrong number of embeddings.");

            foreach (var item in data.EnumerateArray())
            {
                int index = item.GetProperty("index").GetInt32();
                if (index < 0 || index >= expected || vectors[index] is not null)
                    throw new RagException("Voyage returned an unexpected response.");
                vectors[index] = item.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new RagException("Voyage returned an unexpected response.");
        }

        if (vectors.Any(v => v is null || v.Length == 0) || vectors.Select(v => v.Length).Distinct().Count() > 1)
            throw new RagException("Voyage returned malformed embeddings.");
        return vectors;
    }
}
