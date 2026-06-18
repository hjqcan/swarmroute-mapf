using Microsoft.Extensions.Logging;

namespace SwarmRoute.TrafficControl.Application.Subscribers;

/// <summary>
/// Reacts to a contended allocation by signalling that the affected agent should replan (prune the contended
/// resources and search again). In the full system this is a hint to the Coordination control loop.
/// <para>
/// <b>v0 stub:</b> the CAP subscription (e.g. <c>[CapSubscribe("TrafficControl.Reservation.Denied")]</c>) and
/// the binding to Coordination's replan queue are wired at integration. The class compiles and is testable
/// standalone; only the transport binding is deferred.
/// </para>
/// </summary>
public sealed class ReplanTriggerSubscriber
{
    private readonly ILogger<ReplanTriggerSubscriber> _logger;

    public ReplanTriggerSubscriber(ILogger<ReplanTriggerSubscriber> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles a replan trigger for <paramref name="agentId"/>. v0: logs the request; integration wires this to
    /// Coordination so the agent reruns planning with the contended resources pruned.
    /// </summary>
    /// <remarks>
    /// TODO(integration): annotate with <c>[CapSubscribe("TrafficControl.Reservation.Denied")]</c>, accept the
    /// denied-event payload and enqueue a replan onto the Coordination loop.
    /// </remarks>
    public Task HandleAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        _logger.LogInformation("Replan requested for agent {AgentId} (allocation contended).", agentId);
        return Task.CompletedTask;
    }
}
