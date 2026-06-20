using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Smoke tests for the V1 "Contracts" round: the Dispatch domain value records guard their invariants and the
/// frozen enums carry their expected members. The concrete scheduler/calendar behaviour is tested in later rounds.
/// </summary>
public sealed class ContractsFoundationTests
{
    [Fact]
    public void StationDefinition_RejectsNonPositiveServiceDuration()
    {
        Assert.Throws<ArgumentException>(() => new StationDefinition(
            StationId: "S1",
            DockPoint: "CP-dock",
            PreDockBuffers: new[] { "CP-buf" },
            BlockingClosure: new HashSet<ResourceRef> { new(ResourceKind.CP, "CP-dock") },
            ServiceDurationMs: 0,
            StationType: StationType.HardBlocking));
    }

    [Fact]
    public void StationDefinition_RoundTripsValidValues()
    {
        var closure = new HashSet<ResourceRef> { new(ResourceKind.CP, "CP-dock"), new(ResourceKind.Zone, "Z1") };

        var station = new StationDefinition(
            StationId: "S1",
            DockPoint: "CP-dock",
            PreDockBuffers: new[] { "CP-buf" },
            BlockingClosure: closure,
            ServiceDurationMs: 60_000,
            StationType: StationType.SoftBlocking);

        Assert.Equal("S1", station.StationId);
        Assert.Equal("CP-dock", station.DockPoint);
        Assert.Equal(60_000, station.ServiceDurationMs);
        Assert.Equal(StationType.SoftBlocking, station.StationType);
        Assert.Equal(2, station.BlockingClosure.Count);
    }

    [Fact]
    public void ServiceAdmissionRequest_RejectsDeadlineBeforeEarliestStart()
    {
        Assert.Throws<ArgumentException>(() => new ServiceAdmissionRequest(
            AgentId: "A1",
            StationId: "S1",
            PreDockBuffer: "CP-buf",
            DockPoint: "CP-dock",
            ServiceDurationMs: 1_000,
            Priority: 0,
            EarliestStartMs: 500,
            DeadlineMs: 100));
    }

    [Fact]
    public void ServiceAdmissionRequest_AllowsNullDeadline()
    {
        var request = new ServiceAdmissionRequest(
            AgentId: "A1",
            StationId: "S1",
            PreDockBuffer: "CP-buf",
            DockPoint: "CP-dock",
            ServiceDurationMs: 1_000,
            Priority: 3,
            EarliestStartMs: 500,
            DeadlineMs: null);

        Assert.Null(request.DeadlineMs);
        Assert.Equal(3, request.Priority);
    }

    [Fact]
    public void ServiceAdmissionRequest_RejectsNegativePriority()
    {
        Assert.Throws<ArgumentException>(() => new ServiceAdmissionRequest(
            AgentId: "A1",
            StationId: "S1",
            PreDockBuffer: "CP-buf",
            DockPoint: "CP-dock",
            ServiceDurationMs: 1_000,
            Priority: -1,
            EarliestStartMs: 0,
            DeadlineMs: null));
    }

    [Fact]
    public void ServiceAdmissionDecision_RejectsNullReason()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceAdmissionDecision(
            Granted: false,
            ServiceStartMs: null,
            Reason: null!,
            VehiclesToClearFirst: Array.Empty<string>()));
    }

    [Fact]
    public void TrafficImpact_RejectsNegativeWait()
    {
        Assert.Throws<ArgumentException>(() => new TrafficImpact(
            AffectedAgentIds: Array.Empty<string>(),
            BlocksTransitCore: true,
            HasBypass: false,
            EstWaitTicks: -5));
    }

    [Fact]
    public void EndpointSet_RejectsNullPartition()
    {
        Assert.Throws<ArgumentNullException>(() => new EndpointSet(
            Workstations: new HashSet<string>(),
            Parkings: null!,
            Buffers: new HashSet<string>(),
            Chargers: new HashSet<string>()));
    }

    [Fact]
    public void MobilityClass_HasFourMembers()
    {
        Assert.Equal(4, Enum.GetValues<MobilityClass>().Length);
        Assert.True(Enum.IsDefined(MobilityClass.ImmovableUntilServiceComplete));
    }

    [Fact]
    public void AgvMissionState_CoversFullDockAndServiceCycle()
    {
        Assert.Equal(10, Enum.GetValues<AgvMissionState>().Length);
        Assert.True(Enum.IsDefined(AgvMissionState.InService));
        Assert.True(Enum.IsDefined(AgvMissionState.IdleParked));
    }

    [Fact]
    public void StationType_HasThreeBlockingLevels()
    {
        Assert.Equal(3, Enum.GetValues<StationType>().Length);
    }
}
