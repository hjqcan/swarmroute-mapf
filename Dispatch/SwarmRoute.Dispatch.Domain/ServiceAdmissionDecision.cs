namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// The scheduler's verdict on a <see cref="ServiceAdmissionRequest"/> (停靠准入決定).
/// <para>
/// When <see cref="Granted"/> is <see langword="true"/>, <see cref="ServiceStartMs"/> carries the admitted start
/// instant and <see cref="VehiclesToClearFirst"/> is empty. When denied, <see cref="ServiceStartMs"/> is
/// <see langword="null"/> and <see cref="VehiclesToClearFirst"/> may list the blocking-closure occupants that
/// must clear before this request can be reconsidered (clearance-before-service).
/// </para>
/// </summary>
/// <param name="Granted">Whether the service window was granted.</param>
/// <param name="ServiceStartMs">The admitted service start instant when granted; otherwise <see langword="null"/>.</param>
/// <param name="Reason">Human-readable explanation of the decision (e.g. "granted", "closure occupied").</param>
/// <param name="VehiclesToClearFirst">Ids of vehicles that must clear the blocking closure before this request can be admitted.</param>
public sealed record ServiceAdmissionDecision(
    bool Granted,
    long? ServiceStartMs,
    string Reason,
    IReadOnlyList<string> VehiclesToClearFirst)
{
    /// <summary>Human-readable explanation of the decision.</summary>
    public string Reason { get; } = Validation.NotNull(Reason, nameof(Reason));

    /// <summary>Ids of vehicles that must clear the blocking closure before this request can be admitted.</summary>
    public IReadOnlyList<string> VehiclesToClearFirst { get; } =
        Validation.NotNull(VehiclesToClearFirst, nameof(VehiclesToClearFirst));
}
