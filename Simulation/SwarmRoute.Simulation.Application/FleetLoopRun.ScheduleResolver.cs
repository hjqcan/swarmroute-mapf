namespace SwarmRoute.Simulation.Application;

internal sealed partial class FleetLoopRun
{
    /// <summary>
    /// Schedule-faithful advance resolution: returns the ids of en-route agents that step to their next CP this
    /// tick. An agent is a <i>candidate</i> when its planned arrival tick for the next CP has come (and the cell
    /// ahead is not a parked vehicle it must re-route around). Candidates are then pruned to a set whose post-move
    /// positions are all distinct: a candidate may follow a leader into the cell the leader vacates this same tick
    /// (back-to-back), but never step onto a cell a non-moving vehicle holds, and two candidates never take the
    /// same cell. Resolution iterates to a fixpoint, so revoking a blocked leader correctly blocks its follower in
    /// the next pass. The SIPP schedule is interval-exclusive, so in normal operation every candidate is granted;
    /// the pruning is a defensive guarantee that keeps execution collision-free even if reality diverges from the
    /// plan (a delayed or re-routed vehicle), with block (3) reporting any residual breach.
    /// </summary>
    private static HashSet<string> ResolveScheduleFaithfulAdvances(
        IReadOnlyList<RunAgent> fleet, long tick, IReadOnlySet<string> parkedCells, Action<string>? log = null)
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        var posById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ag in fleet)
        {
            posById[ag.Id] = ag.Position;
            if (ag is not { EnRoute: true, Done: false })
                continue;
            if (ag.Idx >= ag.CpRoute.Count - 1)
                continue; // at goal: parks, does not step
            if (ag.CpEntryTicks.Count != ag.CpRoute.Count)
                continue; // no schedule attached (e.g. just reset): do not step
            if (tick < ag.CpEntryTicks[ag.Idx + 1])
                continue; // planned wait this tick

            var to = ag.CpRoute[ag.Idx + 1];
            if (parkedCells.Contains(to))
                continue; // parked vehicle ahead: the main loop re-routes this agent rather than advancing it

            target[ag.Id] = to;
        }

        var granted = new HashSet<string>(target.Keys, StringComparer.Ordinal);
        var ordered = fleet
            .Where(a => target.ContainsKey(a.Id))
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        var changed = true;
        while (changed)
        {
            changed = false;

            // (a) Forbid 2-cycle swaps: two granted movers exchanging cells traverse one lane in OPPOSITE
            //     directions = a head-on edge collision the CP-distinctness pass below can't catch (their end
            //     cells ARE distinct). Hold BOTH (neither may take the other's cell); the stall-reroute then
            //     re-plans one of them around. (Longer rotations a→b→c→a are valid — no opposite traversal —
            //     and are NOT revoked.) Such a swap only arises when execution has desynced from the
            //     interval-exclusive plan (a delayed/re-routed vehicle); this is the hard executor backstop.
            var moverByPos = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in ordered)
                if (granted.Contains(a.Id))
                    moverByPos[posById[a.Id]] = a.Id;
            foreach (var a in ordered)
            {
                if (!granted.Contains(a.Id))
                    continue;
                if (moverByPos.TryGetValue(target[a.Id], out var other) && !string.Equals(other, a.Id, StringComparison.Ordinal)
                    && target.TryGetValue(other, out var otherTarget)
                    && string.Equals(otherTarget, posById[a.Id], StringComparison.Ordinal))
                {
                    granted.Remove(a.Id); // both halves revoke themselves (moverByPos snapshot stays valid this pass)
                    changed = true;
                    log?.Invoke($"swap-prevented@tick{tick}: {a.Id} ({posById[a.Id]}<->{target[a.Id]}) {other} — held to avoid a head-on edge collision.");
                }
            }
            if (changed)
                continue; // recompute after dropping swap pairs

            // (b) Forbid landing on a stayer's cell, or a cell another (higher-priority) mover already claimed.
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in fleet)
                if (!granted.Contains(a.Id))
                    blocked.Add(a.Position);

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in ordered)
            {
                if (!granted.Contains(a.Id))
                    continue;
                var to = target[a.Id];
                if (blocked.Contains(to) || !claimed.Add(to))
                {
                    granted.Remove(a.Id);
                    changed = true;
                }
            }
        }

        return granted;
    }
}
