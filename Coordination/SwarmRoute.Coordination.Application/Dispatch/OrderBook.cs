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
    private readonly List<OrderState> _pending = new();
    private readonly Dictionary<string, OrderState> _assigned = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<OrderState> _completed = new();
    private readonly HashSet<string> _knownOrderIds = new(StringComparer.Ordinal);
    private long _nextGeneratedId;
    private long _arrivalSeq;
    private long _completedTotal;

    /// <summary>Enqueues an order. A blank id is replaced with a generated stable id (returned on the order).</summary>
    public TransportOrder Submit(TransportOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.DestinationSiteId))
            throw new ArgumentException("Order destination must be provided.", nameof(order));

        lock (_gate)
        {
            var id = string.IsNullOrWhiteSpace(order.Id) ? NextGeneratedId() : order.Id.Trim();
            if (!_knownOrderIds.Add(id))
                throw new DuplicateOrderIdException(id);

            var stored = order with { Id = id, DestinationSiteId = order.DestinationSiteId.Trim() };
            _pending.Add(new OrderState(stored, ++_arrivalSeq));
            return stored;
        }
    }

    /// <summary>Claims a specific pending order by id (moves it to assigned). False if it is no longer pending.</summary>
    public bool TryTake(string orderId)
    {
        lock (_gate)
        {
            var idx = _pending.FindIndex(o => string.Equals(o.Order.Id, orderId, StringComparison.Ordinal));
            if (idx < 0)
                return false;
            var order = _pending[idx];
            _pending.RemoveAt(idx);
            _assigned[order.Order.Id] = order;
            return true;
        }
    }

    /// <summary>Snapshot of the orders waiting to be assigned (highest priority first, then arrival order).</summary>
    public IReadOnlyList<TransportOrder> Pending()
    {
        lock (_gate)
            return _pending
                .OrderBy(o => o.Order.Priority)
                .ThenBy(o => o.Sequence)
                .Select(o => o.Order)
                .ToList();
    }

    /// <summary>Marks an assigned order complete (moved to the completed log).</summary>
    public void Complete(string orderId)
    {
        lock (_gate)
        {
            if (_assigned.Remove(orderId, out var order))
            {
                _completed.Enqueue(order);
                _completedTotal++;
                while (_completed.Count > 256)
                    _completed.TryDequeue(out _);
            }
        }
    }

    /// <summary>Counts: pending, assigned (in flight), completed retained in the rolling log.</summary>
    public (int Pending, int Assigned, int Completed) Counts()
    {
        lock (_gate)
            return (_pending.Count, _assigned.Count, _completed.Count);
    }

    /// <summary>Monotonic number of completed orders for the lifetime of this in-memory order book.</summary>
    public long CompletedTotal
    {
        get { lock (_gate) return _completedTotal; }
    }

    private string NextGeneratedId()
    {
        string id;
        do
        {
            id = $"ord-{++_nextGeneratedId}";
        } while (_knownOrderIds.Contains(id));

        return id;
    }

    private sealed record OrderState(TransportOrder Order, long Sequence);
}

/// <summary>Raised when an order id has already been used in this order book lifetime.</summary>
public sealed class DuplicateOrderIdException(string orderId)
    : InvalidOperationException($"Order id '{orderId}' already exists.")
{
    public string OrderId { get; } = orderId;
}
