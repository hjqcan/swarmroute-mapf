using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The Round-2 FMS implementation of the Coordination <see cref="IDockAdmissionController"/> port: it filters a
/// cycle's agent goals through the dock-admission policy before the coordination loop plans (停靠准入閘門實作).
/// <para>
/// <b>Why it lives in Dispatch.</b> The port is declared in Coordination (it deals in
/// <see cref="AgentGoal"/>); the admission <em>policy</em> — the station scheduler, calendar and catalog — lives
/// here in Dispatch. Per the FMS dependency-cycle rule the dependency points one way (Dispatch → Coordination), so
/// the policy reaches the loop without Coordination ever referencing Dispatch's application services.
/// </para>
/// <para>
/// <b>V1 behaviour.</b> For each goal whose destination site is a station dock point (resolved against the
/// <see cref="IStationCatalog"/>):
/// <list type="bullet">
///   <item>request admission via <see cref="IStationScheduler.RequestDockAdmissionAsync"/>;</item>
///   <item><b>granted</b> → keep the original dock-point goal;</item>
///   <item><b>denied</b> → rewrite the goal's <see cref="AgentGoal.ToSiteId"/> to the station's first pre-dock
///   buffer (hold the vehicle at the buffer) and add the station's
///   <see cref="StationDefinition.BlockingClosure"/> to the blocked set so the planner routes the rest of the
///   fleet around the contended dock (clearance-before-service, ADR-F3).</item>
/// </list>
/// Goals not bound to a station dock point pass through unchanged, and nothing is blocked on their account. The
/// pass is deterministic and total — output order matches input order, one output goal per input goal.
/// </para>
/// </summary>
public sealed class DockAdmissionController : IDockAdmissionController
{
    private readonly IStationScheduler _scheduler;
    private readonly IStationCatalog _catalog;

    /// <summary>Creates the controller over the dock-admission scheduler and the station catalog.</summary>
    /// <param name="scheduler">The admission policy each station-bound goal is evaluated against.</param>
    /// <param name="catalog">Resolves a goal's destination site to the station whose dock point it is.</param>
    public DockAdmissionController(IStationScheduler scheduler, IStationCatalog catalog)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public async Task<DockAdmissionResult> EvaluateAdmissionAsync(
        Guid roadmapId,
        IReadOnlyCollection<AgentGoal> goals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        // Reverse index: destination dock point -> its station. Built per call from the catalog snapshot so the
        // controller observes catalog changes and stays free of cached state. Ordinal keying matches the dock-point
        // control-point id space; if two stations ever share a dock point the first in catalog order wins (the
        // catalog already forbids duplicate station ids, the realistic uniqueness key).
        var byDockPoint = new Dictionary<string, StationDefinition>(StringComparer.Ordinal);
        foreach (var station in _catalog.Stations)
            byDockPoint.TryAdd(station.DockPoint, station);

        var admitted = new List<AgentGoal>(goals.Count);
        HashSet<ResourceRef>? blocked = null;

        foreach (var goal in goals)
        {
            // Not headed for a station dock point -> untouched, contributes no blocked resources.
            if (!byDockPoint.TryGetValue(goal.ToSiteId, out var station))
            {
                admitted.Add(goal);
                continue;
            }

            var decision = await _scheduler
                .RequestDockAdmissionAsync(ToRequest(goal, station), ct)
                .ConfigureAwait(false);

            if (decision.Granted)
            {
                // Admitted: keep driving to the dock point.
                admitted.Add(goal);
                continue;
            }

            // Denied: hold the vehicle at the pre-dock buffer (when the station defines one) and block the station's
            // closure so the planner routes everyone else around the contended dock.
            admitted.Add(HoldAtBuffer(goal, station));
            (blocked ??= []).UnionWith(station.BlockingClosure);
        }

        return new DockAdmissionResult(
            admitted,
            blocked is { Count: > 0 } ? blocked : EmptyBlocked);
    }

    /// <summary>
    /// Rewrites <paramref name="goal"/> to hold its vehicle at <paramref name="station"/>'s first pre-dock buffer.
    /// If the station defines no buffer there is nowhere to redirect to, so the original dock-point goal is kept
    /// (the closure is still blocked by the caller); a buffer that already equals the destination is likewise a
    /// no-op. Origin and priority are preserved so the cycle's deterministic ordering is undisturbed.
    /// </summary>
    private static AgentGoal HoldAtBuffer(AgentGoal goal, StationDefinition station)
    {
        if (station.PreDockBuffers.Count == 0)
            return goal;

        var buffer = station.PreDockBuffers[0];
        return string.Equals(buffer, goal.ToSiteId, StringComparison.Ordinal)
            ? goal
            : goal with { ToSiteId = buffer };
    }

    /// <summary>
    /// Builds the admission request for a station-bound goal. V1 stages the vehicle in the station's first pre-dock
    /// buffer (or, absent one, the dock point itself), asks for the station's nominal service duration from an
    /// earliest start of 0, and leaves priority/deadline at the V1 defaults — the scheduler admits first-come,
    /// first-served and defers priority/deadline weighing to FMS-V2.
    /// </summary>
    private static ServiceAdmissionRequest ToRequest(AgentGoal goal, StationDefinition station)
        => new(
            AgentId: goal.AgentId,
            StationId: station.StationId,
            PreDockBuffer: station.PreDockBuffers.Count > 0 ? station.PreDockBuffers[0] : station.DockPoint,
            DockPoint: station.DockPoint,
            ServiceDurationMs: station.ServiceDurationMs,
            Priority: 0,
            EarliestStartMs: 0,
            DeadlineMs: null);

    private static readonly IReadOnlySet<ResourceRef> EmptyBlocked = new HashSet<ResourceRef>();
}
