using SwarmRoute.Host.Adapters;
using SwarmRoute.Simulation.Application;

namespace SwarmRoute.Integration.Tests;

public sealed class SimulationEngineFactoryTests
{
    [Fact]
    public async Task InMemorySimulationEngine_ExposesDeadlockDriverHooks()
    {
        var field = new GridFieldFactory().BuildGrid(3, 3);
        await using var engine = new InMemorySimulationEngineFactory().Create(field.Graph);

        Assert.NotNull(engine.Redirects);
        Assert.Empty(engine.Redirects.ActiveRedirects);
        Assert.Empty(await engine.RecoverTick(CancellationToken.None));

        await engine.EscalateLivelock("ghost", CancellationToken.None);
    }
}
