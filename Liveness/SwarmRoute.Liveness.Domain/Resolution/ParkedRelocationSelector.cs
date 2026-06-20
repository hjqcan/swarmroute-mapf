using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Liveness.Domain.Resolution;

/// <summary>One parked-blocker relocation: send <see cref="BlockerId"/> aside to <see cref="Dest"/> so the
/// walled-out agent <see cref="WalledAgentId"/> can plan through the cell the blocker is vacating.</summary>
/// <param name="BlockerId">The finished (parked) vehicle to step aside.</param>
/// <param name="Dest">The free neighbour it is sent to (becomes its temporary goal).</param>
/// <param name="WalledAgentId">The walled-out agent this relocation unblocks (its stuck streak resets).</param>
public readonly record struct ParkedRelocation(string BlockerId, string Dest, string WalledAgentId);

/// <summary>One agent's state as the relocation selector needs it (a projection of the executor's run state).</summary>
/// <param name="Id">Stable agent id.</param>
/// <param name="Position">The CP the agent physically sits on this tick.</param>
/// <param name="Goal">The agent's goal CP.</param>
/// <param name="StuckTicks">Consecutive ticks a waiting agent has failed to obtain a progressing route.</param>
/// <param name="YieldTicksRemaining">Ticks left in a relocated gatekeeper's window (0 = not currently yielding).</param>
/// <param name="IsWalledCandidate">True when the agent is a waiting goal-seeker eligible to be unblocked
/// (not done, not en route, not holding aside, not redirecting).</param>
/// <param name="IsParked">True when the agent has arrived and is parked on its goal cell.</param>
/// <param name="Mobility">(FMS) The vehicle's mobility class. A vehicle that is
/// <see cref="MobilityClass.ImmovableUntilServiceComplete"/> (docked and in service) is a hard immovable obstacle
/// and is never sent aside as a parked blocker. The default <see cref="MobilityClass.Movable"/> keeps the
/// pre-FMS step-aside behaviour byte-identical.</param>
public readonly record struct RelocationAgentState(
    string Id, string Position, string Goal, int StuckTicks, int YieldTicksRemaining,
    bool IsWalledCandidate, bool IsParked, MobilityClass Mobility = MobilityClass.Movable);

/// <summary>
/// Pure selection of the parked-vehicle step-asides for one tick (the gatekeeper recovery's companion). A waiting
/// agent unplannable for <c>gatekeeperUnblockThreshold</c>+ ticks is walled out of its goal — typically by a
/// finished vehicle sitting on the only approach, which holds no lease so the deadlock detector cannot see it.
/// For each such agent, in priority order, this walks its shortest path and, for every parked vehicle on it (that
/// is not already yielding), picks the ordinal-first free non-parked non-goal neighbour to send it to. Sending a
/// blocker aside frees its cell so the next plan can route the walled agent through.
/// <para>
/// Extracted verbatim from the executor's inlined step-aside block so it stays a pure function over poses (no
/// engine, no I/O). It models the same running mutations the inline loop made — a freed cell leaves the parked set
/// (so a later walled agent sees it open), and an occupied aside cell is reserved (so two blockers never target the
/// same cell) — so the executor enacting the returned list reproduces the inline behaviour byte-for-byte.
/// </para>
/// </summary>
public static class ParkedRelocationSelector
{
    /// <summary>
    /// Returns the ordered relocations to enact this tick (empty when nothing is walled out or nowhere to step).
    /// </summary>
    /// <param name="agents">Per-agent state, already in the executor's stable fleet order (priority, then id).</param>
    /// <param name="parkedCells">Cells a finished vehicle is parked on.</param>
    /// <param name="graph">The roadmap (shortest paths + out-neighbours).</param>
    /// <param name="gatekeeperUnblockThreshold">A waiting agent stuck this many ticks is treated as walled out.</param>
    public static IReadOnlyList<ParkedRelocation> Select(
        IReadOnlyList<RelocationAgentState> agents,
        IReadOnlySet<string> parkedCells,
        RoadmapGraph graph,
        int gatekeeperUnblockThreshold)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(parkedCells);
        ArgumentNullException.ThrowIfNull(graph);

        var relocations = new List<ParkedRelocation>();

        // Working copies of the state the inline loop mutated as it ran, so processing order is reproduced exactly:
        //  - parked: a freed cell leaves the parked set (persists across walled agents);
        //  - position/yielding/parked-flag of a vehicle that gets relocated this tick (so it is not re-selected).
        var parked = new HashSet<string>(parkedCells, StringComparer.Ordinal);
        var positionOf = agents.ToDictionary(a => a.Id, a => a.Position, StringComparer.Ordinal);
        var isParkedNow = agents.ToDictionary(a => a.Id, a => a.IsParked, StringComparer.Ordinal);
        var yieldingNow = agents.ToDictionary(a => a.Id, a => a.YieldTicksRemaining > 0, StringComparer.Ordinal);

        // (FMS InService gate) A vehicle docked and in service is an immovable obstacle: it must never be picked as a
        // parked blocker to step aside. Default Movable ⇒ nothing is excluded ⇒ byte-identical to the pre-FMS path.
        var immovable = agents
            .Where(a => a.Mobility == MobilityClass.ImmovableUntilServiceComplete)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stuck in agents)
        {
            if (!stuck.IsWalledCandidate || stuck.StuckTicks < gatekeeperUnblockThreshold)
                continue;

            var path = graph.ShortestPath(stuck.Position, stuck.Goal);
            if (path is null)
                continue;

            // All agents' current physical cells (recomputed per walled agent, as the inline loop did), mutated
            // locally as blockers are sent aside so two never land on the same cell within this walled agent's scan.
            var occupiedCells = new HashSet<string>(positionOf.Values, StringComparer.Ordinal);

            foreach (var cell in path)
            {
                if (string.Equals(cell, stuck.Position, StringComparison.Ordinal)
                    || string.Equals(cell, stuck.Goal, StringComparison.Ordinal))
                    continue;

                // A finished vehicle parked on this approach cell that is not already yielding aside — and is not an
                // in-service immovable vehicle (those are hard obstacles, never relocated).
                string? blockerId = null;
                foreach (var a in agents)
                    if (isParkedNow[a.Id] && !yieldingNow[a.Id] && !immovable.Contains(a.Id)
                        && string.Equals(positionOf[a.Id], cell, StringComparison.Ordinal))
                    {
                        blockerId = a.Id;
                        break;
                    }
                if (blockerId is null)
                    continue;

                var aside = graph.Neighbours(cell)
                    .Where(n => !occupiedCells.Contains(n) && !parked.Contains(n)
                        && !string.Equals(n, stuck.Goal, StringComparison.Ordinal))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (aside is null)
                    continue; // nowhere to step — leave it; the walled agent stays reported as non-converged

                relocations.Add(new ParkedRelocation(blockerId, aside, stuck.Id));

                // Mirror the inline mutations so subsequent cells/walled-agents see the same world.
                parked.Remove(cell);
                occupiedCells.Add(aside);
                isParkedNow[blockerId] = false;     // p.Done = false
                yieldingNow[blockerId] = true;       // p.YieldTicksRemaining = window
                positionOf[blockerId] = cell;        // p.Start = cell (Position stays on the freed cell until it moves)
            }
        }

        return relocations;
    }
}
