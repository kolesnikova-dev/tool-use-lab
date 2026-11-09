using System.Net;
using System.Text;

namespace ToolUseLab.Tests;

public class RagOptionsTests
{
    private static Func<string, string?> EnvOf(params (string Key, string Value)[] vars) =>
        key => vars.Where(v => v.Key == key).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void Defaults_apply_when_only_the_key_is_set()
    {
        var o = RagOptions.FromEnvironment(EnvOf(("VOYAGE_API_KEY", "pa-123")));

        Assert.Equal("pa-123", o.ApiKey);
        Assert.Equal("docs", o.DocsPath);
        Assert.Equal(4, o.TopK);
        Assert.Equal(ChunkingStrategy.Auto, o.Chunking);
        Assert.Equal(1500, o.ChunkChars);
        Assert.Equal(200, o.ChunkOverlap);
        Assert.Equal("voyage-3.5", o.Model);
    }

    [Fact]
    public void Environment_overrides_every_setting()
    {
        var o = RagOptions.FromEnvironment(EnvOf(
            ("VOYAGE_API_KEY", " k "), ("VOYAGE_MODEL", "voyage-3-large"), ("RAG_DOCS_PATH", "notes"), ("RAG_TOP_K", "7"),
            ("RAG_CHUNKING", "Sentence"), ("RAG_CHUNK_CHARS", "800"), ("RAG_CHUNK_OVERLAP", "50"), ("RAG_MIN_SCORE", "0.45")));

        Assert.Equal("k", o.ApiKey);
        Assert.Equal("voyage-3-large", o.Model);
        Assert.Equal("notes", o.DocsPath);
        Assert.Equal(7, o.TopK);
        Assert.Equal(ChunkingStrategy.Sentence, o.Chunking);
        Assert.Equal(800, o.ChunkChars);
        Assert.Equal(50, o.ChunkOverlap);
        Assert.Equal(0.45f, o.MinScore);
    }

    [Theory]
    [InlineData("VOYAGE_API_KEY", "", "VOYAGE_API_KEY is not set")]
    [InlineData("VOYAGE_API_KEY", "bad key", "VOYAGE_API_KEY")]
    [InlineData("RAG_TOP_K", "0", "RAG_TOP_K")]
    [InlineData("RAG_TOP_K", "many", "RAG_TOP_K")]
    [InlineData("RAG_CHUNKING", "paragraph", "RAG_CHUNKING")]
    [InlineData("RAG_CHUNK_CHARS", "10", "RAG_CHUNK_CHARS")]
    [InlineData("RAG_CHUNK_OVERLAP", "900", "RAG_CHUNK_OVERLAP")]
    [InlineData("RAG_MIN_SCORE", "2", "RAG_MIN_SCORE")]
    [InlineData("RAG_MIN_SCORE", "high", "RAG_MIN_SCORE")]
    [InlineData("VOYAGE_MODEL", "has space", "VOYAGE_MODEL")]
    public void Bad_values_give_a_readable_reason(string key, string value, string expected)
    {
        var env = EnvOf((key, value), ("VOYAGE_API_KEY", "k")); // first match wins, so the value under test goes first

        var ex = Assert.Throws<FormatException>(() => RagOptions.FromEnvironment(env));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ToString_never_prints_the_key() =>
        Assert.DoesNotContain("secret-key", new RagOptions { ApiKey = "secret-key" }.ToString());
}

public class VoyageEmbedderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];
        public List<string?> AuthHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            return respond(request, Calls++);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Embeddings(int count, int dims = 3) =>
        "{\"data\":[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $"{{\"index\":{i},\"embedding\":[{string.Join(",", Enumerable.Repeat((i + 1).ToString() + ".5", dims))}]}}")) + "]}";

    private static VoyageEmbedder Embedder(StubHandler handler, string key = "SECRET-KEY") =>
        new(new HttpClient(handler), key, "voyage-3.5", TimeSpan.Zero);

    [Fact]
    public async Task Sends_the_model_input_type_and_bearer_key()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, Embeddings(2)));

        var vectors = await Embedder(handler).EmbedAsync(["a", "b"], EmbedKind.Query);

        Assert.Equal(2, vectors.Length);
        Assert.Equal(1.5f, vectors[0][0]);
        Assert.Contains("\"input_type\":\"query\"", handler.Bodies[0]);
        Assert.Contains("\"model\":\"voyage-3.5\"", handler.Bodies[0]);
        Assert.Equal("Bearer SECRET-KEY", handler.AuthHeaders[0]);
        Assert.DoesNotContain("SECRET-KEY", handler.Bodies[0]);
    }

    [Fact]
    public async Task Large_inputs_are_sent_in_batches_and_keep_their_order()
    {
        var handler = new StubHandler((req, _) =>
        {
            int n = System.Text.Json.JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("input").GetArrayLength();
            return Json(HttpStatusCode.OK, Embeddings(n));
        });

        var vectors = await Embedder(handler).EmbedAsync(Enumerable.Range(0, 150).Select(i => $"t{i}").ToList(), EmbedKind.Document);

        Assert.Equal(150, vectors.Length);
        Assert.Equal(3, handler.Calls); // 64 + 64 + 22
        Assert.Contains("\"input_type\":\"document\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task A_rate_limit_is_retried_and_then_succeeds()
    {
        var handler = new StubHandler((_, call) => call < 2 ? Json((HttpStatusCode)429, "{}") : Json(HttpStatusCode.OK, Embeddings(1)));

        var vectors = await Embedder(handler).EmbedAsync(["a"], EmbedKind.Document);

        Assert.Single(vectors);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_persistent_server_error_gives_up_after_the_retries()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{\"detail\":\"boom\"}"));

        var ex = await Assert.ThrowsAsync<RagException>(() => Embedder(handler).EmbedAsync(["a"], EmbedKind.Document));

        Assert.Equal(1 + VoyageEmbedder.MaxRetries, handler.Calls);
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_rejected_key_is_not_retried_and_never_echoed(int status)
    {
        var handler = new StubHandler((_, _) => Json((HttpStatusCode)status, "{\"detail\":\"Provided API key is invalid.\"}"));

        var ex = await Assert.ThrowsAsync<RagException>(() => Embedder(handler).EmbedAsync(["a"], EmbedKind.Document));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("VOYAGE_API_KEY", ex.Message);
        Assert.DoesNotContain("SECRET-KEY", ex.Message);
    }

    [Fact]
    public async Task A_bad_request_shows_the_detail_without_retrying()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.BadRequest, "{\"detail\":\"Too many tokens\"}"));

        var ex = await Assert.ThrowsAsync<RagException>(() => Embedder(handler).EmbedAsync(["a"], EmbedKind.Document));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("Too many tokens", ex.Message);
    }

    [Fact]
    public async Task A_network_failure_becomes_a_readable_error()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused to api.voyageai.com"));

        var ex = await Assert.ThrowsAsync<RagException>(() => Embedder(handler).EmbedAsync(["a"], EmbedKind.Document));

        Assert.Contains("Could not reach Voyage", ex.Message);
        Assert.Equal(1 + VoyageEmbedder.MaxRetries, handler.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"nodata\":1}")]
    [InlineData("{\"data\":[{\"index\":5,\"embedding\":[1]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[]}]}")]
    public async Task A_malformed_response_is_rejected(string json)
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, json));

        await Assert.ThrowsAsync<RagException>(() => Embedder(handler).EmbedAsync(["a"], EmbedKind.Document));
    }
}
