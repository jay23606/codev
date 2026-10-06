using System.Text.Json;
using Codev;

namespace Codev.Tests;

public sealed class ToolOutputTranscriptParserTests
{
    [Fact]
    public void Untrusted_tool_output_is_removed_from_prose_and_pretty_printed_for_collapsed_view()
    {
        var envelope = UntrustedToolOutput.Format("approved command output", "{\"output\":\"first\\nsecond\",\"exit code\":0}");
        var transcript = "I ran the command.\n\n**run command**\n" + envelope + "\n\nAll set.";

        var (displayText, outputs) = ToolOutputTranscriptParser.Parse(transcript);

        Assert.Equal("I ran the command.\n\nAll set.", displayText);
        var output = Assert.Single(outputs);
        Assert.Equal("run command · approved command output", output.Header);
        using var parsed = JsonDocument.Parse(output.Content);
        Assert.Contains("\n  \"output\"", output.Content);
        Assert.Equal(0, parsed.RootElement.GetProperty("exit code").GetInt32());
    }

    [Fact]
    public void Ordinary_json_in_assistant_prose_is_not_hidden_as_a_tool_result()
    {
        const string transcript = "Use this JSON: {\"type\":\"untrusted_tool_output\"}";

        var (displayText, outputs) = ToolOutputTranscriptParser.Parse(transcript);

        Assert.Equal(transcript, displayText);
        Assert.Empty(outputs);
    }

    [Fact]
    public void Truncating_a_tool_envelope_keeps_it_valid_and_collapsible()
    {
        var envelope = UntrustedToolOutput.Format("approved command output", new string('x', 20_000));
        var truncated = UntrustedToolOutput.Truncate(envelope, 500);
        var transcript = "**run command**\n" + truncated;

        Assert.True(truncated.Length <= 500);
        var (displayText, outputs) = ToolOutputTranscriptParser.Parse(transcript);

        Assert.Equal("", displayText);
        Assert.Contains("tool output truncated", Assert.Single(outputs).Content);
    }

    [Fact]
    public void Command_output_exposes_compact_ran_summary_and_keeps_details()
    {
        const string command = "Get-Location; rg -n \"TaskChecklist\" Codev.Avalonia";
        var envelope = UntrustedToolOutput.Format("approved command output", "C:\\project\nExit code: 0", command: command);

        var (_, outputs) = ToolOutputTranscriptParser.Parse("**run command**\n" + envelope);
        var output = Assert.Single(outputs);

        Assert.Equal("Ran " + command, output.Summary);
        Assert.Contains("Exit code: 0", output.Content);
    }

    [Fact]
    public void Verification_status_before_tool_envelope_keeps_command_in_collapsed_activity()
    {
        const string command = "node --version";
        var envelope = UntrustedToolOutput.Format("approved verification command output", "v22.23.3\nExit code: 0", command: command);

        var (displayText, outputs) = ToolOutputTranscriptParser.Parse("**verify command**\nVerification PASSED (exit code 0).\n" + envelope);

        Assert.Equal("Verification PASSED (exit code 0).", displayText);
        var output = Assert.Single(outputs);
        Assert.Equal("verify command · approved verification command output", output.Header);
        Assert.Equal("Ran node --version", output.Summary);
        Assert.Contains("v22.23.3", output.Content);
        Assert.DoesNotContain("untrusted_tool_output", displayText, StringComparison.Ordinal);
    }

    [Fact]
    public void Assistant_message_separates_command_outputs_for_the_collapsed_ran_commands_group()
    {
        var command = UntrustedToolOutput.Format("approved command output", "Exit code: 0", command: "git status --short");
        var file = UntrustedToolOutput.Format("project file", "contents", path: "README.md", activity: "read_file");
        var message = new ChatMessage("assistant", "**run command**\n" + command + "\n**read_file**\n" + file);

        Assert.Equal("Ran commands, read files", message.CommandToolOutputsHeader);
        Assert.True(message.HasCommandToolOutputs);
        Assert.Equal("git status --short", message.CommandToolOutputs[0].Command);
        Assert.Equal("Read file · README.md", message.CommandToolOutputs[1].Summary);
    }

    [Fact]
    public void Collapsed_activity_summary_covers_each_action_in_turn_order()
    {
        var created = UntrustedToolOutput.Format("project file created", "created", "index.html", activity: "created_file");
        var edited = UntrustedToolOutput.Format("project file updated", "updated", "game.js", activity: "edited_file");
        var search = UntrustedToolOutput.Format("project search results", "match", activity: "search_files");
        var command = UntrustedToolOutput.Format("approved command output", "Exit code: 0", command: "npm test");
        var webSearch = UntrustedToolOutput.Format("web search results", "result", activity: "web_search");
        var transcript = "**create_file**\n" + created + "\n**write_file**\n" + edited + "\n**run_command**\n" + command + "\n**search_files**\n" + search + "\n**web_search**\n" + webSearch;

        var message = new ChatMessage("assistant", transcript);

        Assert.Equal("Created a file, edited a file, ran commands, searched files, searched the web", message.CommandToolOutputsHeader);
        Assert.Equal(new[] { "Created file · index.html", "Edited file · game.js", "Ran npm test", "Searched files", "Searched the web" },
            message.CommandToolOutputs.Select(output => output.Summary));
    }

    [Fact]
    public void Collapsed_activity_summary_names_mcp_tools_as_their_own_action()
    {
        var mcp = UntrustedToolOutput.Format("MCP tool output", "result", command: "GitHub/search", activity: "mcp_tool");
        var command = UntrustedToolOutput.Format("approved command output", "Exit code: 0", command: "dotnet test");
        var message = new ChatMessage("assistant", "**mcp call**\n" + mcp + "\n**run command**\n" + command);

        Assert.Equal("Used MCP tools, ran commands", message.CommandToolOutputsHeader);
        Assert.Equal("Used MCP tool · GitHub/search", message.CommandToolOutputs[0].Summary);
    }

    [Fact]
    public void Collapsed_activity_summary_includes_unknown_actions_without_calling_them_commands()
    {
        var webSearch = UntrustedToolOutput.Format("web search results", "result", activity: "web_search");
        var futureAction = UntrustedToolOutput.Format("workspace action", "done", activity: "export_conversation");
        var command = UntrustedToolOutput.Format("command output", "Exit code: 0", command: "npm test");
        var message = new ChatMessage("assistant", "**web_search**\n" + webSearch + "\n**export**\n" + futureAction + "\n**run_command**\n" + command);

        Assert.Equal("Searched the web, did other things, ran commands", message.CommandToolOutputsHeader);
        Assert.Equal("Searched the web", message.CommandToolOutputs[0].Summary);
    }

    [Fact]
    public void Collapsed_activity_summary_uses_singular_wording_for_one_edited_file()
    {
        var edited = UntrustedToolOutput.Format("project file updated", "updated", "game.js", activity: "edited_file");
        var command = UntrustedToolOutput.Format("approved command output", "Exit code: 0", command: "npm test");
        var webSearch = UntrustedToolOutput.Format("web search results", "result", activity: "web_search");
        var message = new ChatMessage("assistant", "**write_file**\n" + edited + "\n**run_command**\n" + command + "\n**web_search**\n" + webSearch);

        Assert.Equal("Edited a file, ran commands, searched the web", message.CommandToolOutputsHeader);
    }

    [Fact]
    public void Tool_output_truncation_preserves_activity_and_path_metadata()
    {
        var envelope = UntrustedToolOutput.Format("project file updated", new string('x', 20_000), "src/game.js", activity: "edited_file");

        var truncated = UntrustedToolOutput.Truncate(envelope, 500);
        var (_, outputs) = ToolOutputTranscriptParser.Parse("**write_file**\n" + truncated);

        Assert.Equal("Edited file · src/game.js", Assert.Single(outputs).Summary);
    }
}
