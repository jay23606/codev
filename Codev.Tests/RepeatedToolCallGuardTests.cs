using System.Text.Json;

namespace Codev.Tests;

public sealed class RepeatedToolCallGuardTests
{
    [Fact]
    public void Counts_identical_consecutive_calls_even_when_object_properties_are_reordered()
    {
        using var first = JsonDocument.Parse("{\"path\":\"src/a.cs\",\"limit\":3}");
        using var reordered = JsonDocument.Parse("{\"limit\":3,\"path\":\"src/a.cs\"}");
        var guard = new RepeatedToolCallGuard();

        Assert.Equal(1, guard.Record("read_file", first.RootElement));
        Assert.Equal(2, guard.Record("read_file", reordered.RootElement));
        Assert.Equal(3, guard.Record("read_file", first.RootElement));
    }

    [Fact]
    public void Different_tool_or_arguments_start_a_new_consecutive_run()
    {
        using var first = JsonDocument.Parse("{\"path\":\"a.cs\"}");
        using var another = JsonDocument.Parse("{\"path\":\"b.cs\"}");
        var guard = new RepeatedToolCallGuard();

        Assert.Equal(1, guard.Record("read_file", first.RootElement));
        Assert.Equal(1, guard.Record("search_files", first.RootElement));
        Assert.Equal(2, guard.Record("search_files", first.RootElement));
        Assert.Equal(1, guard.Record("search_files", another.RootElement));
    }

    [Fact]
    public void User_approval_resets_the_sequence_for_one_more_repeat_before_prompting_again()
    {
        using var arguments = JsonDocument.Parse("{\"path\":\"a.cs\"}");
        var guard = new RepeatedToolCallGuard();
        for (var index = 0; index < RepeatedToolCallGuard.ConfirmationThreshold; index++)
            guard.Record("read_file", arguments.RootElement);

        guard.AllowOneMore();

        Assert.Equal(1, guard.Record("read_file", arguments.RootElement));
    }
}
