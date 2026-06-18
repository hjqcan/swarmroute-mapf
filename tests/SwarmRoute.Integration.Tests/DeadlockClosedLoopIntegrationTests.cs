using System.Collections.Generic;
using System.Linq;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// CLOSED-LOOP deadlock test: drives the REAL detect → resolve → recover pipeline end-to-end over the wired
/// services + in-process event bus (DoD §5). A genuine 2/3/4-vehicle circular wait is seeded into the
/// reservation table (each agent owns one resource and waits on the next — the partial-hold model a real fleet
/// or v1 SIPP produces; v0 whole-path locking can't form a cycle because a denied agent owns nothing). Then:
/// <list type="number">
///   <item>the contention event triggers <c>RagDeadlockDetector</c> → a cycle is detected;</item>
///   <item><c>AvoidanceDeadlockResolver</c> picks the deterministic victim, requests resolution and reserves a
///         detour — parking the avoidance plan at <c>ConfirmCleared</c> and opening it in the registry;</item>
///   <item>the Coordination consumer projects <c>Deadlock.Case.ResolutionRequested</c> into the redirect store;</item>
///   <item>the victim yields (its held resource is released — "moved to the avoid site"), clearing the cycle;</item>
///   <item><c>IDeadlockRecoveryService</c> drives <c>ConfirmCleared → Recover → Resolved</c> (real
///         snapshot-re-detecting clearance), emitting <c>Deadlock.Case.Resolved</c> and closing the case.</item>
/// </list>
/// </summary>
public sealed class DeadlockClosedLoopIntegrationTests
{
    private const string AvoidSite = "V";

    private static SpaceTimePath Reservation(string siteId, long startMs, long endMs)
        => new([new SpaceTimeCell(RoadmapGraph.SiteRef(siteId), new TimeInterval(startMs, endMs))]);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CircularWait_Detected_VictimRedirected_ThenRecovered(int n)
    {
        // The graph only needs to exist + contain the avoid site (detection works off the reservation snapshot,
        // and the fixed avoidance selector returns AvoidSite regardless).
        using var host = CoordinationTestHost.Build(
            FakeRoadmapQueryService.Graph([AvoidSite, "s0", "s1"], ("V", "s0"), ("s0", "s1")),
            avoidSite: AvoidSite);
        var traffic = host.Traffic;

        // Agent ids "A".."D" — the deterministic victim is the smallest, "A".
        var agents = Enumerable.Range(0, n).Select(i => ((char)('A' + i)).ToString()).ToList();
        const string victim = "A";

        // 1. Each agent owns one distinct resource r{i} over [0,100].
        for (var i = 0; i < n; i++)
            Assert.Equal(AllocationOutcome.Granted,
                await traffic.TryReserveAsync(Reservation($"r{i}", 0, 100), agents[i]));

        // 2. Each agent then waits on the NEXT agent's resource over an overlapping window — the last request
        //    closes the cycle (agent i waits r{(i+1) mod n}).
        for (var i = 0; i < n; i++)
        {
            var outcome = await traffic.TryReserveAsync(Reservation($"r{(i + 1) % n}", 50, 150), agents[i]);
            Assert.Equal(AllocationOutcome.Queued, outcome);
        }

        // 3. The cycle was detected and a resolution requested, synchronously through the bus.
        Assert.Contains(host.Events.Handled, e => e is DeadlockCaseDetectedEvent);
        Assert.Contains(host.Events.Handled, e => e is DeadlockCaseResolutionRequestedEvent);

        // 4. Coordination projected the resolution into the redirect store: victim "A" → the avoid site.
        Assert.True(host.Redirects.TryGetActiveRedirect(victim, out var intent),
            "expected an active redirect for the victim");
        Assert.Equal(AvoidSite, intent.AvoidSiteId);
        Assert.False(host.Redirects.IsRecovered(victim));

        // 5. The victim yields the contended corridor (releases its held resource — modelling "drove to the
        //    avoid site"), which breaks the circular wait.
        await traffic.ReleaseAsync(victim, [RoadmapGraph.SiteRef("r0")]);

        // 6. Recovery pump: the cycle is now clear, so the victim's plan drives ConfirmCleared → Recover →
        //    Resolved and the case closes.
        var recovered = await host.Recovery.TryRecoverAllAsync();

        Assert.Contains(victim, recovered);
        Assert.Contains(host.Events.Handled, e => e is DeadlockCaseResolvedEvent);
        Assert.True(host.Redirects.IsRecovered(victim));

        // 7. Idempotent: a second recovery pump finds nothing more to recover.
        Assert.Empty(await host.Recovery.TryRecoverAllAsync());
    }

    [Fact]
    public async Task WhileCycleStillClosed_RecoveryDoesNotFire()
    {
        using var host = CoordinationTestHost.Build(
            FakeRoadmapQueryService.Graph([AvoidSite, "s0", "s1"], ("V", "s0"), ("s0", "s1")),
            avoidSite: AvoidSite);
        var traffic = host.Traffic;

        Assert.Equal(AllocationOutcome.Granted, await traffic.TryReserveAsync(Reservation("r0", 0, 100), "A"));
        Assert.Equal(AllocationOutcome.Granted, await traffic.TryReserveAsync(Reservation("r1", 0, 100), "B"));
        Assert.Equal(AllocationOutcome.Queued, await traffic.TryReserveAsync(Reservation("r1", 50, 150), "A"));
        Assert.Equal(AllocationOutcome.Queued, await traffic.TryReserveAsync(Reservation("r0", 50, 150), "B"));

        Assert.Contains(host.Events.Handled, e => e is DeadlockCaseResolutionRequestedEvent);

        // The victim has NOT yielded yet → the cycle is still closed → recovery must not complete.
        var recovered = await host.Recovery.TryRecoverAllAsync();

        Assert.Empty(recovered);
        Assert.DoesNotContain(host.Events.Handled, e => e is DeadlockCaseResolvedEvent);
        Assert.False(host.Redirects.IsRecovered("A"));
    }
}
