using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Application.Services;
using SwarmRoute.Deadlock.Application.Subscribers;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Infra.CrossCutting.IoC;

/// <summary>
/// Registers the Deadlock bounded context's services into the host container, following the grukirbs
/// <c>*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)</c> convention.
/// </summary>
public static class DeadlockNativeInjectorBootStrapper
{
    /// <summary>
    /// Registers Deadlock domain services, the application service, and the contended-allocation
    /// subscriber.
    /// <para>
    /// The integration seams (<see cref="IAvoidancePointSelector"/>, <see cref="IDetourReservationService"/>,
    /// <see cref="IClearanceConfirmer"/>, <see cref="IDeadlockSnapshotProvider"/>) are registered with their
    /// <c>Null*</c> implementations via <c>TryAdd</c>, so the context is fully resolvable standalone while
    /// the Host (at integration) can override each with a real Map/TrafficControl-backed adapter simply by
    /// registering it first. The shared <c>IIntegrationEventPublisher</c> is intentionally NOT registered
    /// here — it is owned by the EventBus/Host wiring.
    /// </para>
    /// </summary>
    public static WebApplicationBuilder RegisterServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RegisterCore(builder.Services);
        return builder;
    }

    /// <summary>
    /// Web-agnostic overload used by non-web hosts and integration tests.
    /// </summary>
    public static IServiceCollection RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RegisterCore(services);
        return services;
    }

    private static void RegisterCore(IServiceCollection services)
    {
        // Domain - detection & resolution
        services.AddScoped<IDeadlockDetector, RagDeadlockDetector>();
        services.AddScoped<IVictimSelector, DeterministicVictimSelector>();
        services.AddScoped<IDeadlockResolver, AvoidanceDeadlockResolver>();

        // Integration seams - Null defaults (overridable by the Host at integration).
        services.TryAddScoped<IAvoidancePointSelector, NullAvoidancePointSelector>();
        services.TryAddScoped<IDetourReservationService, NullDetourReservationService>();
        services.TryAddScoped<IClearanceConfirmer, NullClearanceConfirmer>();
        services.TryAddScoped<IDeadlockSnapshotProvider, NullDeadlockSnapshotProvider>();

        // Application - service & subscriber
        services.AddScoped<IDeadlockAppService, DeadlockAppService>();
        services.AddScoped<AllocationContendedSubscriber>();
        services.AddScoped<IIntegrationEventHandler>(sp =>
            sp.GetRequiredService<AllocationContendedSubscriber>());
    }
}
