using SwarmRoute.Dispatch.Application;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// Behaviour of the FMS-V2 <see cref="TrafficImpactAnalyzer"/>: affected-agent detection, transit-core severing,
/// bypass survival, the wait estimate, and the byte-identical inert path (empty plan / zero window).
/// </summary>
public sealed class TrafficImpactAnalyzerTests
{
    private static ResourceRef Cp(string id) => new(ResourceKind.CP, id);

    private static IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> Plan(
        params (string Agent, ResourceRef[] Resources)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<ResourceRef>>(StringComparer.Ordinal);
        foreach (var (agent, resources) in entries)
            map[agent] = resources;
        return map;
    }

    private static readonly TimeInterval Window = new(0, 1_000);

    [Fact]
    public void AffectedAgents_AreThoseWhosePlanIntersectsTheClosure()
    {
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Ring());
        var closure = new HashSet<ResourceRef> { Cp("B") };

        var plan = Plan(
            ("AGV-1", [Cp("A"), Cp("B"), Cp("C")]), // passes through B -> affected
            ("AGV-2", [Cp("A"), Cp("D")]),          // avoids B -> not affected
            ("AGV-3", [Cp("B")]));                  // sits on B -> affected

        var impact = analyzer.AnalyzeBlockingImpact(closure, Window, plan);

        Assert.Equal(new[] { "AGV-1", "AGV-3" }, impact.AffectedAgentIds);
    }

    [Fact]
    public void AffectedAgents_AreReturnedInOrdinalOrder_Deterministically()
    {
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Ring());
        var closure = new HashSet<ResourceRef> { Cp("B") };

        // Insert out of order; output must still be ordinal-sorted and reproducible.
        var plan = Plan(
            ("AGV-9", [Cp("B")]),
            ("AGV-1", [Cp("B")]),
            ("AGV-5", [Cp("B")]));

        var impact = analyzer.AnalyzeBlockingImpact(closure, Window, plan);

        Assert.Equal(new[] { "AGV-1", "AGV-5", "AGV-9" }, impact.AffectedAgentIds);
    }

    [Fact]
    public void BlocksTransitCore_WhenClosureSeversTheBridge()
    {
        // Barbell: removing the bridge vertex C disconnects {A,B} from {D,E}.
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Barbell());
        var closure = new HashSet<ResourceRef> { Cp("C") };

        var impact = analyzer.AnalyzeBlockingImpact(
            closure, Window, Plan(("AGV-1", [Cp("C")])));

        Assert.True(impact.BlocksTransitCore);
        Assert.False(impact.HasBypass); // a severed core has no bypass
    }

    [Fact]
    public void HasBypass_WhenCoreStaysConnectedWithoutTheClosure()
    {
        // Ring: removing any single vertex leaves the rest connected, so a bypass survives.
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Ring());
        var closure = new HashSet<ResourceRef> { Cp("B") };

        var impact = analyzer.AnalyzeBlockingImpact(
            closure, Window, Plan(("AGV-1", [Cp("A"), Cp("B"), Cp("C")])));

        Assert.False(impact.BlocksTransitCore);
        Assert.True(impact.HasBypass);
        Assert.Contains("AGV-1", impact.AffectedAgentIds);
    }

    [Fact]
    public void EstWaitTicks_IsTheMaxAffectedTraversalInsideTheClosure()
    {
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Ring());
        var closure = new HashSet<ResourceRef> { Cp("B"), Cp("C") };

        var plan = Plan(
            ("AGV-1", [Cp("A"), Cp("B")]),          // 1 resource in closure
            ("AGV-2", [Cp("B"), Cp("C"), Cp("D")])); // 2 resources in closure -> the max

        var impact = analyzer.AnalyzeBlockingImpact(closure, Window, plan);

        Assert.Equal(2, impact.EstWaitTicks);
    }

    [Fact]
    public void EmptyFleetPlan_YieldsNoAffectedAgents_ButStillReportsConnectivity()
    {
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Barbell());
        var closure = new HashSet<ResourceRef> { Cp("C") };

        var impact = analyzer.AnalyzeBlockingImpact(
            closure,
            Window,
            new Dictionary<string, IReadOnlyList<ResourceRef>>());

        Assert.Empty(impact.AffectedAgentIds);
        Assert.Equal(0, impact.EstWaitTicks);
        // Connectivity is a property of the graph + closure, independent of whether any agent is planned there.
        Assert.True(impact.BlocksTransitCore);
    }

    [Fact]
    public void ZeroDurationWindow_HoldsNothing_SoAffectsNoOneAndSeversNothing()
    {
        var analyzer = new TrafficImpactAnalyzer(RoadmapGraphFixtures.Barbell());
        var closure = new HashSet<ResourceRef> { Cp("C") };
        var emptyWindow = new TimeInterval(500, 500);

        var impact = analyzer.AnalyzeBlockingImpact(
            closure, emptyWindow, Plan(("AGV-1", [Cp("C")])));

        Assert.Empty(impact.AffectedAgentIds);
        Assert.False(impact.BlocksTransitCore);
    }

    [Fact]
    public void NullGraph_IsRejected()
        => Assert.Throws<ArgumentNullException>(() => new TrafficImpactAnalyzer(null!));
}
