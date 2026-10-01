using System.Net;
using System.Text;
using System.Text.Json;

namespace Codev.Tests;

public sealed class OllamaStructuredPlanClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434");
    private static readonly ChatMessage[] Messages = [new("system", "Read-only planner."), new("user", "Add a setting")];
    private const string ValidPlan = """{"objective":"Add a setting.","steps":[{"title":"Update preferences","details":"Add the persisted option.","files":["Settings.cs"]}],"risks":["Migration may be needed."],"questions":[]}""";

    [Fact]
    public async Task Sends_schema_and_returns_a_formatted_plan_with_token_count()
    {
        var payloadCallbackCompleted = false;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.True(payloadCallbackCompleted, "The last-request context callback should complete before sending the request.");
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = payload.RootElement;
            Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("steps", root.GetProperty("format").GetProperty("required")[1].GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.True(root.GetProperty("think").GetBoolean());
            Assert.Equal(4096, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Contains("JSON object matching", root.GetProperty("messages")[0].GetProperty("content").GetString());
            return Json(new { message = new { content = ValidPlan }, prompt_eval_count = 123 });
        }));

        var result = await new OllamaStructuredPlanClient(http, Endpoint).CreatePlanAsync("local-model", Messages,
            new Dictionary<string, object> { ["num_ctx"] = 4096 }, think: true, onRequestPayload: body =>
            {
                Assert.Contains("\"format\"", body, StringComparison.Ordinal);
                payloadCallbackCompleted = true;
                return Task.CompletedTask;
            });

        Assert.True(result.UsedStructuredOutput);
        Assert.Equal(123, result.PromptTokens);
        Assert.Contains("## Objective", result.Markdown);
        Assert.Contains("1. **Update preferences**", result.Markdown);
        Assert.Contains("`Settings.cs`", result.Markdown);
        Assert.Contains("## Risks", result.Markdown);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"objective\":\"Do work\",\"steps\":[],\"risks\":[],\"questions\":[]}")]
    [InlineData("{\"objective\":\"Do work\",\"steps\":[{\"title\":\"T\",\"details\":\"D\",\"files\":[],\"unexpected\":true}],\"risks\":[],\"questions\":[]}")]
    public async Task Invalid_structured_plan_falls_back_to_plain_markdown(string invalidPlan)
    {
        var requests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            requests++;
            if (requests == 1)
            {
                Assert.True(payload.RootElement.TryGetProperty("format", out _));
                return Json(new { message = new { content = invalidPlan } });
            }
            Assert.False(payload.RootElement.TryGetProperty("format", out _));
            Assert.Contains("plain text or Markdown", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Json(new { message = new { content = "1. Implement the setting." }, prompt_eval_count = 77 });
        }));

        var result = await new OllamaStructuredPlanClient(http, Endpoint).CreatePlanAsync("local-model", Messages, null, think: false);

        Assert.False(result.UsedStructuredOutput);
        Assert.Equal("1. Implement the setting.", result.Markdown);
        Assert.Equal(77, result.PromptTokens);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Unsupported_schema_retries_but_other_http_errors_propagate()
    {
        var requests = 0;
        using var fallbackHttp = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return requests == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("format is not supported") }
                : Json(new { message = new { content = "Fallback plan." } });
        }));
        var fallback = await new OllamaStructuredPlanClient(fallbackHttp, Endpoint).CreatePlanAsync("local-model", Messages, null, false);
        Assert.False(fallback.UsedStructuredOutput);
        Assert.Equal("Fallback plan.", fallback.Markdown);
        Assert.Equal(2, requests);

        using var errorHttp = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("model not found") }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaStructuredPlanClient(errorHttp, Endpoint).CreatePlanAsync("missing", Messages, null, false));
        Assert.Contains("model not found", error.Message);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
