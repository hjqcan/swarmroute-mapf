using Microsoft.AspNetCore.Mvc;
using SwarmRoute.Map.Application.Contract.Dtos;
using SwarmRoute.Map.Application.Contract.Services;

namespace SwarmRoute.Map.Api.Controllers;

/// <summary>
/// HTTP endpoints for the Map bounded context: import a roadmap topology, read it back, and inspect its
/// built graph summary.
/// </summary>
[ApiController]
[Route("api/maps")]
public sealed class MapsController : ControllerBase
{
    private readonly IMapAppService _mapAppService;

    public MapsController(IMapAppService mapAppService)
        => _mapAppService = mapAppService ?? throw new ArgumentNullException(nameof(mapAppService));

    /// <summary>Imports a roadmap topology from JSON.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RoadmapDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Import([FromBody] ImportRoadmapRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var roadmap = await _mapAppService.ImportAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = roadmap.Id }, roadmap);
        }
        catch (ArgumentException ex)
        {
            // Aggregate-invariant violations (dangling endpoints, duplicate ids, etc.) surface as 400.
            return BadRequest(new ProblemDetails { Title = "Invalid roadmap topology", Detail = ex.Message });
        }
    }

    /// <summary>Returns the full topology of a roadmap.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RoadmapDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var roadmap = await _mapAppService.GetAsync(id, cancellationToken);
        return roadmap is null ? NotFound() : Ok(roadmap);
    }

    /// <summary>Returns all roadmaps' topologies.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoadmapDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok(await _mapAppService.ListAsync(cancellationToken));

    /// <summary>Returns a summary of the built graph (vertices/edges/weights) for a roadmap.</summary>
    [HttpGet("{id:guid}/graph")]
    [ProducesResponseType(typeof(RoadmapGraphSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGraph(Guid id, CancellationToken cancellationToken)
    {
        var summary = await _mapAppService.GetGraphSummaryAsync(id, cancellationToken);
        return summary is null ? NotFound() : Ok(summary);
    }

    /// <summary>Re-publishes a roadmap (raises <c>Map.Roadmap.Published</c>).</summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
    {
        var ok = await _mapAppService.PublishAsync(id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Deletes a roadmap.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var ok = await _mapAppService.DeleteAsync(id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }
}
