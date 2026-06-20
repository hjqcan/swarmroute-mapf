namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// A single relocation directive produced by the parking manager when clearing a walled agent's path: move the
/// parked vehicle <see cref="AgentId"/> off the blocking site <see cref="FromSite"/> to the staging buffer
/// <see cref="ToBuffer"/> (停車重定位指令).
/// </summary>
/// <param name="AgentId">The parked vehicle that must yield.</param>
/// <param name="FromSite">The site the vehicle currently occupies (on the walled agent's path).</param>
/// <param name="ToBuffer">The buffer / parking site the vehicle should relocate to.</param>
public sealed record ParkingRelocation(string AgentId, string FromSite, string ToBuffer)
{
    /// <summary>The parked vehicle that must yield.</summary>
    public string AgentId { get; } = Validation.NotNullOrWhiteSpace(AgentId, nameof(AgentId));

    /// <summary>The site the vehicle currently occupies (on the walled agent's path).</summary>
    public string FromSite { get; } = Validation.NotNullOrWhiteSpace(FromSite, nameof(FromSite));

    /// <summary>The buffer / parking site the vehicle should relocate to.</summary>
    public string ToBuffer { get; } = Validation.NotNullOrWhiteSpace(ToBuffer, nameof(ToBuffer));
}
