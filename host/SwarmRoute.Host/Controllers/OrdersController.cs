using Microsoft.AspNetCore.Mvc;
using SwarmRoute.Coordination.Application.Dispatch;

namespace SwarmRoute.Host.Controllers;

/// <summary>Submit a transport order (<c>POST</c>) or read order-book counts (<c>GET</c>). The order is queued
/// for the autonomous dispatcher (Track B), which assigns it to the nearest idle vehicle. Returns 503 when the
/// dispatcher fleet is not enabled (<c>Dispatcher:Enabled=false</c>).</summary>
[ApiController]
[Route("api/orders")]
public sealed class OrdersController(IServiceProvider services) : ControllerBase
{
    /// <summary>Request body for a new transport order.</summary>
    public sealed record OrderRequest(string Destination, int Priority = 0, string? Id = null);

    [HttpPost]
    [ProducesResponseType(typeof(TransportOrder), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit([FromBody] OrderRequest request, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(OrderBook)) is not OrderBook orders)
            return Problem("The autonomous dispatcher is not enabled (set Dispatcher:Enabled=true).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (services.GetService(typeof(DispatcherService)) is not DispatcherService dispatcher)
            return Problem("The autonomous dispatcher is not available.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (request is null || string.IsNullOrWhiteSpace(request.Destination))
            return BadRequest(new ProblemDetails { Title = "Invalid order", Detail = "A destination site id is required." });

        var destination = request.Destination.Trim();
        var destinationExists = await dispatcher.DestinationExistsAsync(destination, cancellationToken).ConfigureAwait(false);
        if (destinationExists is null)
            return Problem("The dispatcher roadmap graph is not available.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (destinationExists == false)
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid order",
                Detail = $"Destination site '{destination}' does not exist on the active roadmap.",
            });

        try
        {
            var accepted = orders.Submit(new TransportOrder(request.Id ?? string.Empty, destination, request.Priority));
            return Accepted(accepted);
        }
        catch (DuplicateOrderIdException ex)
        {
            return Conflict(new ProblemDetails { Title = "Duplicate order id", Detail = ex.Message });
        }
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Counts()
    {
        if (services.GetService(typeof(OrderBook)) is not OrderBook orders)
            return Problem("The autonomous dispatcher is not enabled (set Dispatcher:Enabled=true).",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var (pending, assigned, completed) = orders.Counts();
        return Ok(new { pending, assigned, completed });
    }
}
