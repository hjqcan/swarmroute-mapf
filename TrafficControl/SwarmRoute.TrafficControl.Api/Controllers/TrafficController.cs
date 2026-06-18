using Microsoft.AspNetCore.Mvc;
using SwarmRoute.TrafficControl.Application.Contract.Dtos;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.TrafficControl.Api.Controllers;

/// <summary>
/// Operator HTTP endpoints for the TrafficControl bounded context: inspect live occupancy (the reservation
/// table's active leases + contended requests) and perform manual overrides (force-unlock an agent's holds).
/// The hot-path reserve/release seam (<see cref="ITrafficCoordinatorAppService"/>) is in-process only and is
/// deliberately <b>not</b> exposed over HTTP.
/// </summary>
[ApiController]
[Route("api/traffic")]
public sealed class TrafficController : ControllerBase
{
    private readonly ITrafficControlOperatorAppService _operator;
    private readonly ITrafficControlSnapshotProvider _snapshotProvider;

    public TrafficController(
        ITrafficControlOperatorAppService operatorService,
        ITrafficControlSnapshotProvider snapshotProvider)
    {
        _operator = operatorService ?? throw new ArgumentNullException(nameof(operatorService));
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    /// <summary>Returns the live occupancy: active leases, contended requests and the table state version.</summary>
    [HttpGet("occupancy")]
    [ProducesResponseType(typeof(OccupancyDto), StatusCodes.Status200OK)]
    public IActionResult GetOccupancy() => Ok(_operator.GetOccupancy());

    /// <summary>
    /// Returns the resource-allocation snapshot (Owns / Waits edges) — the same view the Deadlock context
    /// consumes. Useful for operators diagnosing contention.
    /// </summary>
    [HttpGet("allocation-graph")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAllocationGraph()
    {
        var snapshot = _snapshotProvider.GetSnapshot();
        return Ok(new
        {
            Owns = snapshot.Owns.Select(e => new { e.AgentId, e.ResourceId }),
            Waits = snapshot.Waits.Select(e => new { e.AgentId, e.ResourceId })
        });
    }

    /// <summary>
    /// Operator override: force-release the leases an agent holds on specific resources (with their
    /// parent-block + interference closure) or, when none are specified, all of the agent's leases. Returns the
    /// number of leases freed.
    /// </summary>
    [HttpPost("unlock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ManualUnlock([FromBody] ManualUnlockRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.AgentId))
            return BadRequest(new ProblemDetails { Title = "agentId is required" });

        var freed = _operator.ManualUnlock(request);
        return Ok(new { request.AgentId, FreedLeases = freed });
    }
}
