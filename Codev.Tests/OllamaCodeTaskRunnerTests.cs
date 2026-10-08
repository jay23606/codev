using System.Net;
using System.Text;
using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class OllamaCodeTaskRunnerTests
{
    [Fact]
    public async Task Sends_keep_alive_and_sampling_settings_and_returns_final_text_and_stats()
    {
        JsonDocument? capturedRequest = null;
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            capturedRequest = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json("""{"message":{"role":"assistant","content":"Done","thinking":"I checked."},"prompt_eval_count":23}""");
        }));
        var thinking = new List<string>();
        var promptTokens = new List<int>();
        var requestSnapshots = new List<string>();
        var transcripts = new List<string>();
        var runner = new OllamaCodeTaskRunner(http);

        var result = await runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "inspect")], [new { type = "function", name = "read_file" }],
            think: true, numCtx: 8192, temperature: 0.25, topP: 0.8, topK: 20,
            presencePenalty: 0.5, repeatPenalty: 1.1, numPredict: 4096,
            onRequest: (_, _, payload) => { requestSnapshots.Add(payload); return Task.CompletedTask; },
            onThinking: text => { thinking.Add(text); return Task.CompletedTask; },
            onPromptTokens: count => { promptTokens.Add(count); return Task.CompletedTask; },
            executeTool: (_, _, _) => throw new InvalidOperationException("No tool call was expected."),
            confirmRepeatedToolCall: (_, _, _) => Task.FromResult(false),
            onTranscript: text => { transcripts.Add(text); return Task.CompletedTask; });

        var payload = capturedRequest!.RootElement;
        Assert.Equal("qwen-test", payload.GetProperty("model").GetString());
        Assert.Equal(OllamaRuntimeClient.ConversationKeepAlive, payload.GetProperty("keep_alive").GetString());
        Assert.True(payload.GetProperty("think").GetBoolean());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        var options = payload.GetProperty("options");
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0.25, options.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, options.GetProperty("top_p").GetDouble());
        Assert.Equal(20, options.GetProperty("top_k").GetInt32());
        Assert.Equal(0.5, options.GetProperty("presence_penalty").GetDouble());
        Assert.Equal(1.1, options.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(4096, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(requestSnapshots[0], payload.GetRawText());
        Assert.Equal("Done", result.Transcript);
        Assert.False(result.ReachedStepLimit);
        Assert.Equal(1, result.Requests);
        Assert.Equal(["I checked."], thinking);
        Assert.Equal([23], promptTokens);
        Assert.Equal(["Done"], transcripts);
    }

    [Fact]
    public async Task Runs_tool_round_trip_and_publishes_bounded_untrusted_output()
    {
        var requests = new List<JsonDocument>();
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            requests.Add(JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return requests.Count == 1
                ? Json("""{"message":{"role":"assistant","content":"Checking.","tool_calls":[{"function":{"name":"read_file","arguments":{"relative_path":"a.txt"}}}]}}""")
                : Json("""{"message":{"role":"assistant","content":"Found it."}}""");
        }));
        var executed = 0;
        var runner = new OllamaCodeTaskRunner(http);
        var result = await runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "read a.txt")], [new { type = "function", name = "read_file" }],
            think: false, numCtx: 4096, temperature: null, topP: null, topK: null,
            presencePenalty: null, repeatPenalty: null, numPredict: null,
            executeTool: (name, arguments, _) =>
            {
                Assert.Equal("read_file", name);
                Assert.Equal("a.txt", arguments.GetProperty("relative_path").GetString());
                executed++;
                return Task.FromResult("file contents");
            },
            confirmRepeatedToolCall: (_, _, _) => Task.FromResult(false));

        Assert.Equal(1, executed);
        Assert.Equal(2, result.Requests);
        Assert.False(result.ReachedStepLimit);
        Assert.Contains("Checking.", result.Transcript);
        Assert.Contains("**read file**", result.Transcript);
        Assert.Contains("file contents", result.Transcript);
        Assert.Contains("Found it.", result.Transcript);
        var secondMessages = requests[1].RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("assistant", secondMessages[1].GetProperty("role").GetString());
        Assert.Equal("read_file", secondMessages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", secondMessages[2].GetProperty("role").GetString());
        Assert.Equal("read_file", secondMessages[2].GetProperty("tool_name").GetString());
        Assert.Equal("file contents", secondMessages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Corrects_tool_output_mimic_before_publishing_a_real_tool_result()
    {
        var requests = new List<JsonDocument>();
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            requests.Add(JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return requests.Count switch
            {
                1 => Json("""{"message":{"role":"assistant","content":"**write file**\n{\"type\":\"untrusted_tool_output\",\"source\":\"project file modified\",\"path\":\"a.txt\",\"content\":\"Replaced the existing project file.\",\"activity\":\"modified_file\"}"}}"""),
                2 => Json("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"write_file","arguments":{"relative_path":"a.txt","content":"updated"}}}]}}"""),
                _ => Json("""{"message":{"role":"assistant","content":"The requested edit is complete."}}""")
            };
        }));
        var executed = 0;
        var runner = new OllamaCodeTaskRunner(http);

        var result = await runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "edit a.txt")], [new { type = "function", name = "write_file" }],
            think: false, numCtx: 0, temperature: null, topP: null, topK: null,
            presencePenalty: null, repeatPenalty: null, numPredict: null,
            executeTool: (name, arguments, _) =>
            {
                Assert.Equal("write_file", name);
                Assert.Equal("a.txt", arguments.GetProperty("relative_path").GetString());
                executed++;
                return Task.FromResult(UntrustedToolOutput.Format("project file updated", "Applied the change.", "a.txt", activity: "edited_file"));
            },
            confirmRepeatedToolCall: (_, _, _) => Task.FromResult(false));

        Assert.Equal(1, executed);
        Assert.Equal(3, result.Requests);
        Assert.DoesNotContain("project file modified", result.Transcript, StringComparison.Ordinal);
        Assert.Contains("**write file**", result.Transcript, StringComparison.Ordinal);
        Assert.Contains("The requested edit is complete.", result.Transcript, StringComparison.Ordinal);
        var outputs = ToolOutputTranscriptParser.Parse(result.Transcript).Outputs;
        var actualEdit = Assert.Single(outputs);
        Assert.Equal("edited_file", actualEdit.Activity);
        Assert.Equal("a.txt", actualEdit.Path);
        var correctionMessages = requests[1].RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("user", correctionMessages[^1].GetProperty("role").GetString());
        Assert.Contains("structured tool call", correctionMessages[^1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suppresses_repeated_tool_output_mimic_when_no_tool_is_executed()
    {
        const string fakeOutput = "**write file**\n{\"type\":\"untrusted_tool_output\",\"source\":\"project file modified\",\"path\":\"a.txt\",\"content\":\"Replaced the existing project file.\",\"activity\":\"modified_file\"}";
        using var http = new HttpClient(new ResponseHandler(_ =>
            Json(JsonSerializer.Serialize(new { message = new { role = "assistant", content = fakeOutput } }))));
        var executed = 0;
        var transcripts = new List<string>();
        var runner = new OllamaCodeTaskRunner(http);

        var result = await runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "edit a.txt")], [new { type = "function", name = "write_file" }],
            think: false, numCtx: 0, temperature: null, topP: null, topK: null,
            presencePenalty: null, repeatPenalty: null, numPredict: null,
            executeTool: (_, _, _) => { executed++; return Task.FromResult("unexpected"); },
            confirmRepeatedToolCall: (_, _, _) => Task.FromResult(false),
            onTranscript: text => { transcripts.Add(text); return Task.CompletedTask; });

        Assert.Equal(0, executed);
        Assert.Equal(2, result.Requests);
        Assert.DoesNotContain("project file modified", result.Transcript, StringComparison.Ordinal);
        Assert.Contains("no structured tool call", result.Transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Transcript, Assert.Single(transcripts));
    }

    [Fact]
    public async Task Stops_at_profile_step_limit_after_recording_completed_tool_result()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            return Json("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"list_files","arguments":{}}}]}}""");
        }));
        var executed = 0;
        var runner = new OllamaCodeTaskRunner(http);
        var result = await runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "list files")], [],
            think: false, numCtx: 0, temperature: null, topP: null, topK: null,
            presencePenalty: null, repeatPenalty: null, numPredict: null,
            executeTool: (_, _, _) => { executed++; return Task.FromResult("one file"); },
            confirmRepeatedToolCall: (_, _, _) => Task.FromResult(false), maxSteps: 1);

        Assert.Equal(1, requests);
        Assert.Equal(1, executed);
        Assert.True(result.ReachedStepLimit);
        Assert.Contains("1-step limit", result.Transcript);
        Assert.Contains("one file", result.Transcript);
    }

    [Fact]
    public async Task Repeated_identical_tool_calls_stop_when_confirmation_is_declined()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            return Json("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"list_files","arguments":{}}}]}}""");
        }));
        var executed = 0;
        var confirmations = 0;
        var runner = new OllamaCodeTaskRunner(http);
        var result = await runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "list files")], [],
            think: false, numCtx: 0, temperature: null, topP: null, topK: null,
            presencePenalty: null, repeatPenalty: null, numPredict: null,
            executeTool: (_, _, _) => { executed++; return Task.FromResult("entries"); },
            confirmRepeatedToolCall: (_, _, _) => { confirmations++; return Task.FromResult(false); });

        Assert.Equal(3, requests);
        Assert.Equal(2, executed);
        Assert.Equal(1, confirmations);
        Assert.False(result.ReachedStepLimit);
        Assert.Contains("same tool call repeated", result.Transcript);
    }

    [Fact]
    public async Task Cancellation_after_tool_execution_prevents_another_request()
    {
        var requests = 0;
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            requests++;
            return Json("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"read_file","arguments":{}}}]}}""");
        }));
        using var cancellation = new CancellationTokenSource();
        var runner = new OllamaCodeTaskRunner(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new Uri("http://127.0.0.1:11434/"), "qwen-test",
            [new OllamaCodeTaskMessage("user", "read")], [],
            think: false, numCtx: 0, temperature: null, topP: null, topK: null,
            presencePenalty: null, repeatPenalty: null, numPredict: null,
            executeTool: (_, _, _) => { cancellation.Cancel(); return Task.FromResult("ok"); },
            confirmRepeatedToolCall: (_, _, _) => Task.FromResult(false), cancellationToken: cancellation.Token));

        Assert.Equal(1, requests);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
