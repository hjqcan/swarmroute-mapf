using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>Whether a constraint forbids occupying a control point (vertex) or traversing a directed lane (edge).</summary>
public enum CbsConstraintKind
{
    /// <summary>Agent must not OCCUPY the control point <c>Resource</c> during <c>Interval</c>.</summary>
    Vertex,

    /// <summary>Agent must not TRAVERSE the directed lane <c>Resource</c> (its own direction) during <c>Interval</c>.</summary>
    Edge
}

/// <summary>
/// A CBS high-level constraint: "<see cref="AgentId"/> must NOT occupy/traverse <see cref="Resource"/> during
/// <see cref="Interval"/>." At the discrete level <see cref="Interval"/> is always a single tick <c>[t, t+1)</c>,
/// but carried as a <see cref="TimeInterval"/> so the low level honours it uniformly as a busy window (via
/// <see cref="CbsConstraintView"/>) — and so the same record survives the continuous-time (CCBS) pillar, where
/// the window becomes a motion-dependent unsafe interval.
/// </summary>
public sealed record CbsConstraint(string AgentId, CbsConstraintKind Kind, ResourceRef Resource, TimeInterval Interval);
