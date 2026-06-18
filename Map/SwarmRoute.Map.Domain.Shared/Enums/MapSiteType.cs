using System.ComponentModel;

namespace SwarmRoute.Map.Domain.Shared.Enums;

/// <summary>
/// Functional classification of a roadmap site/station.
/// <para>
/// Ported from <c>AJR.MAPF.Map.MapSiteType</c>. The original source had a duplicate-value bug
/// (<c>RelaySite = 3</c> and <c>AvoidSite = 3</c>, with <c>DockSite = 4</c> colliding with the slot
/// <c>AvoidSite</c> should have occupied). This port FIXES that by renumbering so every member is
/// distinct and contiguous.
/// </para>
/// </summary>
public enum MapSiteType
{
    /// <summary>Charging / battery-swap station (充电/换电站点).</summary>
    [Description("充电/换电站点")]
    CPSite = 1,

    /// <summary>Work station where tasks are performed (工作站点).</summary>
    [Description("工作站点")]
    WorkSite = 2,

    /// <summary>Navigation / relay waypoint (导航站点) — formerly <c>RelaySite = 3</c>.</summary>
    [Description("导航站点")]
    RelaySite = 3,

    /// <summary>Avoidance / yield site used for deadlock resolution (避让站点) — formerly the duplicate <c>= 3</c>.</summary>
    [Description("避让站点")]
    AvoidSite = 4,

    /// <summary>Docking / parking site (停靠站点) — renumbered from <c>4</c> to keep all values distinct.</summary>
    [Description("停靠站点")]
    DockSite = 5
}
