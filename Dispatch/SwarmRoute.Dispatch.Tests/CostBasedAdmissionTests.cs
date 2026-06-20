using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Services;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// FMS-V3 cost-based admission: the opt-in <see cref="CostBasedAdmissionPolicy"/> scored in isolation, and wired
/// into <see cref="StationScheduler"/> via the appended optional ctor param. Asserts the two canonical cases —
/// a high-urgency service goes first over low-priority followers, a low-urgency service defers to high-priority
/// followers — plus weight tunability, the no-bypass penalty, and determinism.
/// </summary>
public sealed class CostBasedAdmissionTests
{
    private const long OneHourMs = 3_600_000;

    // ---- policy in isolation -----------------------------------------------------------------------------

    [Fact]
    public void HighUrgencyService_OverLowPriorityFollowers_Admits_GoFirst()
    {
        // A high-priority service blocking two LOW-priority followers (with a bypass) => high score => admit now.
        var policy = new CostBasedAdmissionPolicy(); // defaults
        var impact = SoftImpact("F1", "F2");
        var plan = Plan(("F1", 0), ("F2", 0));

        var scored = policy.Score(Request("AGV-svc", priority: 9), impact, plan);

        // value 9*10=90, minus 2 blocked *5 = 10, no high-priority blocked, bypass exists => 80 >= 0.
        Assert.True(scored.Admit);
        Assert.Equal(80, scored.Score);
        Assert.Empty(scored.VehiclesToClearFirst); // go first => nobody clears
    }

    [Fact]
    public void LowUrgencyService_BlockingHighPriorityFollower_Defers_ClearFirst()
    {
        // A low-priority service blocking even ONE high-priority follower => low score => defer, let it pass.
        var policy = new CostBasedAdmissionPolicy();
        var impact = SoftImpact("F-vip");
        var plan = Plan(("F-vip", 100)); // far out-ranks the service

        var scored = policy.Score(Request("AGV-svc", priority: 1), impact, plan);

        // value 1*10=10, minus 1 blocked*5=5, minus 1 high-priority*50=50 => -45 < 0 => defer.
        Assert.False(scored.Admit);
        Assert.Equal(-45, scored.Score);
        Assert.Equal(new[] { "F-vip" }, scored.VehiclesToClearFirst);
    }

    [Fact]
    public void NoBypassClosure_AddsTheNoBypassPenalty_AndDeters()
    {
        // Even a fairly high-priority service is deferred when the closure leaves the follower no bypass.
        var policy = new CostBasedAdmissionPolicy();
        var impactNoBypass = new TrafficImpact(
            AffectedAgentIds: new[] { "F1" },
            BlocksTransitCore: true,
            HasBypass: false,
            EstWaitTicks: 3);

        var scored = policy.Score(Request("AGV-svc", priority: 9), impactNoBypass, Plan(("F1", 0)));

        // value 90 - 1*5 - 0 - 1000 (no bypass) => -915 < 0 => defer.
        Assert.False(scored.Admit);
        Assert.Equal(90 - 5 - 1000, scored.Score);
        Assert.Equal(new[] { "F1" }, scored.VehiclesToClearFirst);
    }

    [Fact]
    public void NoAffectedVehicles_ScoresPurePositiveValue_AndAdmits()
    {
        var policy = new CostBasedAdmissionPolicy();
        var impact = new TrafficImpact(
            AffectedAgentIds: Array.Empty<string>(),
            BlocksTransitCore: false,
            HasBypass: true,
            EstWaitTicks: 0);

        var scored = policy.Score(Request("AGV-svc", priority: 4), impact, fleetPlan: null);

        Assert.True(scored.Admit);
        Assert.Equal(40, scored.Score); // 4*10, no penalties
        Assert.Empty(scored.VehiclesToClearFirst);
    }

    [Fact]
    public void Weights_AreHonored_SameInputsFlipWithDifferentWeights()
    {
        // With the default high-priority penalty (50) this DEFERS; crank the service urgency up and it ADMITS.
        var impact = SoftImpact("F-vip");
        var plan = Plan(("F-vip", 100));
        var request = Request("AGV-svc", priority: 2);

        var strict = new CostBasedAdmissionPolicy(); // urgency 10 => 2*10 - 5 - 50 = -35 => defer
        var generous = new CostBasedAdmissionPolicy(
            new CostBasedAdmissionWeights(ServiceUrgency: 100)); // 2*100 - 5 - 50 = 145 => admit

        Assert.False(strict.Score(request, impact, plan).Admit);
        Assert.True(generous.Score(request, impact, plan).Admit);
    }

    [Fact]
    public void AdmitThreshold_IsInclusive_ScoreEqualToThresholdAdmits()
    {
        // Tune so the score lands exactly on the threshold; >= admits.
        var impact = SoftImpact("F1");
        var plan = Plan(("F1", 0));
        // value 1*10=10, blocked 1*10=10 => score 0; threshold 0 => admit (inclusive).
        var weights = new CostBasedAdmissionWeights(
            ServiceUrgency: 10, BlockedPenalty: 10, HighPriorityPenalty: 0, NoBypassPenalty: 0, AdmitThreshold: 0);

        var scored = new CostBasedAdmissionPolicy(weights).Score(Request("AGV-svc", priority: 1), impact, plan);

        Assert.Equal(0, scored.Score);
        Assert.True(scored.Admit);
    }

    [Fact]
    public void UnknownFollowerPriority_IsNotCountedHighPriority()
    {
        // A follower with no plan entry is never "high priority"; only the blocked-count penalty applies.
        var policy = new CostBasedAdmissionPolicy();
        var impact = SoftImpact("F-unknown");

        var scored = policy.Score(Request("AGV-svc", priority: 1), impact, fleetPlan: null);

        // 1*10 - 1*5 - 0 (unknown not high) - 0 (bypass) => 5 >= 0 => admit.
        Assert.True(scored.Admit);
        Assert.Equal(5, scored.Score);
    }

    [Fact]
    public void Score_IsDeterministic_AcrossRepeatedCalls()
    {
        var policy = new CostBasedAdmissionPolicy();
        var impact = SoftImpact("F1", "F2");
        var plan = Plan(("F1", 0), ("F2", 7));
        var request = Request("AGV-svc", priority: 3);

        var first = policy.Score(request, impact, plan);
        for (var i = 0; i < 5; i++)
        {
            var again = policy.Score(request, impact, plan);
            Assert.Equal(first.Score, again.Score);
            Assert.Equal(first.Admit, again.Admit);
        }
    }

    // ---- wired into the scheduler (end-to-end, opt-in) ---------------------------------------------------

    [Fact]
    public async Task Scheduler_WithCostPolicy_HighUrgencyService_IsAdmitted()
    {
        // High-urgency service vs two low-priority followers (bypass) => the scheduler reserves the window.
        var impact = new StubImpactAnalyzer(SoftImpact("F1", "F2"));
        var plan = Plan(("F1", 0), ("F2", 0));
        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]),
            impactAnalyzer: impact, fleetPlan: plan, costPolicy: new CostBasedAdmissionPolicy());

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-svc", priority: 9));

        Assert.True(decision.Granted);
        Assert.Equal(0, decision.ServiceStartMs);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task Scheduler_WithCostPolicy_LowUrgencyService_DefersWithClearFirstBatch()
    {
        // Low-urgency service vs a high-priority follower => deny with the affected vehicle to clear first.
        var impact = new StubImpactAnalyzer(SoftImpact("F-vip"));
        var plan = Plan(("F-vip", 100));
        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]),
            impactAnalyzer: impact, fleetPlan: plan, costPolicy: new CostBasedAdmissionPolicy());

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-svc", priority: 1));

        Assert.False(decision.Granted);
        Assert.Null(decision.ServiceStartMs);
        Assert.Equal("let affected vehicles pass first", decision.Reason);
        Assert.Equal(new[] { "F-vip" }, decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task Scheduler_WithCostPolicy_OverridesV2BinaryGate_AdmitsSeveredCoreWhenScoreIsHigh()
    {
        // V2 NEVER preempts a severed core. With a generous-enough cost policy (no no-bypass penalty), the V3
        // score governs instead and a high-priority service is admitted even though BlocksTransitCore is true.
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            AffectedAgentIds: new[] { "F1" }, BlocksTransitCore: true, HasBypass: true, EstWaitTicks: 1));
        var plan = Plan(("F1", 0));
        var lenient = new CostBasedAdmissionWeights(
            ServiceUrgency: 100, BlockedPenalty: 1, HighPriorityPenalty: 1, NoBypassPenalty: 0);
        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]),
            impactAnalyzer: impact, fleetPlan: plan, costPolicy: new CostBasedAdmissionPolicy(lenient));

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-svc", priority: 9));

        // 9*100=900 - 1 blocked - 0 high (F1 prio 0 < 9) - 0 no-bypass => 899 >= 0 => admit.
        Assert.True(decision.Granted);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task Scheduler_WithCostPolicy_NoAffectedVehicles_StillAdmits()
    {
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            Array.Empty<string>(), BlocksTransitCore: false, HasBypass: true, EstWaitTicks: 0));
        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]),
            impactAnalyzer: impact, fleetPlan: null, costPolicy: new CostBasedAdmissionPolicy());

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-svc", priority: 0));

        Assert.True(decision.Granted);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    // ---- fixtures ----------------------------------------------------------------------------------------

    private static TrafficImpact SoftImpact(params string[] affected)
        => new(
            AffectedAgentIds: affected,
            BlocksTransitCore: false,
            HasBypass: true,
            EstWaitTicks: 1);

    private static StubFleetPlan Plan(params (string Id, int Priority)[] priorities)
        => new(priorities.ToDictionary(p => p.Id, p => p.Priority, StringComparer.Ordinal));

    private static StationDefinition Station(string stationId = "S1", string dockPoint = "CP-dock")
        => new(
            StationId: stationId,
            DockPoint: dockPoint,
            PreDockBuffers: new[] { "CP-buf" },
            BlockingClosure: new HashSet<ResourceRef>
            {
                new(ResourceKind.CP, dockPoint),
                new(ResourceKind.Zone, "Z1"),
            },
            ServiceDurationMs: OneHourMs,
            StationType: StationType.HardBlocking);

    private static ServiceAdmissionRequest Request(string agentId, int priority = 0, long earliestStartMs = 0)
        => new(
            AgentId: agentId,
            StationId: "S1",
            PreDockBuffer: "CP-buf",
            DockPoint: "CP-dock",
            ServiceDurationMs: OneHourMs,
            Priority: priority,
            EarliestStartMs: earliestStartMs,
            DeadlineMs: null);

    private static StationResourceCalendar NewCalendar()
    {
        var topology = IResourceTopology.Empty;
        var table = new ReservationTable(topology);
        var allocator = new ResourceAllocator(topology);
        var coordinator = new TrafficCoordinatorAppService(
            table, allocator, NullLogger<TrafficCoordinatorAppService>.Instance);
        return new StationResourceCalendar(coordinator);
    }

    /// <summary>An <see cref="ITrafficImpactAnalyzer"/> returning a pre-canned impact.</summary>
    private sealed class StubImpactAnalyzer(TrafficImpact impact) : ITrafficImpactAnalyzer
    {
        public TrafficImpact AnalyzeBlockingImpact(
            IReadOnlySet<ResourceRef> blockingClosure,
            TimeInterval serviceWindow,
            IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> fleetPlannedResources)
            => impact;
    }

    /// <summary>An <see cref="IFleetPlanProvider"/> exposing a fixed priority map and an empty plan.</summary>
    private sealed class StubFleetPlan(Dictionary<string, int> priorities) : IFleetPlanProvider
    {
        public IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> GetPlannedResources()
            => new Dictionary<string, IReadOnlyList<ResourceRef>>(StringComparer.Ordinal);

        public int? GetPriority(string agentId)
            => priorities.TryGetValue(agentId, out var p) ? p : null;
    }
}
