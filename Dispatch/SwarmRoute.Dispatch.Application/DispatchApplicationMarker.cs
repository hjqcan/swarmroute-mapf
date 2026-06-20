namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// Assembly marker for the Dispatch application layer.
/// <para>
/// V1 "Contracts" round deliberately ships this layer empty: the concrete (synchronous, deterministic)
/// <c>IStationScheduler</c> / <c>IStationResourceCalendar</c> implementations are built in the Foundations phase
/// on top of the <c>TrafficControl</c> reservation seam. This type exists only so the project compiles and can be
/// referenced for DI assembly scanning later.
/// </para>
/// </summary>
public static class DispatchApplicationMarker
{
}
