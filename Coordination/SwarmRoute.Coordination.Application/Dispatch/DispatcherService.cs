using Microsoft.Extensions.Logging;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Coordination.Application.Dispatch;

/// <summary>
/// The autonomous dispatcher (OpenTCS "Dispatcher"/"Router" reduced to a v1 skeleton): it turns a live feed of
/// <see cref="TransportOrder"/>s into a goal book for the lifelong <see cref="FleetCoordinationLoop"/> and drives
/// demo vehicle poses so the host can show order assignment progress. Implements <see cref="ICoordinationGoalSource"/>
/// so the loop reads the goal book directly; a separate hosted service ticks <see cref="DispatchAsync"/>.
/// <para>
/// Each dispatch tick: (1) advance every assigned vehicle one control point along the shortest path to its
/// destination — through a one-vehicle-per-CP gate so the demo poses never overlap — completing the order on
/// arrival; (2) assign each idle vehicle the nearest reachable pending order (graph distance); (3) rebuild the
/// goal book from the in-flight assignments (each vehicle's CURRENT pose → its destination), so the coordination
/// loop continuously re-plans/reserves the remaining route.
/// </para>
/// <para><b>Skeleton scope.</b> Pose advancement is a simple kinematic demo stand-in (one CP/tick along the
/// shortest path, gated for occupancy). It produces goals for the reservation layer, but it does not consume
/// reservation grants as movement authority; the validated reservation-authoritative executor lives in the
/// Simulation context. This is a dispatch-flow demo, not a full WMS.</para>
/// </summary>
public sealed class DispatcherService : ICoordinationGoalSource
{
    private readonly Guid _roadmapId;
    private readonly IRoadmapQueryService _roadmaps;
    private readonly OrderBook _orders;
    private readonly VehicleRegistry _vehicles;
    private readonly ILogger<DispatcherService> _logger;

    private readonly object _gate = new();
    private IReadOnlyCollection<AgentGoal> _goals = Array.Empty<AgentGoal>();

    public DispatcherService(
        Guid roadmapId,
        IRoadmapQueryService roadmaps,
        OrderBook orders,
        VehicleRegistry vehicles,
        ILogger<DispatcherService> logger)
    {
        _roadmapId = roadmapId;
        _roadmaps = roadmaps ?? throw new ArgumentNullException(nameof(roadmaps));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Guid? CurrentRoadmapId => _roadmapId;

    /// <inheritdoc />
    public IReadOnlyCollection<AgentGoal> CurrentGoals
    {
        get { lock (_gate) return _goals; }
    }

    /// <summary>
    /// Validates that an intake destination exists on the active dispatcher roadmap. Returns <see langword="null"/>
    /// when the roadmap graph is unavailable, so API callers can fail fast instead of creating stuck orders.
    /// </summary>
    public async Task<bool?> DestinationExistsAsync(string siteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            return false;

        var graph = await _roadmaps.TryGetGraphAsync(_roadmapId, cancellationToken).ConfigureAwait(false);
        return graph?.HasSite(siteId.Trim());
    }

    /// <summary>
    /// Advances the fleet one step and refreshes the goal book. Safe no-op when the roadmap graph is not yet
    /// available. Returns the goals now in flight (also exposed via <see cref="CurrentGoals"/>).
    /// </summary>
    public async Task<IReadOnlyCollection<AgentGoal>> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var graph = await _roadmaps.TryGetGraphAsync(_roadmapId, cancellationToken).ConfigureAwait(false);
        if (graph is null)
            return Array.Empty<AgentGoal>();

        var goals = _vehicles.Mutate(vehicles =>
        {
            AdvanceAssigned(vehicles, graph);
            AssignIdle(vehicles, graph);

            return vehicles
                .Where(v => !v.IsIdle)
                .Select(v => new AgentGoal(v.Id, v.SiteId, v.GoalSiteId!, v.Priority))
                .ToList();
        });

        lock (_gate)
            _goals = goals;
        return goals;
    }

    /// <summary>Step each assigned vehicle one CP toward its goal in the demo pose model; complete the order on arrival.</summary>
    private void AdvanceAssigned(IReadOnlyCollection<Vehicle> vehicles, RoadmapGraph graph)
    {
        // Cells that will be occupied after this tick: idle vehicles stay put; movers claim their next CP.
        var claimedNext = new HashSet<string>(
            vehicles.Where(v => v.IsIdle).Select(v => v.SiteId), StringComparer.Ordinal);
        var occupantNow = vehicles.ToDictionary(v => v.SiteId, v => v.Id, StringComparer.Ordinal);

        foreach (var v in vehicles.Where(v => !v.IsIdle).OrderBy(v => v.Priority).ThenBy(v => v.Id, StringComparer.Ordinal))
        {
            if (string.Equals(v.SiteId, v.GoalSiteId, StringComparison.Ordinal))
            {
                CompleteOrder(v);
                continue;
            }

            var path = graph.ShortestPath(v.SiteId, v.GoalSiteId!);
            if (path is null || path.Count < 2)
            {
                claimedNext.Add(v.SiteId); // destination unreachable right now — hold and retry next tick
                continue;
            }

            var nextCp = path[1];
            var blockedByMover = claimedNext.Contains(nextCp);
            var occupiedByOther = occupantNow.TryGetValue(nextCp, out var occId) && !string.Equals(occId, v.Id, StringComparison.Ordinal);
            if (blockedByMover || occupiedByOther)
            {
                claimedNext.Add(v.SiteId); // wait one tick (the one-vehicle-per-CP gate)
                continue;
            }

            v.SiteId = nextCp;
            claimedNext.Add(nextCp);
            if (string.Equals(v.SiteId, v.GoalSiteId, StringComparison.Ordinal))
                CompleteOrder(v);
        }
    }

    /// <summary>Assign each pending order (highest priority first) to its nearest idle vehicle by graph distance.</summary>
    private void AssignIdle(IReadOnlyCollection<Vehicle> vehicles, RoadmapGraph graph)
    {
        var idle = vehicles.Where(v => v.IsIdle).ToList();
        if (idle.Count == 0)
            return;

        foreach (var order in _orders.Pending()) // already priority-ordered
        {
            if (idle.Count == 0)
                break;

            var best = idle
                .Select(v => (v, dist: graph.DistanceTo(v.SiteId, order.DestinationSiteId)))
                .Where(t => t.dist is not null)
                .OrderBy(t => t.dist!.Value)
                .ThenBy(t => t.v.Id, StringComparer.Ordinal)
                .Select(t => t.v)
                .FirstOrDefault();

            if (best is null || !_orders.TryTake(order.Id))
                continue; // no idle vehicle can reach this order's destination right now

            best.OrderId = order.Id;
            best.GoalSiteId = order.DestinationSiteId;
            best.Priority = order.Priority;
            idle.Remove(best);
            _logger.LogInformation("Dispatched order {OrderId} to vehicle {VehicleId} ({From} -> {To}).",
                order.Id, best.Id, best.SiteId, order.DestinationSiteId);
        }
    }

    private void CompleteOrder(Vehicle v)
    {
        if (v.OrderId is { } orderId)
        {
            _orders.Complete(orderId);
            _logger.LogInformation("Vehicle {VehicleId} completed order {OrderId} at {Site}.", v.Id, orderId, v.SiteId);
        }
        v.OrderId = null;
        v.GoalSiteId = null;
        v.Priority = 0;
    }
}
