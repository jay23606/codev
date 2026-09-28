using System.Net;
using System.Text;
using System.Text.Json;

namespace Codev.Tests;

public sealed class OllamaRuntimeClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434");

    [Fact]
    public async Task Lists_loaded_models_and_reports_server_memory_fields()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/ps", request.RequestUri!.AbsolutePath);
            return Json("""{"models":[{"name":"coder:latest","size":8589934592,"size_vram":2147483648,"context_length":65536,"expires_at":"2026-09-28T10:00:00+00:00"},{"model":"general","size":1024}]}""");
        }));

        var models = await new OllamaRuntimeClient(http, Endpoint).ListRunningModelsAsync();

        Assert.Collection(models,
            model =>
            {
                Assert.Equal("coder:latest", model.Name);
                Assert.Equal(8L * 1024 * 1024 * 1024, model.Size);
                Assert.Equal(2L * 1024 * 1024 * 1024, model.SizeVram);
                Assert.Equal(65536, model.ContextLength);
                Assert.Equal(DateTimeOffset.Parse("2026-09-28T10:00:00+00:00"), model.ExpiresAt);
            },
            model =>
            {
                Assert.Equal("general", model.Name);
                Assert.Equal(1024, model.Size);
                Assert.Equal(0, model.SizeVram);
            });
    }

    [Fact]
    public async Task Unloads_model_with_keep_alive_zero_and_empty_generation()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/generate", request.RequestUri!.AbsolutePath);
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("coder:latest", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(0, payload.RootElement.GetProperty("keep_alive").GetInt32());
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(payload.RootElement.TryGetProperty("prompt", out _));
            return Json("""{"model":"coder:latest","done":true,"done_reason":"unload"}""");
        }));

        await new OllamaRuntimeClient(http, Endpoint).UnloadAsync("coder:latest");
    }

    [Fact]
    public async Task Unload_rejects_an_empty_model_name_before_network_access()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No request should be sent.")));

        await Assert.ThrowsAsync<ArgumentException>(() => new OllamaRuntimeClient(http, Endpoint).UnloadAsync(" "));
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
