using NetDevPack.Domain;
using NetDevPack.Messaging;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Events;
using SwarmRoute.TrafficControl.Domain.Services;
using SwarmRoute.TrafficControl.Domain.Shared;
using SwarmRoute.TrafficControl.Domain.ValueObjects;

namespace SwarmRoute.TrafficControl.Domain.Aggregates;

/// <summary>
/// The in-memory <b>authoritative</b> live reservation state — the DDD home of the original engine's
/// mutable <c>GraphMap._sites/_lines/_blocks</c> status + <c>_agvPathDic</c>, re-expressed as time-interval
/// leases. It is a singleton aggregate (single writer = TrafficControl, invariant I5); EF is used only for
/// snapshot/audit, never on the hot path (ADR-002 / R2).
/// </summary>
/// <remarks>
/// <para><b>Dual index.</b> Leases are indexed both by <see cref="ResourceRef"/> (sorted by interval start,
/// so free-interval math and conflict checks are local) and by <c>AgentId</c> (so release / RAG snapshot are
/// O(agent's leases)).</para>
/// <para><b>Invariant.</b> No two <em>conflicting</em> leases coexist: same resource, overlapping interval,
/// different agents. Same-agent overlapping/touching windows on the same resource are merged, not duplicated.
/// Every mutating method preserves this and bumps <see cref="IncrementStateVersion"/> for optimistic
/// concurrency.</para>
/// <para><b>v0 semantics.</b> A granted reservation covers the whole path timeline at once (≈ the original
/// whole-path lock) — but it is genuinely interval-based, so swapping in SIPP at v1 is a strategy change in
/// the allocator, not a model change here.</para>
/// </remarks>
public sealed class ReservationTable : Entity, IAggregateRoot
{
    private readonly object _sync = new();

    /// <summary>resource → leases on that resource, kept sorted by <c>Interval.StartMs</c>.</summary>
    private readonly Dictionary<ResourceRef, List<ResourceLease>> _byResource = new();

    /// <summary>agent → that agent's leases (insertion order).</summary>
    private readonly Dictionary<string, List<ResourceLease>> _byAgent = new(StringComparer.Ordinal);

    /// <summary>Contended (queued) requests, newest last. The "Waits" edges exposed to Deadlock.</summary>
    private readonly List<ReservationRequest> _contended = new();

    private readonly IResourceTopology _topology;

    /// <summary>Grant-time deadlock-prevention oracle. Default = <see cref="NullWouldCloseCycleDetector"/> (off).</summary>
    private readonly IWouldCloseCycleDetector _cycleDetector;

    /// <summary>Fast gate: false when prevention is the no-op default, so the off path never builds the RAG snapshot.</summary>
    private readonly bool _preventionEnabled;

    // EF / construction-from-host: a table with no Map topology wired (closure = identity), prevention off.
    private ReservationTable() : this(IResourceTopology.Empty) { }

    /// <summary>Creates an empty reservation table whose grant/release closure is computed from <paramref name="topology"/>,
    /// with grant-time cycle prevention OFF (v0/v1 behaviour).</summary>
    public ReservationTable(IResourceTopology topology)
        : this(topology, NullWouldCloseCycleDetector.Instance) { }

    /// <summary>
    /// Creates an empty reservation table whose grant/release closure is computed from <paramref name="topology"/>
    /// and whose grants are screened by <paramref name="cycleDetector"/> for would-be wait-for cycles (v2
    /// constructive prevention; pass <see cref="NullWouldCloseCycleDetector.Instance"/> to disable).
    /// </summary>
    public ReservationTable(IResourceTopology topology, IWouldCloseCycleDetector cycleDetector)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _cycleDetector = cycleDetector ?? throw new ArgumentNullException(nameof(cycleDetector));
        _preventionEnabled = cycleDetector is not NullWouldCloseCycleDetector;
        StateVersion = 0;
        StateChangedAtUtc = null;
    }

    /// <summary>Optimistic-concurrency version, bumped on every mutation (grukirbs convention).</summary>
    public long StateVersion { get; private set; }

    /// <summary>UTC of the last state change, or null if never mutated.</summary>
    public DateTimeOffset? StateChangedAtUtc { get; private set; }

    /// <summary>The active (non-free) leases across all resources — a stable snapshot copy.</summary>
    public IReadOnlyList<ResourceLease> ActiveLeases
    {
        get
        {
            lock (_sync)
            {
                return _byResource.Values.SelectMany(v => v).ToList();
            }
        }
    }

    /// <summary>The currently contended requests — a stable snapshot copy.</summary>
    public IReadOnlyList<ReservationRequest> ContendedRequests
    {
        get
        {
            lock (_sync)
            {
                return _contended.ToList();
            }
        }
    }

    /// <summary>
    /// Atomically copies and clears the aggregate's buffered domain events under the table lock.
    /// </summary>
    public IReadOnlyList<Event> DrainDomainEvents()
    {
        lock (_sync)
        {
            var events = DomainEvents;
            if (events is null || events.Count == 0)
                return [];

            var batch = events.ToList();
            ClearDomainEvents();
            return batch;
        }
    }

    /// <summary>
    /// Returns an immutable reservation view over the current leases using the same topology-closure resource
    /// semantics as the writer side.
    /// </summary>
    public IReservationView CreateSnapshotView()
    {
        lock (_sync)
        {
            return new SnapshotReservationView(
                _byResource.Values.SelectMany(v => v).ToList(),
                _topology);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Grant (ports GraphMap whole-path lock + pruning's "occupied by another / blacklisted" filter)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Attempts to reserve the whole <paramref name="path"/> for <paramref name="agentId"/>. Ports the
    /// original whole-path lock: every resource on the path — expanded to its <see cref="IResourceTopology"/>
    /// closure (parent block + interference) — must be free for this agent over the cell's interval, i.e. not
    /// held by another agent across an overlapping window and not blacklisted. If <em>all</em> are free the
    /// leases are created atomically and the result is <see cref="AllocationOutcome.Granted"/>; otherwise no
    /// lease is created, a contended <see cref="ReservationRequest"/> is recorded for each blocking resource,
    /// and the result is <see cref="AllocationOutcome.Blocked"/> (blacklist) or <see cref="AllocationOutcome.Queued"/>.
    /// </summary>
    public AllocationOutcome TryGrant(SpaceTimePath path, string agentId, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        if (path.Cells.Count == 0)
            return AllocationOutcome.Blocked;

        lock (_sync)
        {
            // Expand the path to its full closure of (resource, interval) cells.
            var requestedCells = ExpandClosure(path);

            var blacklisted = false;
            var contended = false;
            // Contended recording is DEFERRED so grant-time prevention can refuse without leaving a cycle-closing
            // "Waits" edge behind. `candidateWaitEdges` are the (owner, resource) this agent would block on.
            var toRecord = new List<(ResourceRef Resource, TimeInterval Interval)>();
            var candidateWaitEdges = new List<(string OwnerAgentId, ResourceRef Resource)>();

            foreach (var (resource, interval) in requestedCells)
            {
                if (_topology.IsBlacklisted(resource, agentId))
                {
                    blacklisted = true;
                    toRecord.Add((resource, interval));
                    continue;
                }

                var blockingLease = FindBlockingLease(resource, interval, agentId);
                if (blockingLease is not null)
                {
                    contended = true;
                    candidateWaitEdges.Add((blockingLease.AgentId, blockingLease.Resource));
                    toRecord.Add((blockingLease.Resource, interval));
                }
            }

            if (blacklisted || contended)
            {
                // (v2) Constructive deadlock prevention. When granting/queuing this contended path would close a
                // wait-for cycle AND this agent lacks right-of-way over every agent it would block on, REFUSE it:
                // record nothing (no cycle-closing edge) and return CycleAverted so the coordinator re-routes it.
                // Blacklist is a hard stop, never subject to this. The right-of-way gate (cheap) is checked before
                // the graph build; the whole block is skipped when prevention is off, so the off path is unchanged.
                if (contended && !blacklisted && _preventionEnabled
                    && LacksRightOfWayOverAll(agentId, candidateWaitEdges)
                    && _cycleDetector.WouldCloseCycle(BuildCurrentRagEdges(), agentId, candidateWaitEdges))
                {
                    Touch();
                    AddDomainEvent(new ReservationDeniedEvent(Id, agentId, requestedCells.Count, AllocationOutcome.CycleAverted.ToString()));
                    return AllocationOutcome.CycleAverted;
                }

                foreach (var (resource, interval) in toRecord)
                    RecordContended(resource, agentId, interval, priority);

                var outcome = blacklisted ? AllocationOutcome.Blocked : AllocationOutcome.Queued;
                Touch();
                AddDomainEvent(new ReservationDeniedEvent(Id, agentId, requestedCells.Count, outcome.ToString()));
                AddDomainEvent(new AllocationContendedEvent(Id, agentId, _contended.Count));
                return outcome;
            }

            // All free → create or merge leases for the whole closure (Reserved).
            var changedLeaseCount = 0;
            foreach (var (resource, interval) in requestedCells)
            {
                if (Insert(new ResourceLease(resource, agentId, interval, LeaseState.Reserved)))
                    changedLeaseCount++;
            }

            // A successful allocation means this agent is no longer waiting on any previous failed
            // candidate path. Keep the RAG Waits edges tied to current contention, not retry history.
            var pruned = _contended.RemoveAll(r => string.Equals(r.AgentId, agentId, StringComparison.Ordinal));
            pruned += PruneSatisfiedContendedRequests();

            if (changedLeaseCount > 0 || pruned > 0)
            {
                Touch();
                if (changedLeaseCount > 0)
                    AddDomainEvent(new ReservationGrantedEvent(Id, agentId, changedLeaseCount));
            }

            return AllocationOutcome.Granted;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Joint step (the host-seam atomic commit: PIBT's one-tick joint move, table as the authority)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Atomically reserves a one-tick <b>joint step</b> for a cluster: each <see cref="JointStepMove"/> claims its
    /// destination CP — and, when it actually moves, the lane it traverses — over the half-open window
    /// <c>[nowMs, nowMs+stepMs)</c>, expanded to the same topology closure (parent block + interference) as
    /// <see cref="TryGrant"/>. The commit is all-or-nothing: if <em>any</em> requested resource conflicts with
    /// another agent's existing lease over the window, or two moves by different agents would land on the same CP or
    /// swap a lane head-on, <em>nothing</em> is reserved and the result is <see cref="AllocationOutcome.Blocked"/>;
    /// otherwise every lease is created and the result is <see cref="AllocationOutcome.Granted"/>. This makes the
    /// reservation table the single authority for the joint step (closing the cross-tick soundness gap the
    /// executor-anchored resolver leaves open), so the autonomous loop can drive PIBT through it safely. The
    /// per-step windows are half-open, so a mover's lease expires on its own once the clock passes <c>nowMs+stepMs</c>.
    /// </summary>
    public AllocationOutcome TryGrantJointStep(IReadOnlyList<JointStepMove> moves, long nowMs, long stepMs)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (stepMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepMs), "A joint step must span a positive window.");
        if (moves.Count == 0)
            return AllocationOutcome.Granted; // an empty step trivially commits

        lock (_sync)
        {
            var window = new TimeInterval(nowMs, nowMs + stepMs);

            // The requested (resource, agent) cells: each mover's destination CP plus — only when it actually moves —
            // the lane it traverses, every cell expanded to its topology closure so blocks/interference are reserved
            // too, exactly like the whole-path TryGrant.
            var requested = new List<(ResourceRef Resource, string AgentId)>();
            foreach (var move in moves)
            {
                if (string.IsNullOrWhiteSpace(move.AgentId))
                    throw new ArgumentException("Every JointStepMove must carry an agentId.", nameof(moves));

                foreach (var member in _topology.ClosureOf(new ResourceRef(ResourceKind.CP, move.ToSiteId)))
                    requested.Add((member, move.AgentId));

                if (!string.Equals(move.FromSiteId, move.ToSiteId, StringComparison.Ordinal))
                    foreach (var member in _topology.ClosureOf(new ResourceRef(ResourceKind.Lane, $"{move.FromSiteId}-{move.ToSiteId}")))
                        requested.Add((member, move.AgentId));
            }

            // (a) No requested cell may conflict with a DIFFERENT agent's existing lease over the window.
            foreach (var (resource, agentId) in requested)
                if (FindBlockingLease(resource, window, agentId) is not null)
                    return AllocationOutcome.Blocked;

            // (b) No two moves by DIFFERENT agents may claim conflicting resources within the batch (vertex-distinct +
            //     no head-on lane swap). They all share the window, so a resource conflict across agents is a collision.
            for (var i = 0; i < requested.Count; i++)
                for (var j = i + 1; j < requested.Count; j++)
                    if (!string.Equals(requested[i].AgentId, requested[j].AgentId, StringComparison.Ordinal)
                        && ResourcesConflict(requested[i].Resource, requested[j].Resource))
                        return AllocationOutcome.Blocked;

            // All clear → create every lease over the window atomically (the pre-checks guarantee Insert never throws).
            var changed = 0;
            foreach (var (resource, agentId) in requested)
                if (Insert(new ResourceLease(resource, agentId, window, LeaseState.Reserved)))
                    changed++;

            // A granted joint step means each moving agent is no longer waiting on a previous failed candidate path —
            // mirror the per-path TryGrant success behaviour so the step leaves no phantom "Waits" edge in the snapshot
            // (Waits comes from `_contended`): drop every contended request for the distinct agents in this step, then
            // prune any other request the new leases now satisfy.
            var pruned = 0;
            foreach (var agentId in moves.Select(m => m.AgentId).Distinct(StringComparer.Ordinal))
                pruned += _contended.RemoveAll(r => string.Equals(r.AgentId, agentId, StringComparison.Ordinal));
            pruned += PruneSatisfiedContendedRequests();

            if (changed > 0 || pruned > 0)
            {
                Touch();
                if (changed > 0)
                    foreach (var agentId in moves.Select(m => m.AgentId).Distinct(StringComparer.Ordinal))
                        AddDomainEvent(new ReservationGrantedEvent(Id, agentId, changed));
            }

            return AllocationOutcome.Granted;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Release (ports UnlockPath — WITH the parent-block + interference closure leak FIXED)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Releases the leases this <paramref name="agentId"/> holds on the <paramref name="passedResources"/>
    /// it has driven past — and, crucially, on the full topology closure of each (parent block + interference).
    /// This is the corrected port of <c>GraphMap.UnlockPath</c>, whose ParentBlock/interference release was
    /// left commented out and therefore leaked. Returns the leases that were freed.
    /// </summary>
    public IReadOnlyList<ResourceLease> ReleaseBehind(string agentId, IReadOnlyList<ResourceRef> passedResources)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));
        ArgumentNullException.ThrowIfNull(passedResources);

        if (passedResources.Count == 0)
            return Array.Empty<ResourceLease>();

        lock (_sync)
        {
            // Expand the passed resources to their full closure so blocks/interference are released too.
            var toRelease = new HashSet<ResourceRef>();
            foreach (var resource in passedResources)
                foreach (var member in _topology.ClosureOf(resource))
                    toRelease.Add(member);

            var freed = RemoveWhere(agentId, lease => toRelease.Contains(lease.Resource));

            var pruned = PruneSatisfiedContendedRequests();

            if (freed.Count > 0 || pruned > 0)
            {
                Touch();
                if (freed.Count > 0)
                    AddDomainEvent(new ReservationReleasedEvent(Id, agentId, freed.Count, partial: true));
            }

            return freed;
        }
    }

    /// <summary>Releases every lease held by <paramref name="agentId"/> (e.g. on abort / completion). Returns freed leases.</summary>
    public IReadOnlyList<ResourceLease> ReleaseAll(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));

        lock (_sync)
        {
            var freed = RemoveWhere(agentId, _ => true);
            // Drop the agent's contended requests as well — it no longer waits on anything.
            var pruned = _contended.RemoveAll(r => string.Equals(r.AgentId, agentId, StringComparison.Ordinal));
            pruned += PruneSatisfiedContendedRequests();

            if (freed.Count > 0 || pruned > 0)
            {
                Touch();
                if (freed.Count > 0)
                    AddDomainEvent(new ReservationReleasedEvent(Id, agentId, freed.Count, partial: false));
            }

            return freed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Read view (the data SIPP will need at v1; the IReservationView is served from these)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The maximal conflict-free (safe) intervals for <paramref name="resource"/> across the whole fleet
    /// clock <c>[0, long.MaxValue)</c>, computed from the gaps between existing leases. Touching leases do not
    /// merge gaps (half-open semantics). The complement of the union of lease windows.
    /// </summary>
    public IReadOnlyList<SafeInterval> FreeIntervals(ResourceRef resource)
    {
        lock (_sync)
        {
            var result = new List<SafeInterval>();
            long cursor = 0;

            var leases = LeasesConflictingWith(resource)
                .OrderBy(l => l.Interval.StartMs)
                .ToList();

            if (leases.Count > 0)
            {
                // leases kept sorted by start; walk them merging overlaps to find gaps.
                long coveredEnd = long.MinValue;
                long segStart = 0;
                foreach (var lease in leases)
                {
                    var s = lease.Interval.StartMs;
                    var e = lease.Interval.EndMs;

                    if (s > coveredEnd)
                    {
                        // gap [cursor, s) is free (if non-empty)
                        if (s > cursor)
                            result.Add(new SafeInterval(resource, new TimeInterval(cursor, s)));
                        coveredEnd = e;
                        segStart = s;
                    }
                    else if (e > coveredEnd)
                    {
                        coveredEnd = e;
                    }

                    if (coveredEnd > cursor)
                        cursor = coveredEnd;
                    _ = segStart;
                }
            }

            // Tail: from the last covered instant to the end of time.
            if (cursor < long.MaxValue)
                result.Add(new SafeInterval(resource, new TimeInterval(cursor, long.MaxValue)));

            return result;
        }
    }

    /// <summary>
    /// True when <paramref name="resource"/> is entirely free over the whole half-open <paramref name="interval"/>
    /// — i.e. no lease by any agent overlaps it. (View semantics; agent-agnostic.)
    /// </summary>
    public bool IsFree(ResourceRef resource, TimeInterval interval)
    {
        lock (_sync)
        {
            return !LeasesConflictingWith(resource).Any(l => l.Interval.Overlaps(interval));
        }
    }

    /// <summary>
    /// True when <paramref name="resource"/> is free for <paramref name="agentId"/> over
    /// <paramref name="interval"/> — i.e. no <em>other</em> agent's lease overlaps it. The agent's own
    /// overlapping leases do not block it. Used by the allocator to compute the pruning set.
    /// </summary>
    public bool IsFreeForExcept(ResourceRef resource, TimeInterval interval, string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId must be provided.", nameof(agentId));
        lock (_sync)
        {
            return IsFreeFor(resource, interval, agentId);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Maintenance
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Evicts every lease whose window has fully elapsed at fleet-clock instant <paramref name="nowMs"/>
    /// (and prunes empty resource buckets). Used by <c>LeaseExpirySweepJob</c>. Returns the evicted leases.
    /// </summary>
    public IReadOnlyList<ResourceLease> Refresh(long nowMs)
    {
        lock (_sync)
        {
            var evicted = new List<ResourceLease>();
            foreach (var agentId in _byAgent.Keys.ToList())
            {
                var removed = RemoveWhere(agentId, lease => lease.HasExpiredAt(nowMs));
                evicted.AddRange(removed);
            }

            var pruned = PruneSatisfiedContendedRequests(nowMs);

            if (evicted.Count > 0 || pruned > 0)
                Touch();

            return evicted;
        }
    }

    /// <summary>Records (idempotently) a contended request; the "Waits" edge for Deadlock and the thing the escalation job ages.</summary>
    public void RecordContention(ReservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (AddOrKeepContended(request))
                Touch();
        }
    }

    /// <summary>Replaces the contended requests in bulk (used by the escalation job after aging them).</summary>
    public void ReplaceContended(IEnumerable<ReservationRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        lock (_sync)
        {
            _contended.Clear();
            foreach (var request in requests)
                AddOrKeepContended(request);
            PruneSatisfiedContendedRequests();
            Touch();
        }
    }

    /// <summary>
    /// Ages every contended request by <paramref name="agingSeconds"/> (incrementing <c>HadWaitedTime</c> so
    /// long-waiters eventually win the right-of-way tie-break → no starvation, invariant I7) and, if any
    /// remain, raises <c>AllocationContendedEvent</c> so Deadlock re-scans. Returns the number aged. Driven by
    /// <c>StaleRequestEscalationJob</c>.
    /// </summary>
    public int EscalateStaleRequests(int agingSeconds)
    {
        if (agingSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(agingSeconds));

        lock (_sync)
        {
            var pruned = PruneSatisfiedContendedRequests();
            if (pruned > 0)
                Touch();

            if (_contended.Count == 0)
                return 0;

            for (var i = 0; i < _contended.Count; i++)
                _contended[i] = _contended[i].AgedBy(agingSeconds);

            Touch();

            // One event per escalation pass; the longest-waiting agent is the canonical subject.
            var subject = _contended
                .OrderByDescending(r => r.HadWaitedTime)
                .ThenBy(r => r.AgentId, StringComparer.Ordinal)
                .First();
            AddDomainEvent(new AllocationContendedEvent(Id, subject.AgentId, _contended.Count));

            return _contended.Count;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    private List<(ResourceRef Resource, TimeInterval Interval)> ExpandClosure(SpaceTimePath path)
    {
        var cells = new List<(ResourceRef, TimeInterval)>();
        var seen = new HashSet<(ResourceRef, long, long)>();
        foreach (var cell in path.Cells)
        {
            foreach (var member in _topology.ClosureOf(cell.Resource))
            {
                var key = (member, cell.Interval.StartMs, cell.Interval.EndMs);
                if (seen.Add(key))
                    cells.Add((member, cell.Interval));
            }
        }
        return cells;
    }

    /// <summary>True when no <em>other</em> agent holds <paramref name="resource"/> over an overlapping window.</summary>
    private bool IsFreeFor(ResourceRef resource, TimeInterval interval, string agentId)
    {
        foreach (var lease in LeasesConflictingWith(resource))
        {
            if (!string.Equals(lease.AgentId, agentId, StringComparison.Ordinal)
                && lease.Interval.Overlaps(interval))
                return false;
        }
        return true;
    }

    private bool Insert(ResourceLease lease)
    {
        // Invariant guard: never insert a conflicting lease.
        foreach (var other in LeasesConflictingWith(lease.Resource))
        {
            if (!string.Equals(lease.AgentId, other.AgentId, StringComparison.Ordinal)
                && lease.Interval.Overlaps(other.Interval))
                throw new InvalidOperationException(
                    $"{TrafficControlErrorCodes.ConflictingLease}: {lease.Resource.Kind}:{lease.Resource.Id} " +
                    $"conflicts with {other.Resource.Kind}:{other.Resource.Id} held by {other.AgentId} over an overlapping interval.");
        }

        var mergedStart = lease.Interval.StartMs;
        var mergedEnd = lease.Interval.EndMs;

        if (!_byResource.TryGetValue(lease.Resource, out var existing))
        {
            existing = new List<ResourceLease>();
            _byResource[lease.Resource] = existing;
        }

        var mergees = existing
            .Where(l => string.Equals(l.AgentId, lease.AgentId, StringComparison.Ordinal)
                        && WindowsTouchOrOverlap(l.Interval, lease.Interval))
            .ToList();

        if (mergees.Count > 0)
        {
            if (mergees.Count == 1
                && mergees[0].State == lease.State
                && Covers(mergees[0].Interval, lease.Interval))
                return false;

            foreach (var mergee in mergees)
            {
                mergedStart = Math.Min(mergedStart, mergee.Interval.StartMs);
                mergedEnd = Math.Max(mergedEnd, mergee.Interval.EndMs);
                RemoveLeaseFromIndexes(mergee);
            }

            if (!_byResource.TryGetValue(lease.Resource, out existing))
            {
                existing = new List<ResourceLease>();
                _byResource[lease.Resource] = existing;
            }

            lease = new ResourceLease(lease.Resource, lease.AgentId, new TimeInterval(mergedStart, mergedEnd), lease.State);
        }

        AddLeaseToIndexes(lease, existing);
        return true;
    }

    private void AddLeaseToIndexes(ResourceLease lease, List<ResourceLease> resourceBucket)
    {
        // keep sorted by start for free-interval math
        var idx = resourceBucket.FindIndex(l => l.Interval.StartMs > lease.Interval.StartMs);
        if (idx < 0) resourceBucket.Add(lease);
        else resourceBucket.Insert(idx, lease);

        if (!_byAgent.TryGetValue(lease.AgentId, out var agentLeases))
        {
            agentLeases = new List<ResourceLease>();
            _byAgent[lease.AgentId] = agentLeases;
        }
        agentLeases.Add(lease);
    }

    private void RemoveLeaseFromIndexes(ResourceLease lease)
    {
        if (_byResource.TryGetValue(lease.Resource, out var bucket))
        {
            bucket.Remove(lease);
            if (bucket.Count == 0)
                _byResource.Remove(lease.Resource);
        }

        if (_byAgent.TryGetValue(lease.AgentId, out var agentLeases))
        {
            agentLeases.Remove(lease);
            if (agentLeases.Count == 0)
                _byAgent.Remove(lease.AgentId);
        }
    }

    private List<ResourceLease> RemoveWhere(string agentId, Func<ResourceLease, bool> predicate)
    {
        var freed = new List<ResourceLease>();
        if (!_byAgent.TryGetValue(agentId, out var agentLeases))
            return freed;

        var kept = new List<ResourceLease>();
        foreach (var lease in agentLeases)
        {
            if (predicate(lease))
            {
                freed.Add(lease);
                if (_byResource.TryGetValue(lease.Resource, out var bucket))
                {
                    bucket.Remove(lease);
                    if (bucket.Count == 0)
                        _byResource.Remove(lease.Resource);
                }
            }
            else
            {
                kept.Add(lease);
            }
        }

        if (kept.Count == 0) _byAgent.Remove(agentId);
        else _byAgent[agentId] = kept;

        return freed;
    }

    private void RecordContended(ResourceRef resource, string agentId, TimeInterval interval, int priority)
    {
        AddOrKeepContended(new ReservationRequest(
            agentId,
            resource,
            DateTime.UtcNow,
            estimateTime: (int)Math.Max(0, interval.Duration / 1000),
            hadWaitedTime: 0,
            requested: interval,
            priority: priority));
    }

    private ResourceLease? FindBlockingLease(ResourceRef resource, TimeInterval interval, string agentId)
        => LeasesConflictingWith(resource)
            .FirstOrDefault(lease =>
                !string.Equals(lease.AgentId, agentId, StringComparison.Ordinal)
                && lease.Interval.Overlaps(interval));

    /// <summary>
    /// Builds the live RAG edges for the prevention oracle: <c>Owns</c> = active leases, <c>Waits</c> = current
    /// contended requests — the same mapping <c>TrafficControlSnapshotProvider</c> exposes to the reactive
    /// detector, so prevention and detection reason over identical edges. Called under the lock, only when
    /// prevention is on and a path is contended.
    /// </summary>
    private ResourceAllocationGraphSnapshot BuildCurrentRagEdges()
    {
        var owns = new List<(string AgentId, ResourceRef Resource)>();
        foreach (var (_, leases) in _byResource)
            foreach (var lease in leases)
                owns.Add((lease.AgentId, lease.Resource));

        var waits = new List<(string AgentId, ResourceRef Resource)>(_contended.Count);
        foreach (var request in _contended)
            waits.Add((request.AgentId, request.Resource));

        return new ResourceAllocationGraphSnapshot(owns, waits);
    }

    /// <summary>
    /// True when <paramref name="candidateAgentId"/> lacks right-of-way over every agent it would block on — i.e.
    /// it is ordinally after all of them — so it is the one that must yield (re-route) to break the would-be
    /// cycle. The ordinally-smallest agent in any cycle therefore never yields, so the fleet always makes progress
    /// (no mutual back-off) and the choice is deterministic. (Priority/aging-aware right-of-way via
    /// <see cref="RightOfWay"/> is a refinement; leases do not carry the holder's priority today.)
    /// </summary>
    private static bool LacksRightOfWayOverAll(
        string candidateAgentId,
        IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges)
        => candidateWaitEdges.All(e => string.CompareOrdinal(candidateAgentId, e.OwnerAgentId) > 0);

    private IEnumerable<ResourceLease> LeasesConflictingWith(ResourceRef resource)
    {
        foreach (var (heldResource, leases) in _byResource)
        {
            if (!ResourcesConflict(resource, heldResource))
                continue;

            foreach (var lease in leases)
                yield return lease;
        }
    }

    private static bool ResourcesConflict(ResourceRef a, ResourceRef b)
        => a.Equals(b) || IsReversedLane(a, b);

    private static bool WindowsTouchOrOverlap(TimeInterval a, TimeInterval b)
        => a.StartMs <= b.EndMs && b.StartMs <= a.EndMs;

    private static bool Covers(TimeInterval outer, TimeInterval inner)
        => outer.StartMs <= inner.StartMs && outer.EndMs >= inner.EndMs;

    private static bool IsReversedLane(ResourceRef a, ResourceRef b)
    {
        if (a.Kind != ResourceKind.Lane || b.Kind != ResourceKind.Lane)
            return false;

        var dashA = a.Id.IndexOf('-');
        var dashB = b.Id.IndexOf('-');
        if (dashA <= 0 || dashB <= 0)
            return false;

        var aStart = a.Id.AsSpan(0, dashA);
        var aEnd = a.Id.AsSpan(dashA + 1);
        var bStart = b.Id.AsSpan(0, dashB);
        var bEnd = b.Id.AsSpan(dashB + 1);

        return aStart.SequenceEqual(bEnd) && aEnd.SequenceEqual(bStart);
    }

    private static IReadOnlyList<ResourceLease> BlockingLeasesFor(
        IReadOnlyCollection<ResourceLease> leases,
        IResourceTopology topology,
        ResourceRef resource)
    {
        var resources = topology.ClosureOf(resource);
        return leases
            .Where(lease => resources.Any(member => ResourcesConflict(member, lease.Resource)))
            .OrderBy(lease => lease.Interval.StartMs)
            .ToList();
    }

    private bool AddOrKeepContended(ReservationRequest request)
    {
        var existingIndex = _contended.FindIndex(existing =>
            string.Equals(existing.AgentId, request.AgentId, StringComparison.Ordinal)
            && existing.Resource.Equals(request.Resource));

        if (existingIndex >= 0)
        {
            var merged = _contended[existingIndex].MergedWith(request);
            if (merged.Equals(_contended[existingIndex]))
                return false;

            _contended[existingIndex] = merged;
            return true;
        }

        _contended.Add(request);
        return true;
    }

    private int PruneSatisfiedContendedRequests(long? nowMs = null)
        => _contended.RemoveAll(request =>
            (nowMs is not null && request.Requested.EndMs <= nowMs.Value)
            || IsFreeFor(request.Resource, request.Requested, request.AgentId));

    private void Touch()
    {
        IncrementStateVersion();
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    private void IncrementStateVersion() => StateVersion++;

    private sealed class SnapshotReservationView : IReservationView
    {
        private readonly IReadOnlyList<ResourceLease> _leases;
        private readonly IResourceTopology _topology;

        public SnapshotReservationView(
            IReadOnlyList<ResourceLease> leases,
            IResourceTopology topology)
        {
            _leases = leases;
            _topology = topology;
        }

        public IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource)
        {
            var result = new List<SafeInterval>();
            long cursor = 0;

            var leases = BlockingLeasesFor(_leases, _topology, resource);
            if (leases.Count > 0)
            {
                long coveredEnd = long.MinValue;
                foreach (var lease in leases)
                {
                    var s = lease.Interval.StartMs;
                    var e = lease.Interval.EndMs;

                    if (s > coveredEnd)
                    {
                        if (s > cursor)
                            result.Add(new SafeInterval(resource, new TimeInterval(cursor, s)));
                        coveredEnd = e;
                    }
                    else if (e > coveredEnd)
                    {
                        coveredEnd = e;
                    }

                    if (coveredEnd > cursor)
                        cursor = coveredEnd;
                }
            }

            if (cursor < long.MaxValue)
                result.Add(new SafeInterval(resource, new TimeInterval(cursor, long.MaxValue)));

            return result;
        }

        public bool IsFree(ResourceRef resource, TimeInterval interval)
            => !BlockingLeasesFor(_leases, _topology, resource)
                .Any(lease => lease.Interval.Overlaps(interval));
    }
}
