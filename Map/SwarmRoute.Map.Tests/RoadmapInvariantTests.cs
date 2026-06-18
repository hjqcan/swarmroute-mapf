using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Events;
using SwarmRoute.Map.Domain.Shared;

namespace SwarmRoute.Map.Tests;

public sealed class RoadmapInvariantTests
{
    [Fact]
    public void Valid_topology_constructs_and_sets_version()
    {
        var roadmap = Builders.DiamondRoadmap();

        Assert.Equal(4, roadmap.Sites.Count);
        Assert.Equal(4, roadmap.Lines.Count);
        Assert.Equal(1, roadmap.StateVersion);
        Assert.NotNull(roadmap.StateChangedAtUtc);
    }

    [Fact]
    public void Dangling_line_start_endpoint_throws()
    {
        var sites = new[] { Builders.Site("A"), Builders.Site("B") };
        // "X" is not a site.
        var lines = new[] { Builders.Line("X-B", "X", "B", 1.0) };

        var ex = Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "bad", sites, lines));

        Assert.Contains(MapErrorCodes.DanglingLineEndpoint, ex.Message);
    }

    [Fact]
    public void Dangling_line_end_endpoint_throws()
    {
        var sites = new[] { Builders.Site("A"), Builders.Site("B") };
        var lines = new[] { Builders.Line("A-Z", "A", "Z", 1.0) };

        var ex = Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "bad", sites, lines));

        Assert.Contains(MapErrorCodes.DanglingLineEndpoint, ex.Message);
    }

    [Fact]
    public void Duplicate_site_id_throws()
    {
        var sites = new[] { Builders.Site("A"), Builders.Site("A") };
        var ex = Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "dup", sites, Array.Empty<MapLine>()));

        Assert.Contains(MapErrorCodes.DuplicateSiteId, ex.Message);
    }

    [Fact]
    public void Duplicate_line_id_throws()
    {
        var sites = new[] { Builders.Site("A"), Builders.Site("B") };
        var lines = new[]
        {
            Builders.Line("dup", "A", "B", 1.0),
            Builders.Line("dup", "B", "A", 1.0)
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "dup", sites, lines));

        Assert.Contains(MapErrorCodes.DuplicateLineId, ex.Message);
    }

    [Fact]
    public void Empty_site_set_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "empty", Array.Empty<MapSite>(), Array.Empty<MapLine>()));

        Assert.Contains(MapErrorCodes.RoadmapHasNoSites, ex.Message);
    }

    [Fact]
    public void Dangling_block_member_throws()
    {
        var sites = new[] { Builders.Site("A"), Builders.Site("B") };
        var lines = new[] { Builders.Line("A-B", "A", "B", 1.0) };
        var blocks = new[] { new MapBlock("blk", containedSiteIds: new[] { "A", "ghost" }) };

        var ex = Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "blk", sites, lines, blocks));

        Assert.Contains(MapErrorCodes.DanglingBlockMember, ex.Message);
    }

    [Fact]
    public void Empty_name_throws()
    {
        var sites = new[] { Builders.Site("A") };
        Assert.Throws<ArgumentException>(() =>
            new Roadmap(Guid.NewGuid(), "  ", sites, Array.Empty<MapLine>()));
    }

    [Fact]
    public void Rename_increments_version()
    {
        var roadmap = Builders.DiamondRoadmap();
        var before = roadmap.StateVersion;

        roadmap.Rename("renamed");

        Assert.Equal("renamed", roadmap.Name);
        Assert.Equal(before + 1, roadmap.StateVersion);
    }

    [Fact]
    public void ReplaceTopology_with_dangling_endpoint_throws_and_does_not_mutate()
    {
        var roadmap = Builders.DiamondRoadmap();
        var beforeVersion = roadmap.StateVersion;
        var beforeLineCount = roadmap.Lines.Count;

        var sites = new[] { Builders.Site("A"), Builders.Site("B") };
        var badLines = new[] { Builders.Line("A-Q", "A", "Q", 1.0) };

        Assert.Throws<ArgumentException>(() => roadmap.ReplaceTopology(sites, badLines));

        // Aggregate must be unchanged after a rejected replace.
        Assert.Equal(beforeVersion, roadmap.StateVersion);
        Assert.Equal(beforeLineCount, roadmap.Lines.Count);
    }

    [Fact]
    public void MarkPublished_raises_integration_event()
    {
        var roadmap = Builders.DiamondRoadmap();
        roadmap.MarkPublished();

        var evt = Assert.Single(roadmap.DomainEvents!);
        var published = Assert.IsType<MapRoadmapPublishedEvent>(evt);
        Assert.Equal("Map.Roadmap.Published", published.EventName);
        Assert.Equal("v1", published.Version);
    }
}
