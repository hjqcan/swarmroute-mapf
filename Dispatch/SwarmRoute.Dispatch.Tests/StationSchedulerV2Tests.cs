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
/// FMS-V2 upgrade of <see cref="StationScheduler"/>: the clearance-before-service gate and priority override that
/// switch on only when an <see cref="ITrafficImpactAnalyzer"/> is supplied. With no analyzer the scheduler is
/// exact V1 FCFS (asserted here against the same wiring as <see cref="StationSchedulerTests"/>).
/// </summary>
public sealed class StationSchedulerV2Tests
{
    private const long OneHourMs = 3_600_000;

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

    // ---- V1 parity: no analyzer => exact FCFS ------------------------------------------------------------

    [Fact]
    public async Task NoAnalyzer_GrantsFreeStation_ExactlyAsV1()
    {
        var scheduler = new StationScheduler(NewCalendar(), new InMemoryStationCatalog([Station()]));

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A"));

        Assert.True(decision.Granted);
        Assert.Equal(0, decision.ServiceStartMs);
        Assert.Equal("granted", decision.Reason);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task NoAnalyzer_NeverPopulatesClearanceBatch_EvenWhenBusy()
    {
        var scheduler = new StationScheduler(NewCalendar(), new InMemoryStationCatalog([Station()]));

        Assert.True((await scheduler.RequestDockAdmissionAsync(Request("AGV-A"))).Granted);
        var second = await scheduler.RequestDockAdmissionAsync(Request("AGV-B", earliestStartMs: OneHourMs / 2));

        Assert.False(second.Granted);
        Assert.Equal("station busy", second.Reason);
        Assert.Empty(second.VehiclesToClearFirst);
    }

    // ---- V2 gate: clearance-before-service ---------------------------------------------------------------

    [Fact]
    public async Task BlockedTransitCore_DeniesAndPopulatesClearanceBatch()
    {
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            AffectedAgentIds: new[] { "AGV-X", "AGV-Y" },
            BlocksTransitCore: true,
            HasBypass: false,
            EstWaitTicks: 3));

        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]), impact);

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A"));

        Assert.False(decision.Granted);
        Assert.Null(decision.ServiceStartMs);
        Assert.Equal("let affected vehicles pass first", decision.Reason);
        Assert.Equal(new[] { "AGV-X", "AGV-Y" }, decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task SeveredCore_IsNotPreemptedEvenByHigherPriority()
    {
        // Hard block (no bypass): priority must NOT override — the displaced traffic would be trapped.
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            AffectedAgentIds: new[] { "AGV-X" },
            BlocksTransitCore: true,
            HasBypass: false,
            EstWaitTicks: 1));
        var plan = new StubFleetPlan(priorities: new() { ["AGV-X"] = 1 });

        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]), impact, plan);

        // Requester far out-ranks the affected vehicle, yet the severed core still denies.
        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A", priority: 99));

        Assert.False(decision.Granted);
        Assert.Equal("let affected vehicles pass first", decision.Reason);
        Assert.Equal(new[] { "AGV-X" }, decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task NoAffectedVehicles_AdmitsThroughTheGate()
    {
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            AffectedAgentIds: Array.Empty<string>(),
            BlocksTransitCore: false,
            HasBypass: true,
            EstWaitTicks: 0));

        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]), impact);

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A"));

        Assert.True(decision.Granted);
        Assert.Equal(0, decision.ServiceStartMs);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    // ---- V2 gate: priority override (soft impact, bypass exists) -----------------------------------------

    [Fact]
    public async Task SoftImpact_WithoutPriorityOverride_DeniesAndClearsFirst()
    {
        // Soft block (bypass exists) but the requester does NOT outrank the affected vehicle -> yield to it.
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            AffectedAgentIds: new[] { "AGV-X" },
            BlocksTransitCore: false,
            HasBypass: true,
            EstWaitTicks: 1));
        var plan = new StubFleetPlan(priorities: new() { ["AGV-X"] = 5 });

        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]), impact, plan);

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A", priority: 5)); // equal, not strictly higher

        Assert.False(decision.Granted);
        Assert.Equal("let affected vehicles pass first", decision.Reason);
        Assert.Equal(new[] { "AGV-X" }, decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task SoftImpact_WithStrictPriorityOverrideAndBypass_StillReserves()
    {
        // Soft block (bypass exists) AND the requester strictly outranks every affected vehicle -> override.
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            AffectedAgentIds: new[] { "AGV-X", "AGV-Y" },
            BlocksTransitCore: false,
            HasBypass: true,
            EstWaitTicks: 2));
        var plan = new StubFleetPlan(priorities: new() { ["AGV-X"] = 3, ["AGV-Y"] = 7 });

        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station()]), impact, plan);

        var decision = await scheduler.RequestDockAdmissionAsync(Request("AGV-A", priority: 8)); // > both 3 and 7

        Assert.True(decision.Granted);
        Assert.Equal(0, decision.ServiceStartMs);
        Assert.Empty(decision.VehiclesToClearFirst);
    }

    [Fact]
    public async Task UnknownStation_StillDeniedBeforeTheGate()
    {
        var impact = new StubImpactAnalyzer(new TrafficImpact(
            Array.Empty<string>(), BlocksTransitCore: false, HasBypass: true, EstWaitTicks: 0));
        var scheduler = new StationScheduler(
            NewCalendar(), new InMemoryStationCatalog([Station("S1")]), impact);

        var decision = await scheduler.RequestDockAdmissionAsync(
            new ServiceAdmissionRequest(
                AgentId: "AGV-A", StationId: "S-unknown", PreDockBuffer: "CP-buf", DockPoint: "CP-dock",
                ServiceDurationMs: OneHourMs, Priority: 0, EarliestStartMs: 0, DeadlineMs: null));

        Assert.False(decision.Granted);
        Assert.Equal("unknown station", decision.Reason);
        Assert.False(impact.WasCalled); // gate is never consulted for an unknown station
    }

    /// <summary>An <see cref="ITrafficImpactAnalyzer"/> that returns a pre-canned impact and records invocation.</summary>
    private sealed class StubImpactAnalyzer(TrafficImpact impact) : ITrafficImpactAnalyzer
    {
        public bool WasCalled { get; private set; }

        public TrafficImpact AnalyzeBlockingImpact(
            IReadOnlySet<ResourceRef> blockingClosure,
            TimeInterval serviceWindow,
            IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> fleetPlannedResources)
        {
            WasCalled = true;
            return impact;
        }
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
