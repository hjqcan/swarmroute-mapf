namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — TraceEvent) Derives the standardized <b>event trace</b> for a run from the recorded timeline:
/// one <c>Planned</c> per AGV at the start, a <c>Moved</c> for every control-point hop (from → to, time-stamped), and
/// an <c>Arrived</c> when it reaches its goal. A pure, deterministic function of the frames + specs (no engine
/// instrumentation), so it never perturbs execution and is reproducible. Waits are implicit in the gaps between an
/// AGV's <c>Moved</c> events, which keeps the trace compact (transitions, not per-tick rows).
/// <para>
/// This covers the <b>movement lifecycle</b> events. The reservation-level events the doc also lists
/// (<c>ReservationGranted/Denied</c>, <c>RightOfWayGateHeld</c>, <c>DeadlockDetected</c>) happen inside the engine and
/// are not visible in the timeline, so they are a follow-up that needs executor instrumentation — deliberately not
/// faked here.
/// </para>
/// </summary>
public static class TraceEventBuilder
{
    public static IReadOnlyList<TraceEventDto> Build(FleetLoopResult loop, IReadOnlyList<FleetAgentSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(specs);

        var events = new List<TraceEventDto>(loop.Frames.Count + specs.Count);
        var startTick = loop.Frames.Count > 0 ? loop.Frames[0].Tick : 0;

        // Planned: one event per AGV at t0 (start → goal). Emitted first, before any Moved (which start at t ≥ 1).
        foreach (var spec in specs.OrderBy(s => s.Id, StringComparer.Ordinal))
            events.Add(new TraceEventDto(startTick, spec.Id, "Planned", spec.StartSiteId, spec.GoalSiteId));

        // Walk the frames in order; emit a Moved per hop and an Arrival once. Frames are already tick-ordered and the
        // positions are emitted in ordinal agent order, so the trace is deterministic without a re-sort.
        var prevPos = new Dictionary<string, string>(StringComparer.Ordinal);
        var arrived = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in loop.Frames)
            foreach (var p in frame.Positions.OrderBy(x => x.AgentId, StringComparer.Ordinal))
            {
                if (prevPos.TryGetValue(p.AgentId, out var prev) && !string.Equals(prev, p.SiteId, StringComparison.Ordinal))
                    events.Add(new TraceEventDto(frame.Tick, p.AgentId, "Moved", p.SiteId, prev));

                if (p.State == AgentMotionState.Arrived && arrived.Add(p.AgentId))
                    events.Add(new TraceEventDto(frame.Tick, p.AgentId, "Arrived", p.SiteId));

                prevPos[p.AgentId] = p.SiteId;
            }

        return events;
    }
}
