namespace Codev;

/// <summary>Runs locally queued work one item at a time while accepting new items during processing.</summary>
public sealed class SerialAsyncQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private object? _processor;

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void Enqueue(T item)
    {
        lock (_gate) _items.Enqueue(item);
    }

    public bool TryDequeuePending(out T item)
    {
        lock (_gate)
        {
            if (_items.TryDequeue(out item!)) return true;
            item = default!;
            return false;
        }
    }

    public IReadOnlyList<T> RemoveWhere(Predicate<T> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            var retained = new Queue<T>(_items.Count);
            var removed = new List<T>();
            while (_items.TryDequeue(out var item))
            {
                if (predicate(item)) removed.Add(item);
                else retained.Enqueue(item);
            }
            while (retained.TryDequeue(out var item)) _items.Enqueue(item);
            return removed;
        }
    }

    public bool MoveToFront(Predicate<T> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            var items = _items.ToList();
            var index = items.FindIndex(item => predicate(item));
            if (index < 0) return false;
            if (index == 0) return true;
            var prioritized = items[index];
            items.RemoveAt(index);
            items.Insert(0, prioritized);
            _items.Clear();
            foreach (var item in items) _items.Enqueue(item);
            return true;
        }
    }

    public async Task ProcessPendingAsync(Func<T, Task> processAsync, Func<T, Exception, Task> onErrorAsync, Func<bool>? shouldContinue = null)
    {
        var processor = new object();
        lock (_gate)
        {
            if (_processor is not null) return;
            _processor = processor;
        }

        var errors = new List<Exception>();
        try
        {
            while (true)
            {
                T item;
                lock (_gate)
                {
                    if (_items.Count == 0 || shouldContinue?.Invoke() == false)
                    {
                        _processor = null;
                        break;
                    }
                    item = _items.Dequeue();
                }
                try { await processAsync(item); }
                catch (Exception ex)
                {
                    try { await onErrorAsync(item, ex); }
                    catch (Exception handlerError) { errors.Add(handlerError); }
                }
            }
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_processor, processor)) _processor = null;
        }
        if (errors.Count > 0) throw new AggregateException("One or more queued items failed and their error handlers also failed.", errors);
    }
}
