using System.Net;
using System.Text;
using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class OpenAiCodeTaskRunnerTests
{
    [Fact]
    public async Task Runs_function_call_round_trip_and_returns_final_text_with_cumulative_usage()
    {
        var requests = new List<JsonDocument>();
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add(JsonDocument.Parse(body));
            return requests.Count == 1
                ? OpenAiSse("""
                    {"status":"completed","output":[{"type":"function_call","call_id":"call-1","name":"list_files","arguments":"{}"}],"usage":{"input_tokens":10,"output_tokens":5}}
                    """)
                : OpenAiSse("""
                    {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"The folder is empty."}]}],"usage":{"input_tokens":20,"output_tokens":7}}
                    """, "The folder is empty.");
        }));
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
        var toolExecutions = 0;
        var reportedUsage = new List<OpenAiCodeTaskUsage?>();
        var transcriptUpdates = new List<string>();
        var requestPayloadSnapshots = new List<string>();

        var result = await runner.RunAsync("gpt-test", [new { role = "user", content = "list files" }],
            [new { type = "function", name = "list_files" }],
            (step, _, _) => Task.FromResult($"api-key-{step}"),
            (name, arguments, _) =>
            {
                Assert.Equal("list_files", name);
                Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
                toolExecutions++;
                return Task.FromResult("[]");
            },
            (_, _, _) => Task.FromResult(false),
            onResponse: (_, usage) => { reportedUsage.Add(usage); return Task.CompletedTask; },
            onRequestPayload: body => { requestPayloadSnapshots.Add(body); return Task.CompletedTask; },
            onTranscript: text => { transcriptUpdates.Add(text); return Task.CompletedTask; });

        Assert.Equal(2, requests.Count);
        Assert.Equal(2, requestPayloadSnapshots.Count);
        Assert.True(requests[0].RootElement.GetProperty("stream").GetBoolean());
        Assert.False(requests[0].RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(requests[0].RootElement.GetRawText(), requestPayloadSnapshots[0]);
        Assert.Equal(requests[1].RootElement.GetRawText(), requestPayloadSnapshots[1]);
        Assert.Equal(1, toolExecutions);
        var followUpInput = requests[1].RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal("function_call", followUpInput[1].GetProperty("type").GetString());
        Assert.Equal("function_call_output", followUpInput[2].GetProperty("type").GetString());
        Assert.Equal("[]", followUpInput[2].GetProperty("output").GetString());
        Assert.Contains("The folder is empty.", result.Transcript);
        Assert.Equal(new OpenAiCodeTaskUsage(30, 12, 2, 2, 2), result.Usage);
        Assert.Equal(result.Usage, reportedUsage[^1]);
        Assert.Contains("**list files**", transcriptUpdates[0]);
    }

    [Fact]
    public async Task Repeated_identical_function_call_stops_after_user_declines()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            return OpenAiSse("{\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"call-" + requests +
                "\",\"name\":\"list_files\",\"arguments\":\"{}\"}]}");
        }));
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
        var toolExecutions = 0;
        var repeatedPrompts = 0;

        var result = await runner.RunAsync("gpt-test", [new { role = "user", content = "list files" }], [],
            (_, _, _) => Task.FromResult("key"),
            (_, _, _) => { toolExecutions++; return Task.FromResult("[]"); },
            (_, _, _) => { repeatedPrompts++; return Task.FromResult(false); });

        Assert.Equal(3, requests);
        Assert.Equal(2, toolExecutions);
        Assert.Equal(1, repeatedPrompts);
        Assert.Contains("same tool call repeated", result.Transcript);
    }

    [Fact]
    public async Task Stops_after_the_shared_model_step_limit()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            var arguments = JsonSerializer.Serialize("{\"step\":" + requests + "}");
            var body = JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[] { new { type = "function_call", call_id = $"call-{requests}", name = "read_file", arguments } }
            });
            return OpenAiSse(body);
        }));
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
        var toolExecutions = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("gpt-test",
            [new { role = "user", content = "read files" }], [],
            (_, _, _) => Task.FromResult("key"),
            (_, _, _) => { toolExecutions++; return Task.FromResult("contents"); },
            (_, _, _) => Task.FromResult(false)));

        Assert.Equal(OpenAiCodeTaskLimits.MaxModelStepsPerTurn, requests);
        Assert.Equal(requests, toolExecutions);
        Assert.Contains($"{OpenAiCodeTaskLimits.MaxModelStepsPerTurn}-step limit", exception.Message);
    }

    [Fact]
    public async Task Cancellation_after_a_tool_result_prevents_another_api_request()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            return OpenAiSse("""
                {"status":"completed","output":[{"type":"function_call","call_id":"call-1","name":"list_files","arguments":"{}"}]}
                """);
        }));
        using var cancellation = new CancellationTokenSource();
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("gpt-test",
            [new { role = "user", content = "list files" }], [],
            (_, _, _) => Task.FromResult("key"),
            (_, _, _) => { cancellation.Cancel(); return Task.FromResult("[]"); },
            (_, _, _) => Task.FromResult(false), cancellationToken: cancellation.Token));

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Keeps_partial_openai_text_visible_when_the_code_task_stream_is_interrupted()
    {
        using var http = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Partial plan\"}\n\n",
                Encoding.UTF8, "text/event-stream")
        }));
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
        var transcriptUpdates = new List<string>();

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("gpt-test",
            [new { role = "user", content = "inspect" }], [],
            (_, _, _) => Task.FromResult("key"),
            (_, _, _) => Task.FromResult("unused"),
            (_, _, _) => Task.FromResult(false),
            onTranscript: text => { transcriptUpdates.Add(text); return Task.CompletedTask; }));

        Assert.Contains("Partial plan", Assert.Single(transcriptUpdates));
    }

    [Fact]
    public async Task Keeps_completed_usage_and_tool_transcript_when_the_next_openai_round_is_interrupted()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            if (requests == 1)
                return OpenAiSse("""
                    {"status":"completed","output":[{"type":"function_call","call_id":"call-1","name":"list_files","arguments":"{}"}],"usage":{"input_tokens":12,"output_tokens":4}}
                    """);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Partial follow-up\"}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }));
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
        var reportedUsage = new List<OpenAiCodeTaskUsage?>();
        var transcriptUpdates = new List<string>();

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("gpt-test",
            [new { role = "user", content = "list files" }], [],
            (_, _, _) => Task.FromResult("key"),
            (_, _, _) => Task.FromResult("[\"src\"]"),
            (_, _, _) => Task.FromResult(false),
            onResponse: (_, usage) => { reportedUsage.Add(usage); return Task.CompletedTask; },
            onTranscript: text => { transcriptUpdates.Add(text); return Task.CompletedTask; }));

        Assert.Equal(2, requests);
        Assert.Equal(new OpenAiCodeTaskUsage(12, 4, 1, 1, 1), Assert.Single(reportedUsage));
        Assert.Contains(transcriptUpdates, text => text.Contains("**list files**", StringComparison.Ordinal) &&
            text.Contains("src", StringComparison.Ordinal) && text.Contains("Partial follow-up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publishes_openai_code_task_text_before_the_response_completes()
    {
        using var http = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PartialThenBlockingStream(
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Streaming now\"}\n\n"))
        }));
        using var cancellation = new CancellationTokenSource();
        var firstTextVisible = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new OpenAiCodeTaskRunner(new CloudModelApiClient(http));
        var run = runner.RunAsync("gpt-test", [new { role = "user", content = "inspect" }], [],
            (_, _, _) => Task.FromResult("key"),
            (_, _, _) => Task.FromResult("unused"),
            (_, _, _) => Task.FromResult(false),
            onTranscript: text =>
            {
                firstTextVisible.TrySetResult(text);
                return Task.CompletedTask;
            }, cancellationToken: cancellation.Token);

        var visibleText = await firstTextVisible.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Streaming now", visibleText);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static HttpResponseMessage OpenAiSse(string response, string? textDelta = null)
    {
        var deltaEvent = textDelta is null ? "" : "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = textDelta }) + "\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(deltaEvent + "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\n",
                Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class PartialThenBlockingStream(string initialData) : Stream
    {
        private readonly byte[] _data = Encoding.UTF8.GetBytes(initialData);
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            _sent = true;
            _data.AsMemory().CopyTo(buffer);
            return _data.Length;
        }
    }
}
