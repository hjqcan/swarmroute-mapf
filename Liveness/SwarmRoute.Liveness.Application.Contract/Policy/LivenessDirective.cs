namespace SwarmRoute.Liveness.Application.Contract.Policy;

/// <summary>
/// A command the liveness policy emits for the executor to apply. The policy decides (pure, synchronous);
/// the executor performs the mechanism (drop a lease, move a pose, call the cluster planner). Every variant
/// maps 1:1 to an existing mutation in the pre-refactor <c>FleetLoopDriver</c>.
/// </summary>
public abstract record LivenessDirective;

/// <summary>Drop the agent's lease and re-plan from its current pose (head-on yield, stall-reroute, parked-ahead reroute).</summary>
public sealed record YieldAndReplan(string AgentId, string Reason) : LivenessDirective
{
    /// <summary>A lower-priority agent yields a head-on swap (the executor resets its blocked streak on enacting).</summary>
    public const string HeadOnReason = "head-on-yield";

    /// <summary>A schedule-faithful agent stalled past its planned entry tick re-plans (resets its blocked streak).</summary>
    public const string StallReason = "stall-reroute";
}

/// <summary>Hand these agents to the joint resolver: release their stalled leases and begin a PIBT episode.</summary>
public sealed record EnterJointResolver(IReadOnlyList<string> AgentIds) : LivenessDirective;

/// <summary>Move one joint-resolver (PIBT) agent one hop to <paramref name="Cell"/> this tick (or hold when Cell == its current cell).</summary>
public sealed record MoveTo(string AgentId, string Cell) : LivenessDirective;

/// <summary>End the agent's joint-resolver episode (reached goal, held too long, or budget elapsed) — it re-plans normally next.</summary>
public sealed record ExitJointResolver(string AgentId, string Reason) : LivenessDirective;

/// <summary>Solve this standoff cluster jointly with CBS (the executor calls the cluster planner and reserves the result).</summary>
public sealed record SolveClusterJointly(IReadOnlyList<string> AgentIds) : LivenessDirective;

/// <summary>Relocate a parked blocker to <paramref name="Dest"/> for <paramref name="YieldWindow"/> ticks so the
/// walled-out agent <paramref name="WalledAgentId"/>'s goal approach opens (its stuck streak resets too).</summary>
public sealed record RelocateParked(string BlockerId, string Dest, int YieldWindow, string WalledAgentId) : LivenessDirective;

/// <summary>A relocated gatekeeper's yield window elapsed: let it re-plan back to its own goal.</summary>
public sealed record RestoreGoal(string AgentId) : LivenessDirective;

/// <summary>A human-facing standoff diagnostic the executor forwards to its log sink.</summary>
public sealed record Diagnostic(string Message) : LivenessDirective;
