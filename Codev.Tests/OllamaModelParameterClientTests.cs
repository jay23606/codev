using System.Net;
using System.Text.Json;

namespace Codev.Tests;

public sealed class OllamaModelParameterClientTests
{
    [Fact]
    public async Task Reads_declared_parameters_from_show_response_and_requests_the_selected_model()
    {
        var handler = new StubHandler("""
            {"parameters":"temperature                    0.7\ntop_p                          0.9\ntop_k                          40\npresence_penalty               1.5\nPARAMETER repeat_penalty 1.1"}
            """);
        using var http = new HttpClient(handler);
        var client = new OllamaModelParameterClient(http, new Uri("http://127.0.0.1:11434"));

        var values = await client.GetDeclaredDefaultsAsync("qwen3-coder:latest");

        Assert.Equal("0.7", values["temperature"]);
        Assert.Equal("0.9", values["top_p"]);
        Assert.Equal("40", values["top_k"]);
        Assert.Equal("1.5", values["presence_penalty"]);
        Assert.Equal("1.1", values["repeat_penalty"]);
        Assert.Equal("/api/show", handler.Request!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("qwen3-coder:latest", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("verbose").GetBoolean());
    }

    [Fact]
    public async Task Returns_empty_defaults_when_the_model_declares_none()
    {
        using var http = new HttpClient(new StubHandler("""{"parameters":""}"""));
        var client = new OllamaModelParameterClient(http, new Uri("http://127.0.0.1:11434"));

        Assert.Empty(await client.GetDeclaredDefaultsAsync("model"));
    }

    [Fact]
    public async Task Reports_missing_model_from_ollama()
    {
        using var http = new HttpClient(new StubHandler("""{"error":"model not found"}""", HttpStatusCode.NotFound));
        var client = new OllamaModelParameterClient(http, new Uri("http://127.0.0.1:11434"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDeclaredDefaultsAsync("missing"));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("model not found", error.Message);
    }

    private sealed class StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) };
        }
    }
}
