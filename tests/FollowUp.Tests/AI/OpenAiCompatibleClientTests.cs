using System.Net;
using System.Text;
using System.Text.Json;
using FollowUp.AI;
using FollowUp.AI.Llm;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

/// <summary>OpenAI-compatible client against a fake HTTP handler — no network.</summary>
public class OpenAiCompatibleClientTests
{
    private static readonly LlmJsonRequest Request = new("classify message", "system text", "user text",
        Json(new { type = "object", properties = new { category = new { type = "string" } }, required = new[] { "category" }, additionalProperties = false }), 512);

    private static (OpenAiCompatibleLlmClient Client, FakeHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new FakeHandler(status, body);
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler),
            new AiOptions { Provider = "OpenAICompatible", BaseUrl = "https://llm.example.test/v1/", Model = "local-model", ApiKey = "k" });
        return (client, handler);
    }

    private static string Completion(object message, string finish = "stop") =>
        JsonSerializer.Serialize(new { choices = new[] { new { index = 0, finish_reason = finish, message } } });

    [Fact]
    public async Task Posts_a_strict_json_schema_request_and_parses_the_answer()
    {
        var (client, handler) = Create(HttpStatusCode.OK, Completion(new { role = "assistant", content = "{\"category\":\"Blocker\"}" }));

        var json = await client.CompleteJsonAsync(Request, CancellationToken.None);

        Assert.Equal("Blocker", json.GetProperty("category").GetString());
        Assert.Equal("https://llm.example.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        var format = sent.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("classify_message", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal("local-model", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("OpenAI-compatible (local-model)", client.Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, LlmFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, LlmFailureKind.BadRequest)]
    [InlineData(HttpStatusCode.BadGateway, LlmFailureKind.ServerError)]
    public async Task Maps_http_errors(HttpStatusCode status, LlmFailureKind kind)
    {
        var (client, _) = Create(status, "{}");
        var e = await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync(Request, CancellationToken.None));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public async Task Refusal_truncation_and_invalid_json_are_failures()
    {
        var refusal = Create(HttpStatusCode.OK, Completion(new { role = "assistant", content = (string?)null, refusal = "I can't help with that." })).Client;
        Assert.Equal(LlmFailureKind.Refusal, (await Assert.ThrowsAsync<LlmException>(() => refusal.CompleteJsonAsync(Request, CancellationToken.None))).Kind);

        var truncated = Create(HttpStatusCode.OK, Completion(new { role = "assistant", content = "{\"categ" }, finish: "length")).Client;
        Assert.Equal(LlmFailureKind.InvalidResponse, (await Assert.ThrowsAsync<LlmException>(() => truncated.CompleteJsonAsync(Request, CancellationToken.None))).Kind);

        var garbage = Create(HttpStatusCode.OK, Completion(new { role = "assistant", content = "sure! here it is" })).Client;
        Assert.Equal(LlmFailureKind.InvalidResponse, (await Assert.ThrowsAsync<LlmException>(() => garbage.CompleteJsonAsync(Request, CancellationToken.None))).Kind);
    }

    [Fact]
    public void Not_configured_without_base_url_or_model()
    {
        var client = new OpenAiCompatibleLlmClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}")), new AiOptions { Model = "m" });
        Assert.False(client.IsConfigured);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
