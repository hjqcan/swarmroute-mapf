namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// A vehicle's request to be admitted to a station's dock point for a service window (停靠准入請求).
/// <para>
/// The scheduler weighs the request against the station's blocking closure and calendar: it grants a
/// <see cref="ServiceDurationMs"/>-long lease no earlier than <see cref="EarliestStartMs"/>, ideally before
/// <see cref="DeadlineMs"/>, breaking contention by <see cref="Priority"/>.
/// </para>
/// </summary>
/// <param name="AgentId">The requesting vehicle's id.</param>
/// <param name="StationId">The target station's id.</param>
/// <param name="PreDockBuffer">The buffer site the vehicle is (or will be) staged in while awaiting admission.</param>
/// <param name="DockPoint">The dock-point control-point id to be leased for the service window.</param>
/// <param name="ServiceDurationMs">Requested service duration in fleet-clock milliseconds; must be &gt; 0.</param>
/// <param name="Priority">Admission priority; higher wins contention. Must be &gt;= 0.</param>
/// <param name="EarliestStartMs">Earliest fleet-clock instant the service may start; must be &gt;= 0.</param>
/// <param name="DeadlineMs">Optional soft deadline by which service should have started; when set must be &gt;= <paramref name="EarliestStartMs"/>.</param>
public sealed record ServiceAdmissionRequest(
    string AgentId,
    string StationId,
    string PreDockBuffer,
    string DockPoint,
    long ServiceDurationMs,
    int Priority,
    long EarliestStartMs,
    long? DeadlineMs)
{
    /// <summary>The requesting vehicle's id.</summary>
    public string AgentId { get; } = Validation.NotNullOrWhiteSpace(AgentId, nameof(AgentId));

    /// <summary>The target station's id.</summary>
    public string StationId { get; } = Validation.NotNullOrWhiteSpace(StationId, nameof(StationId));

    /// <summary>The buffer site the vehicle is (or will be) staged in while awaiting admission.</summary>
    public string PreDockBuffer { get; } = Validation.NotNullOrWhiteSpace(PreDockBuffer, nameof(PreDockBuffer));

    /// <summary>The dock-point control-point id to be leased for the service window.</summary>
    public string DockPoint { get; } = Validation.NotNullOrWhiteSpace(DockPoint, nameof(DockPoint));

    /// <summary>Requested service duration in fleet-clock milliseconds; always &gt; 0.</summary>
    public long ServiceDurationMs { get; } =
        Validation.Positive(ServiceDurationMs, nameof(ServiceDurationMs));

    /// <summary>Admission priority; higher wins contention. Always &gt;= 0.</summary>
    public int Priority { get; } = Validation.NotNegative(Priority, nameof(Priority));

    /// <summary>Earliest fleet-clock instant the service may start; always &gt;= 0.</summary>
    public long EarliestStartMs { get; } = Validation.NotNegative(EarliestStartMs, nameof(EarliestStartMs));

    /// <summary>Optional soft deadline by which service should have started; when set, never precedes <see cref="EarliestStartMs"/>.</summary>
    public long? DeadlineMs { get; } =
        Validation.DeadlineAtOrAfter(DeadlineMs, EarliestStartMs, nameof(DeadlineMs));
}
