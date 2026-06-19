using System.Collections.Generic;
using System.Linq;

namespace SwarmRoute.Deadlock.Application.Contract.Dtos;

/// <summary>
/// Result of scanning a Resource-Allocation-Graph snapshot for deadlocks: the set of circular waits
/// found (empty when the snapshot is healthy).
/// </summary>
public sealed record DeadlockReportDto
{
    /// <summary>The detected circular waits. Empty if no deadlock was found.</summary>
    public IReadOnlyList<DeadlockCycleDto> Cycles { get; init; } = [];

    /// <summary>True if at least one circular wait was detected.</summary>
    public bool HasDeadlock => Cycles.Count > 0;

    /// <summary>Total number of distinct deadlock cycles detected.</summary>
    public int CycleCount => Cycles.Count;

    /// <summary>All agents involved across all detected cycles (distinct).</summary>
    public IReadOnlyList<string> AffectedAgentIds =>
        Cycles.SelectMany(c => c.AgentIds).Distinct().ToList();

    /// <summary>An empty (healthy) report.</summary>
    public static DeadlockReportDto Empty { get; } = new();
}
