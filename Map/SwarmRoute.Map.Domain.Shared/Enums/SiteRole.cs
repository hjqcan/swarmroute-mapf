using System.ComponentModel;

namespace SwarmRoute.Map.Domain.Shared.Enums;

/// <summary>
/// FMS operational role of a roadmap site, used by the Dispatch context to reason about where vehicles
/// queue, dock, service, park and charge (FMS 站點作業角色).
/// <para>
/// This <b>complements</b> the existing <see cref="MapSiteType"/> (which captures the map-editor's site
/// classification) rather than replacing it: a single physical site can carry a <see cref="MapSiteType"/>
/// (topology authoring) and a <see cref="SiteRole"/> (FMS dispatch semantics). It is additive per ADR-F1 —
/// the <c>MapSite</c> aggregate is not touched in this round.
/// </para>
/// </summary>
public enum SiteRole
{
    /// <summary>Plain through-traffic waypoint on the transit core; vehicles pass but never service here (過境站點).</summary>
    [Description("過境站點")]
    Transit = 0,

    /// <summary>A workstation where a task is performed; arrival alone is not "done" — a service must run (工位).</summary>
    [Description("工位")]
    Workstation = 1,

    /// <summary>A long-term parking slot where an idle vehicle rests without blocking the transit core (停車位).</summary>
    [Description("停車位")]
    Parking = 2,

    /// <summary>A charging / battery-swap site (充電站).</summary>
    [Description("充電站")]
    Charger = 3,

    /// <summary>A general staging / buffer slot used to stage vehicles out of the way (緩衝區).</summary>
    [Description("緩衝區")]
    Buffer = 4,

    /// <summary>A buffer immediately upstream of a dock point where a vehicle waits for dock admission (預停靠緩衝區).</summary>
    [Description("預停靠緩衝區")]
    PreDockBuffer = 5,

    /// <summary>The exact control point a vehicle occupies while docked and in service at a station (停靠點).</summary>
    [Description("停靠點")]
    DockPoint = 6
}
