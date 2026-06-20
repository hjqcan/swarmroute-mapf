using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (v4 SwarmRoute Lab — TraceEvent) The opt-in event trace driven through the REAL engine: off by default (omitted
/// from the JSON, byte-identical); on, a well-formed Planned/Moved/Arrived log consistent with the run's stats.
/// </summary>
public sealed class TraceClosedLoopTests
{
    private static SimulationService Svc() =>
        new(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);

    [Fact]
    public void Trace_is_off_by_default_and_omitted_from_the_json()
    {
        var off = Svc().RunAsync(new SimulationRequest(5, 5, 4, 7, PlannerKind.Sipp)).GetAwaiter().GetResult();

        Assert.Null(off.Trace);
        Assert.DoesNotContain("Trace", System.Text.Json.JsonSerializer.Serialize(off)); // no extra key → byte-identical
    }

    [Fact]
    public void Emit_trace_produces_a_well_formed_event_log()
    {
        var r = Svc().RunAsync(new SimulationRequest(5, 5, 4, 7, PlannerKind.Sipp, EmitTrace: true)).GetAwaiter().GetResult();

        Assert.NotNull(r.Trace);
        var trace = r.Trace!;
        Assert.Equal(4, trace.Count(e => e.Kind == "Planned"));            // one Planned per AGV
        Assert.Equal(r.Stats.Arrived, trace.Count(e => e.Kind == "Arrived")); // an Arrived per agent that reached its goal
        Assert.All(trace.Where(e => e.Kind == "Moved"), e =>
        {
            Assert.NotNull(e.FromSiteId);
            Assert.NotEqual(e.FromSiteId, e.SiteId);                       // a hop leaves one cell for another
        });
        for (var i = 1; i < trace.Count; i++)                             // tick-ordered
            Assert.True(trace[i].Tick >= trace[i - 1].Tick);
    }
}
