namespace SwarmRoute.Dispatch.Domain.Shared;

/// <summary>
/// How freely the Dispatch / Liveness layers may relocate a vehicle when resolving contention (車輛可移動性類別).
/// <para>
/// Only <see cref="Movable"/> and <see cref="MovableWithCost"/> are eligible for relocation; a vehicle that is
/// <see cref="ImmovableUntilServiceComplete"/> or <see cref="Faulted"/> must be treated as a fixed obstacle
/// (see ADR-F5).
/// </para>
/// </summary>
public enum MobilityClass
{
    /// <summary>Freely relocatable at no extra cost — the default for an idle, unladen vehicle (可自由移動).</summary>
    Movable = 0,

    /// <summary>Relocatable, but doing so incurs a cost (e.g. laden, mid-route, or near a deadline) the planner should weigh (可移動但有代價).</summary>
    MovableWithCost = 1,

    /// <summary>Docked and in service; must NOT be relocated until the service completes — treated as a hard obstacle (作業完成前不可移動).</summary>
    ImmovableUntilServiceComplete = 2,

    /// <summary>Faulted / disabled; cannot move under its own power and must be routed around (故障不可移動).</summary>
    Faulted = 3
}
