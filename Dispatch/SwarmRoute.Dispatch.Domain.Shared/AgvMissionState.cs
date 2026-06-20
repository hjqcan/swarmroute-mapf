namespace SwarmRoute.Dispatch.Domain.Shared;

/// <summary>
/// The lifecycle stage of an AGV within an FMS dock-and-service mission (AGV 任務生命週期狀態).
/// <para>
/// The progression models the full warehouse cycle: a vehicle is dispatched toward a station, queues in a
/// pre-dock buffer, waits for dock admission, docks, services, undocks, then moves on to the next task or to
/// parking. The executor (Round 2) drives these transitions; this enum is the shared vocabulary.
/// </para>
/// </summary>
public enum AgvMissionState
{
    /// <summary>No task assigned; available for dispatch (閒置).</summary>
    Idle = 0,

    /// <summary>En route to the pre-dock buffer upstream of a target station (前往預停靠緩衝區).</summary>
    MovingToPreDockBuffer = 1,

    /// <summary>Staged in the pre-dock buffer, awaiting dock admission from the station scheduler (等待停靠准入).</summary>
    WaitingDockAdmission = 2,

    /// <summary>Admitted and moving from the buffer onto the dock point (停靠中).</summary>
    Docking = 3,

    /// <summary>Docked and performing the station service; immovable until complete (作業中).</summary>
    InService = 4,

    /// <summary>Service complete; backing off the dock point to clear it (脫離停靠中).</summary>
    Undocking = 5,

    /// <summary>Undocked and en route to the next assigned task (前往下一任務).</summary>
    MovingToNextTask = 6,

    /// <summary>No further task; en route to a parking slot to clear the transit core (前往停車位).</summary>
    MovingToParking = 7,

    /// <summary>Parked and idle, resting out of the way of traffic (已停泊閒置).</summary>
    IdleParked = 8,

    /// <summary>Faulted / disabled; not progressing the mission and must be routed around (故障).</summary>
    Faulted = 9
}
