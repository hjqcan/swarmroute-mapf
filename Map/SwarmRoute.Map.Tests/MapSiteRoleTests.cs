using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Tests;

/// <summary>
/// Guards the additive FMS <see cref="SiteRole"/> lever on <see cref="MapSite"/>: existing call-sites
/// (which never pass a role) must default to <see cref="SiteRole.Transit"/>, and an explicitly supplied
/// role must round-trip onto the entity.
/// </summary>
public sealed class MapSiteRoleTests
{
    [Fact]
    public void Site_defaults_to_transit_role_when_role_is_not_supplied()
    {
        // Mirrors every existing call-site (e.g. Builders.Site) that omits the new last ctor param.
        var site = new MapSite("S1", MapSiteType.RelaySite, new MapPosition(0, 0));

        Assert.Equal(SiteRole.Transit, site.SiteRole);
    }

    [Theory]
    [InlineData(SiteRole.Transit)]
    [InlineData(SiteRole.Workstation)]
    [InlineData(SiteRole.Parking)]
    [InlineData(SiteRole.Charger)]
    [InlineData(SiteRole.Buffer)]
    [InlineData(SiteRole.PreDockBuffer)]
    [InlineData(SiteRole.DockPoint)]
    public void Site_round_trips_an_explicit_role(SiteRole role)
    {
        var site = new MapSite(
            "S1",
            MapSiteType.WorkSite,
            new MapPosition(1, 2),
            enable: true,
            interferenceSiteIds: null,
            interferenceLineIds: null,
            siteRole: role);

        Assert.Equal(role, site.SiteRole);
    }

    [Fact]
    public void Explicit_site_role_does_not_disturb_the_other_ctor_arguments()
    {
        var site = new MapSite(
            "S1",
            MapSiteType.WorkSite,
            new MapPosition(1, 2),
            enable: false,
            interferenceSiteIds: null,
            interferenceLineIds: null,
            siteRole: SiteRole.DockPoint);

        Assert.Equal("S1", site.SiteId);
        Assert.Equal(MapSiteType.WorkSite, site.SiteType);
        Assert.False(site.Enable);
        Assert.Equal(SiteRole.DockPoint, site.SiteRole);
    }
}
