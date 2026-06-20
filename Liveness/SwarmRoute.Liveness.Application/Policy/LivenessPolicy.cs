using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Domain.Detection;
using SwarmRoute.Liveness.Domain.Resolution;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Liveness.Application.Policy;

/// <summary>
/// The production <see cref="ILivenessPolicy"/>: the physical-standoff decision logic formerly inlined in the
/// simulation's <c>FleetLoopDriver</c>, lifted out as a pure, synchronous function of the per-tick
/// <see cref="LivenessSnapshot"/>. It owns no engine state and performs no I/O — it only classifies stalls and
/// returns <see cref="LivenessDirective"/>s; the executor performs every mutation (lease release, pose move,
/// cluster planning). Constructed once per run with the roadmap graph and <see cref="LivenessOptions"/>; its only
/// per-run memory is the hop-distance cache (one reverse-BFS per distinct goal), so it stays deterministic.
/// <para>
/// Consulted once per <see cref="LivenessPhase"/> per discrete tick, at the exact mechanism point each decision's
/// inputs become available, so the relocation is behaviour-preserving by construction:
/// <list type="bullet">
///   <item><see cref="LivenessPhase.BeforePlanning"/>: gatekeeper recovery (<see cref="RestoreGoal"/>) then parked
///     step-aside (<see cref="RelocateParked"/>).</item>
///   <item><see cref="LivenessPhase.ClusterFormation"/>: PIBT enter (<see cref="EnterJointResolver"/>) or CBS solve
///     (<see cref="SolveClusterJointly"/>) per the configured <see cref="JointResolverKind"/>.</item>
///   <item><see cref="LivenessPhase.JointDrive"/>: drive the joint-resolver (PIBT) agents one hop each
///     (<see cref="MoveTo"/>) and decide their episode exits (<see cref="ExitJointResolver"/>).</item>
///   <item><see cref="LivenessPhase.Advance"/>: the schedule-faithful per-agent yields (<see cref="YieldAndReplan"/>):
///     head-on yield and stall-reroute, with the head-on diagnostic.</item>
/// </list>
/// The parked-ahead reroute (a gate-time check against the live parked set), the greedy-gate standoff diagnostic,
/// and the en-route blocked-streak counter remain the executor's mechanism (the greedy advance outcome is resolved
/// sequentially inside the gate, so it is not a pure up-front decision).
/// </para>
/// </summary>
public sealed class LivenessPolicy : ILivenessPolicy
{
    private readonly RoadmapGraph _graph;
    private readonly LivenessOptions _options;

    // Hop-distance oracle, memoized for the whole run (one reverse-BFS per distinct goal). Mirrors the executor's
    // former pibtHopsCache; the sole piece of per-run policy memory.
    private readonly Dictionary<string, IReadOnlyDictionary<string, int>> _hopsCache =
        new(StringComparer.Ordinal);

    public LivenessPolicy(RoadmapGraph graph, LivenessOptions options)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public JointResolverKind JointResolver => _options.JointResolver;

    private IReadOnlyDictionary<string, int> HopsTo(string goal) =>
        _hopsCache.TryGetValue(goal, out var d) ? d : _hopsCache[goal] = HopDistances.To(_graph, goal);

    // Greedy one-hop toward `goal` via the memoized oracle: the out-neighbour of `from` STRICTLY closer to the goal
    // (least hops, ties by ordinal id), or null if none (at goal, or boxed). A pending agent's intended next cell.
    private string? NextHopToward(string from, string goal)
    {
        var hops = HopsTo(goal);
        if (!hops.TryGetValue(from, out var fromHops) || fromHops == 0)
            return null;
        string? best = null;
        var bestHops = int.MaxValue;
        foreach (var n in _graph.Neighbours(from))
            if (hops.TryGetValue(n, out var h) && h < fromHops
                && (h < bestHops || (h == bestHops && (best is null || string.CompareOrdinal(n, best) < 0))))
            {
                bestHops = h;
                best = n;
            }
        return best;
    }

    /// <inheritdoc />
    public IReadOnlyList<LivenessDirective> Evaluate(LivenessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Phase switch
        {
            LivenessPhase.BeforePlanning => EvaluateBeforePlanning(snapshot),
            LivenessPhase.ClusterFormation => EvaluateClusterFormation(snapshot),
            LivenessPhase.JointDrive => EvaluateJointDrive(snapshot),
            LivenessPhase.Advance => EvaluateAdvance(snapshot),
            _ => Array.Empty<LivenessDirective>(),
        };
    }

    // ── Phase 1: gatekeeper recovery + parked step-aside (before plan+reserve) ─────────────────────────────────
    private IReadOnlyList<LivenessDirective> EvaluateBeforePlanning(LivenessSnapshot snapshot)
    {
        var directives = new List<LivenessDirective>();

        // Recover any gatekeeper whose yield window elapses this tick (its window ticks down to 0 and it currently
        // holds a redirect): it re-plans back to its own goal next cycle. Matches the executor's
        //   foreach p where YieldTicksRemaining > 0: if (--YieldTicksRemaining == 0 && RedirectTarget != null) restore.
        foreach (var a in snapshot.Agents)
            if (a.YieldTicksRemaining == 1 && a.HasActiveRedirect)
                directives.Add(new RestoreGoal(a.Id));

        // Parked step-aside (opt-in): relocate the finished vehicles walling a persistently-stuck waiting agent out
        // of its goal. Off by default — byte-identical when off.
        if (_options.StepAside)
        {
            var states = snapshot.Agents
                .Select(a => new RelocationAgentState(
                    a.Id, a.Position, a.Goal, a.StuckTicks, a.YieldTicksRemaining,
                    // (FMS InService gate) An in-service immovable vehicle is neither a walled goal-seeker to unblock
                    // nor a blocker to step aside; the selector also guards on Mobility. Default Movable ⇒ unchanged.
                    IsWalledCandidate: !a.Done && !a.EnRoute && !a.HoldingAtAvoidSite && !a.HasActiveRedirect
                        && a.Mobility != MobilityClass.ImmovableUntilServiceComplete,
                    IsParked: a.Done,
                    a.Mobility))
                .ToList();

            foreach (var r in ParkedRelocationSelector.Select(
                         states, snapshot.ParkedCells, _graph, _options.GatekeeperUnblockThreshold))
                directives.Add(new RelocateParked(r.BlockerId, r.Dest, _options.GatekeeperYieldWindow, r.WalledAgentId));
        }

        return directives;
    }

    // ── Phase 2: form physical-standoff clusters and hand them to the joint resolver (after plan+reserve) ──────
    private IReadOnlyList<LivenessDirective> EvaluateClusterFormation(LivenessSnapshot snapshot)
    {
        if (_options.JointResolver == JointResolverKind.None)
            return Array.Empty<LivenessDirective>();

        var clusters = AssembleClusters(snapshot);
        if (clusters.Count == 0)
            return Array.Empty<LivenessDirective>();

        var byId = snapshot.Agents.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
        var directives = new List<LivenessDirective>();

        if (_options.JointResolver == JointResolverKind.Pibt)
        {
            // PIBT-enter: every not-already-owned member joins the joint resolver (released + driven jointly).
            foreach (var cluster in clusters)
                foreach (var id in cluster)
                {
                    var a = byId[id];
                    if (a.InJointResolver || a.HasActiveRedirect)
                        continue; // already in PIBT, or owned by the deadlock-redirect machinery
                    if (a.Mobility == MobilityClass.ImmovableUntilServiceComplete)
                        continue; // (FMS InService gate) docked & in service ⇒ hard obstacle, never PIBT-driven
                    directives.Add(new EnterJointResolver(new[] { id }));
                }
        }
        else // JointResolverKind.Cbs
        {
            foreach (var cluster in clusters)
            {
                var members = cluster
                    .OrderBy(id => id, StringComparer.Ordinal)
                    // (FMS InService gate) never hand a docked, in-service immovable vehicle to CBS.
                    .Where(id => !byId[id].InJointResolver && !byId[id].HasActiveRedirect
                        && byId[id].Mobility != MobilityClass.ImmovableUntilServiceComplete)
                    .ToList();
                if (members.Count < 2)
                    continue;
                directives.Add(new SolveClusterJointly(members));
            }
        }

        return directives;
    }

    // ── Phase 3a (JointDrive): drive the joint-resolver (PIBT) agents one hop each ─────────────────────────────
    private IReadOnlyList<LivenessDirective> EvaluateJointDrive(LivenessSnapshot snapshot)
    {
        var pibtAgents = snapshot.Agents.Where(a => a.InJointResolver).ToList();
        if (pibtAgents.Count == 0)
            return Array.Empty<LivenessDirective>();

        var directives = new List<LivenessDirective>();

        // Blocked cells: every non-cluster agent's current cell PLUS the cells this tick's scheduled movers will
        // enter — so a PIBT hop never lands on / swaps with a flowing-fleet agent within the tick.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in snapshot.Agents)
            if (!a.InJointResolver)
                blocked.Add(a.Position);
        foreach (var a in snapshot.Agents)
            if (!a.InJointResolver && a.EnRoute && !a.Done && a.ScheduledToAdvance && a.EnRouteNextCell is not null)
                blocked.Add(a.EnRouteNextCell);

        var views = pibtAgents
            .Select(a => new PibtAgentView(a.Id, a.Position, a.EffectiveGoal, a.Priority, a.PibtHeldTicks))
            .ToList();
        var move = PibtZoneResolver.Resolve(views, blocked, _graph, HopsTo);

        foreach (var a in pibtAgents)
        {
            var to = move[a.Id];
            directives.Add(new MoveTo(a.Id, to));

            // Post-move counters (reproduced from the snapshot so the exit decision matches the executor's):
            //   episodeLeft' = PibtEpisodeTicksLeft - 1; moved ⇒ held' = 0 at `to`; else held' = PibtHeldTicks + 1.
            var moved = !string.Equals(to, a.Position, StringComparison.Ordinal);
            var newPos = moved ? to : a.Position;
            var heldAfter = moved ? 0 : a.PibtHeldTicks + 1;
            var episodeLeftAfter = a.PibtEpisodeTicksLeft - 1;

            if (string.Equals(newPos, a.Goal, StringComparison.Ordinal))
                directives.Add(new ExitJointResolver(a.Id, "reached goal"));
            else if (heldAfter >= _options.JointResolverHeldExitThreshold || episodeLeftAfter <= 0)
                directives.Add(new ExitJointResolver(
                    a.Id,
                    heldAfter >= _options.JointResolverHeldExitThreshold
                        ? $"held {heldAfter} ticks, not progressing"
                        : "episode budget elapsed"));
        }

        return directives;
    }

    // ── Phase 3b (Advance): the schedule-faithful per-agent stall-reroute / head-on yield ──────────────────────
    private IReadOnlyList<LivenessDirective> EvaluateAdvance(LivenessSnapshot snapshot)
    {
        // Only the schedule-faithful executor has a stall-reroute / head-on yield; the greedy gate resolves advances
        // sequentially, so its blocked-streak counter + standoff diagnostic stay the executor's mechanism.
        if (!snapshot.ScheduleFaithful)
            return Array.Empty<LivenessDirective>();

        var directives = new List<LivenessDirective>();
        var byId = snapshot.Agents.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);

        foreach (var a in snapshot.Agents)
        {
            // (FMS InService gate) a docked, in-service immovable vehicle is never yielded / re-planned — it is a
            // hard obstacle the rest of the fleet routes around. Default Movable ⇒ no agent is skipped here.
            if (a.Mobility == MobilityClass.ImmovableUntilServiceComplete)
                continue;

            if (!a.EnRoute || a.Done || a.AtRouteEnd)
                continue;

            // A parked vehicle ahead is the gate's parked-ahead reroute (it fires first, against the LIVE parked set,
            // and continues): do NOT also emit a stall here for that agent — it would double-handle it.
            if (a.NextCellIsParked)
                continue;

            if (a.ScheduledToAdvance)
                continue; // will step this tick

            // Did not advance. A planned wait (schedule says not yet) is normal; only a stall PAST the planned entry
            // tick is a real physical block to break.
            if (!a.ScheduledToMoveThisTick)
                continue;

            // BlockedTicks has already been incremented for this stalled agent by the executor's up-front pass, so
            // the threshold comparison matches the inline `BlockedTicks++; if (BlockedTicks >= threshold)`. The
            // partner is whoever physically occupies the target cell (the executor's tick-start occupantNow, which
            // — at this phase, after only the joint-resolver moved — equals the current positions for any EN-ROUTE
            // partner; a joint-resolver agent is never EnRoute, so it is never a head-on partner).
            var partner = OccupantView(a.EnRouteNextCell, byId);
            var headOn = partner is { EnRoute: true, Done: false }
                && partner.Value.EnRouteNextCell is not null
                && string.Equals(partner.Value.EnRouteNextCell, a.Position, StringComparison.Ordinal);
            var iYield = headOn && a.Priority >= partner!.Value.Priority;
            var threshold = iYield ? _options.HeadOnYieldThreshold : _options.StallRerouteThreshold;
            if (_options.JointResolver != JointResolverKind.None)
                threshold = Math.Max(_options.StallRerouteThreshold, _options.JointResolverTriggerThreshold) + 8;

            if (a.BlockedTicks >= threshold)
            {
                if (headOn)
                    directives.Add(new Diagnostic(
                        $"head-on@tick{snapshot.Tick}: {a.Id} ({a.Position}) yields/re-plans so " +
                        $"{partner!.Value.Id} can pass {a.Position}<->{a.EnRouteNextCell}."));
                directives.Add(new YieldAndReplan(a.Id, headOn ? YieldAndReplan.HeadOnReason : YieldAndReplan.StallReason));
            }
        }

        return directives;
    }

    // Cluster assembly shared by PIBT-enter and CBS, reproducing the executor's BuildStuckSnapshots + Assemble.
    private IReadOnlyList<IReadOnlySet<string>> AssembleClusters(LivenessSnapshot snapshot)
    {
        var occById = snapshot.Agents.ToDictionary(a => a.Position, a => a.Id, StringComparer.Ordinal);
        var stuckSnapshots = snapshot.Agents
            .Select(a =>
            {
                // (FMS InService gate) An in-service immovable vehicle is a hard obstacle: it is never a cluster
                // candidate, so it is neither PIBT-driven nor CBS-driven, and — like a parked/finished vehicle — a
                // neighbour blocked by it is left unlinked (deferred to step-aside, itself gated off for it). Default
                // Movable ⇒ this term is always true ⇒ cluster assembly is byte-identical to the pre-FMS path.
                var candidate = !a.Done && !a.InJointResolver && !a.HasActiveRedirect && !a.HoldingAtAvoidSite
                    && a.Mobility != MobilityClass.ImmovableUntilServiceComplete;
                // Candidate-gated intent (mirrors the executor's former BuildStuckSnapshots): en-route agents use
                // their committed next route CP; pending candidates the greedy next hop toward their effective goal.
                var intended = !candidate ? null
                    : a.EnRoute ? a.EnRouteNextCell
                    : NextHopToward(a.Position, a.EffectiveGoal);
                var stuck = candidate ? (a.EnRoute ? a.BlockedTicks : a.StuckTicks) : 0;
                return new StuckAgentSnapshot(a.Id, intended, stuck, candidate);
            })
            .ToList();
        return StuckClusterDetector.Assemble(stuckSnapshots, occById, _options.JointResolverTriggerThreshold);
    }

    // The agent physically occupying `cell` this tick (the executor's `occupantNow[cell]`), or null if empty.
    private static AgentLivenessView? OccupantView(string? cell, IReadOnlyDictionary<string, AgentLivenessView> byId)
    {
        if (cell is null)
            return null;
        foreach (var v in byId.Values)
            if (string.Equals(v.Position, cell, StringComparison.Ordinal))
                return v;
        return null;
    }
}
