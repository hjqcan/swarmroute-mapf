using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Infra.BackgroundJobs;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class CalendarAndJobsTests
{
    private sealed class FixedClock : IFleetClock
    {
        public long NowMs { get; set; }
    }

    [Fact]
    public void Calendar_EarliestFreeStart_finds_the_first_fitting_window()
    {
        var calendar = new ReservationCalendar();
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 100, 200)), "AGV-A");

        // Need a 50ms window starting at/after 0: [0,100) fits -> 0.
        Assert.Equal(0, calendar.EarliestFreeStart(table, Cp("S1"), earliestStartMs: 0, durationMs: 50));

        // Need a 50ms window starting at/after 120: the [200,inf) interval fits -> 200.
        Assert.Equal(200, calendar.EarliestFreeStart(table, Cp("S1"), earliestStartMs: 120, durationMs: 50));
    }

    [Fact]
    public void LeaseExpirySweepJob_evicts_expired_leases()
    {
        var table = new ReservationTable(EmptyTopology);
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        table.TryGrant(Path(Cell(Cp("S2"), 0, 1000)), "AGV-A");

        var clock = new FixedClock { NowMs = 200 };
        var job = new LeaseExpirySweepJob(table, clock, NullLogger<LeaseExpirySweepJob>.Instance);

        var evicted = job.Sweep();

        Assert.Equal(1, evicted);
        Assert.Single(table.ActiveLeases);
        Assert.Equal(Cp("S2"), table.ActiveLeases[0].Resource);
    }

    [Fact]
    public void StaleRequestEscalationJob_ages_contended_requests_and_emits_event()
    {
        var table = new ReservationTable(EmptyTopology);
        // Create a contended request for AGV-B on S1 (held by AGV-A).
        table.TryGrant(Path(Cell(Cp("S1"), 0, 100)), "AGV-A");
        table.TryGrant(Path(Cell(Cp("S1"), 50, 150)), "AGV-B");
        table.ClearDomainEvents();

        var before = table.ContendedRequests.Single(r => r.AgentId == "AGV-B").HadWaitedTime;

        var job = new StaleRequestEscalationJob(table, NullLogger<StaleRequestEscalationJob>.Instance);
        var aged = job.Escalate(agingSeconds: 3);

        Assert.True(aged >= 1);
        var after = table.ContendedRequests.Single(r => r.AgentId == "AGV-B").HadWaitedTime;
        Assert.Equal(before + 3, after);

        // An AllocationContendedEvent was raised.
        Assert.Contains(table.DomainEvents!, e => e.GetType().Name == "AllocationContendedEvent");
    }
}
