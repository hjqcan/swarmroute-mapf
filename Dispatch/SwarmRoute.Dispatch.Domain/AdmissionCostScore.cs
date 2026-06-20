namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// The outcome of scoring a service admission against the transit it would block (准入成本評分結果): the computed
/// <see cref="Score"/>, the <see cref="Admit"/> verdict (score &gt;= threshold), and the affected vehicles that
/// must clear first when the service defers (<see cref="VehiclesToClearFirst"/>).
/// </summary>
/// <param name="Score">The cost-based admission score (value minus blocking penalties).</param>
/// <param name="Admit">Whether the score cleared the admit threshold (high score → admit now; low → defer).</param>
/// <param name="VehiclesToClearFirst">When deferring, the affected vehicles to let pass first; empty when admitting.</param>
public sealed record AdmissionCostScore(
    long Score,
    bool Admit,
    IReadOnlyList<string> VehiclesToClearFirst)
{
    /// <summary>When deferring, the affected vehicles to let pass first; empty when admitting.</summary>
    public IReadOnlyList<string> VehiclesToClearFirst { get; } =
        Validation.NotNull(VehiclesToClearFirst, nameof(VehiclesToClearFirst));
}
