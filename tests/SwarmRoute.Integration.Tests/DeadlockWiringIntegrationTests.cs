using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Deadlock.Application.Services;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// M3-lite — deadlock wiring. Feeds a 2-agent circular-wait <see cref="ResourceAllocationGraphSnapshot"/> into
/// the REAL Deadlock pipeline (<see cref="RagDeadlockDetector"/> + <see cref="AvoidanceDeadlockResolver"/> +
/// <see cref="DeadlockAppService"/>, wired exactly as <c>DeadlockNativeInjectorBootStrapper</c> does) and
/// asserts the cycle is detected and a <c>Deadlock.Case.ResolutionRequested</c> integration event is produced.
/// </summary>
public sealed class DeadlockWiringIntegrationTests
{
    private static (IDeadlockAppService Service, CapturingIntegrationEventPublisher Publisher) BuildPipeline()
    {
        var services = new ServiceCollection();

        var publisher = new CapturingIntegrationEventPublisher();
        services.AddSingleton<IIntegrationEventPublisher>(publisher);

        // The same registrations DeadlockNativeInjectorBootStrapper applies (the tested detector/resolver).
        services.AddScoped<IDeadlockDetector, RagDeadlockDetector>();
        services.AddScoped<IVictimSelector, DeterministicVictimSelector>();
        services.AddScoped<IDeadlockResolver, AvoidanceDeadlockResolver>();
        services.AddScoped<IAvoidancePointSelector, NullAvoidancePointSelector>();
        services.AddScoped<IDetourReservationService, NullDetourReservationService>();
        services.AddScoped<IClearanceConfirmer, NullClearanceConfirmer>();
        services.AddSingleton<IActiveResolutionRegistry, InMemoryActiveResolutionRegistry>();
        services.AddScoped<IDeadlockAppService, DeadlockAppService>();

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IDeadlockAppService>(), publisher);
    }

    [Fact]
    public async Task M3Lite_TwoAgentCircularWait_IsDetected_AndResolutionRequested()
    {
        var (service, publisher) = BuildPipeline();

        // Canonical 2-agent circular wait: A owns r1 waits r2; B owns r2 waits r1.
        var snapshot = new ResourceAllocationGraphSnapshot(
            Owns:
            [
                ("A", new ResourceRef(ResourceKind.CP, "r1")),
                ("B", new ResourceRef(ResourceKind.CP, "r2")),
            ],
            Waits:
            [
                ("A", new ResourceRef(ResourceKind.CP, "r2")),
                ("B", new ResourceRef(ResourceKind.CP, "r1")),
            ]);

        var report = await service.ScanAsync(snapshot);

        // The detector reports the cycle over both agents.
        Assert.True(report.HasDeadlock);
        var cycle = Assert.Single(report.Cycles);
        Assert.Equal(new[] { "A", "B" }, cycle.AgentIds.OrderBy(a => a).ToArray());

        // A victim was chosen (deterministic) and a resolution was requested on the bus.
        Assert.False(string.IsNullOrEmpty(cycle.VictimAgentId));
        Assert.Contains(publisher.Published, e => e is DeadlockCaseResolutionRequestedEvent);
    }

    [Fact]
    public async Task M3Lite_HealthySnapshot_ProducesNoDeadlock_AndNoEvents()
    {
        var (service, publisher) = BuildPipeline();

        var report = await service.ScanAsync(new ResourceAllocationGraphSnapshot([], []));

        Assert.False(report.HasDeadlock);
        Assert.Empty(report.Cycles);
        Assert.Empty(publisher.Published);
    }
}
