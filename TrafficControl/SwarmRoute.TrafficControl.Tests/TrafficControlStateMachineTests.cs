using SwarmRoute.StateMachine.Core;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using SwarmRoute.TrafficControl.Domain.StateMachine;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class TrafficControlStateMachineTests
{
    [Fact]
    public async Task Happy_path_runs_Requested_to_Free()
    {
        var sm = new TrafficControlStateMachine();
        Assert.Equal(LeaseState.Requested, sm.CurrentState);

        Assert.True((await sm.FireAsync(LeaseTrigger.Grant)).Success);
        Assert.Equal(LeaseState.Reserved, sm.CurrentState);

        Assert.True((await sm.FireAsync(LeaseTrigger.Enter)).Success);
        Assert.Equal(LeaseState.InTransit, sm.CurrentState);

        Assert.True((await sm.FireAsync(LeaseTrigger.Pass)).Success);
        Assert.Equal(LeaseState.Releasing, sm.CurrentState);

        Assert.True((await sm.FireAsync(LeaseTrigger.Release)).Success);
        Assert.Equal(LeaseState.Free, sm.CurrentState);
    }

    [Fact]
    public async Task Invalid_transition_fails_without_changing_state()
    {
        var sm = new TrafficControlStateMachine();
        var result = await sm.FireAsync(LeaseTrigger.Enter); // not allowed from Requested

        Assert.False(result.Success);
        Assert.Equal(LeaseState.Requested, sm.CurrentState);
        Assert.Contains(TrafficControlErrorCodes.InvalidLeaseTransition, result.ErrorMessage);
    }

    [Fact]
    public async Task Grant_is_blocked_by_ResourceAvailableGuard_when_resource_is_busy()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-B"); // S1 busy for [0,100)

        var guards = new IStateGuard<LeaseState, LeaseTrigger>[] { new ResourceAvailableGuard() };
        var sm = new TrafficControlStateMachine(guards);

        var ctx = new LeaseTransitionContext(table, Cp("S1"), T(50, 150), "AGV-A");
        var result = await sm.FireAsync(LeaseTrigger.Grant, ctx);

        Assert.False(result.Success);
        Assert.Equal("ResourceAvailable", result.FailedGuardName);
        Assert.Equal(LeaseState.Requested, sm.CurrentState);
    }

    [Fact]
    public async Task Grant_is_blocked_by_NotBlacklistedGuard()
    {
        var topology = new Application.Topology.DictionaryResourceTopology.Builder()
            .WithBlacklist(Cp("S1"), "AGV-A")
            .Build();
        var table = new ReservationTable(topology);

        var guards = new IStateGuard<LeaseState, LeaseTrigger>[] { new NotBlacklistedGuard(topology) };
        var sm = new TrafficControlStateMachine(guards);

        var ctx = new LeaseTransitionContext(table, Cp("S1"), T(0, 100), "AGV-A");
        var result = await sm.FireAsync(LeaseTrigger.Grant, ctx);

        Assert.False(result.Success);
        Assert.Equal("NotBlacklisted", result.FailedGuardName);
    }

    [Fact]
    public async Task Grant_succeeds_when_NoConflictGuard_is_satisfied()
    {
        var table = new ReservationTable(EmptyTopology);
        var guards = new IStateGuard<LeaseState, LeaseTrigger>[] { new NoConflictGuard(new ConflictDetector(EmptyTopology)) };
        var sm = new TrafficControlStateMachine(guards);

        var ctx = new LeaseTransitionContext(table, Cp("S1"), T(0, 100), "AGV-A");
        var result = await sm.FireAsync(LeaseTrigger.Grant, ctx);

        Assert.True(result.Success);
        Assert.Equal(LeaseState.Reserved, sm.CurrentState);
    }
}
