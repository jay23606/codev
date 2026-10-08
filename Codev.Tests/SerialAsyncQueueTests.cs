using System.Text.Json;

namespace Codev.Tests;

public sealed class SerialAsyncQueueTests
{
    [Fact]
    public async Task Work_added_while_running_joins_fifo_and_never_runs_concurrently()
    {
        var queue = new SerialAsyncQueue<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var active = 0;
        var maximumActive = 0;
        queue.Enqueue(1);

        var drain = queue.ProcessPendingAsync(async item =>
        {
            var nowActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, nowActive);
            if (item == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            lock (order) order.Add(item);
            Interlocked.Decrement(ref active);
        }, (_, _) => Task.CompletedTask);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(2);
        queue.Enqueue(3);
        await queue.ProcessPendingAsync(_ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Equal(2, queue.Count);

        releaseFirst.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 2, 3], order);
        Assert.Equal(1, maximumActive);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task A_failed_item_is_reported_and_does_not_block_later_items()
    {
        var queue = new SerialAsyncQueue<int>();
        var processed = new List<int>();
        var failures = new List<string>();
        queue.Enqueue(1);
        queue.Enqueue(2);

        await queue.ProcessPendingAsync(item =>
        {
            if (item == 1) throw new InvalidOperationException("expected failure");
            processed.Add(item);
            return Task.CompletedTask;
        }, (item, error) =>
        {
            failures.Add($"{item}: {error.Message}");
            return Task.CompletedTask;
        });

        Assert.Equal([2], processed);
        Assert.Equal(["1: expected failure"], failures);
    }

    [Fact]
    public async Task Removing_queued_items_keeps_the_remaining_fifo_order()
    {
        var queue = new SerialAsyncQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        Assert.Equal([2], queue.RemoveWhere(item => item == 2));
        var order = new List<int>();
        await queue.ProcessPendingAsync(item => { order.Add(item); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);

        Assert.Equal([1, 3], order);
    }

    [Fact]
    public async Task Moving_a_queued_item_to_the_front_preserves_the_order_of_other_items()
    {
        var queue = new SerialAsyncQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        Assert.True(queue.MoveToFront(item => item == 3));
        var order = new List<int>();
        await queue.ProcessPendingAsync(item => { order.Add(item); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);

        Assert.Equal([3, 1, 2], order);
    }

    [Fact]
    public void Moving_a_missing_queue_item_does_not_change_order()
    {
        var queue = new SerialAsyncQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);

        Assert.False(queue.MoveToFront(item => item == 3));
        Assert.Equal([1, 2], queue.RemoveWhere(_ => true));
    }

    [Fact]
    public async Task Processing_can_pause_between_items_and_resume_without_reordering()
    {
        var queue = new SerialAsyncQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        var processed = new List<int>();
        var allowNext = true;

        await queue.ProcessPendingAsync(item =>
        {
            processed.Add(item);
            allowNext = false;
            return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, () => allowNext);

        Assert.Equal([1], processed);
        Assert.Equal(2, queue.Count);

        allowNext = true;
        await queue.ProcessPendingAsync(item =>
        {
            processed.Add(item);
            return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, () => allowNext);

        Assert.Equal([1, 2, 3], processed);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Resuming_while_the_previous_processor_is_finishing_does_not_lose_work()
    {
        var queue = new SerialAsyncQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);
        var processed = new List<int>();
        var continueProcessing = true;
        var finishedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var previousProcessor = queue.ProcessPendingAsync(async item =>
        {
            processed.Add(item);
            continueProcessing = false;
            finishedFirst.SetResult();
            await releaseFirst.Task;
        }, (_, _) => Task.CompletedTask, () => continueProcessing);

        await finishedFirst.Task.WaitAsync(TimeSpan.FromSeconds(2));
        continueProcessing = true;
        await queue.ProcessPendingAsync(item => { processed.Add(item); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        releaseFirst.SetResult();
        await previousProcessor.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 2], processed);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Runtime_queue_state_is_not_written_to_the_conversation_store()
    {
        var conversation = new Conversation { PendingRequestCount = 4, LastPromptContext = 32_768 };

        var json = JsonSerializer.Serialize(conversation);

        Assert.DoesNotContain("PendingRequestCount", json);
        Assert.Contains("LastPromptContext", json);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
