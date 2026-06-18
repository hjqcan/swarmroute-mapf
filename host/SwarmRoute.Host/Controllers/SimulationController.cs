using Microsoft.AspNetCore.Mvc;
using SwarmRoute.Simulation.Application;

namespace SwarmRoute.Host.Controllers;

/// <summary>
/// HTTP endpoint for the in-memory Simulation API: build a grid field, run the REAL engine
/// (PathPlanning + TrafficControl + Coordination) to completion as a collision-free multi-AGV simulation,
/// and return the field + per-agent paths + a tick-by-tick timeline for a frontend to replay. No database.
/// </summary>
[ApiController]
[Route("api/simulation")]
public sealed class SimulationController : ControllerBase
{
    private readonly ISimulationService _simulation;

    public SimulationController(ISimulationService simulation)
        => _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));

    /// <summary>Runs a simulation and returns the field, per-agent paths and the replay timeline.</summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(SimulationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Run([FromBody] SimulationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _simulation.RunAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid simulation request", Detail = ex.Message });
        }
    }
}
