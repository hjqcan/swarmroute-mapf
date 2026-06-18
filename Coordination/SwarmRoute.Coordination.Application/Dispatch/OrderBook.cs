using System.Collections.Concurrent;

namespace SwarmRoute.Coordination.Application.Dispatch;

/// <summary>
/// Thread-safe intake + lifecycle store for <see cref="TransportOrder"/>s. Orders arrive (API / CAP subscriber),
/// wait <see cref="Pending"/> until the dispatcher assigns one to a vehicle, and are marked
/// <see cref="Complete"/> when that vehicle reaches the destination. A bounded completed-log is retained for
/// status/observability.
/// </summary>
public sealed class OrderBook
{
    private readonly object _gate = new();
    private readonly List<TransportOrder> _pending = new();
    private readonly Dictionary<string, TransportOrder> _assigned = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<TransportOrder> _completed = new();
    private long _seq;

    /// <summary>Enqueues an order. A blank id is replaced with a generated stable id (returned on the order).</summary>
    public TransportOrder Submit(TransportOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.DestinationSiteId))
            throw new ArgumentException("Order destination must be provided.", nameof(order));

        lock (_gate)
        {
            var id = string.IsNullOrWhiteSpace(order.Id) ? $"ord-{Interlocked.Increment(ref _seq)}" : order.Id.Trim();
            var stored = order with { Id = id };
            _pending.Add(stored);
            return stored;
        }
    }

    /// <summary>Claims a specific pending order by id (moves it to assigned). False if it is no longer pending.</summary>
    public bool TryTake(string orderId)
    {
        lock (_gate)
        {
            var idx = _pending.FindIndex(o => string.Equals(o.Id, orderId, StringComparison.Ordinal));
            if (idx < 0)
                return false;
            var order = _pending[idx];
            _pending.RemoveAt(idx);
            _assigned[order.Id] = order;
            return true;
        }
    }

    /// <summary>Snapshot of the orders waiting to be assigned (highest priority first).</summary>
    public IReadOnlyList<TransportOrder> Pending()
    {
        lock (_gate)
            return _pending.OrderBy(o => o.Priority).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Marks an assigned order complete (moved to the completed log).</summary>
    public void Complete(string orderId)
    {
        lock (_gate)
        {
            if (_assigned.Remove(orderId, out var order))
            {
                _completed.Enqueue(order);
                while (_completed.Count > 256)
                    _completed.TryDequeue(out _);
            }
        }
    }

    /// <summary>Counts: pending, assigned (in flight), completed.</summary>
    public (int Pending, int Assigned, int Completed) Counts()
    {
        lock (_gate)
            return (_pending.Count, _assigned.Count, _completed.Count);
    }
}
