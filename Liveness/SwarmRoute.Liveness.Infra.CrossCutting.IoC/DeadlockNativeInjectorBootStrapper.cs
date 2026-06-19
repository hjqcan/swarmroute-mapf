using SwarmRoute.Liveness.Domain.Detection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Liveness.Domain.Services;

namespace SwarmRoute.Liveness.Infra.CrossCutting.IoC;

/// <summary>
/// Registers the Liveness bounded context's services into the host container, following the grukirbs
/// <c>*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)</c> convention.
/// </summary>
public static class DeadlockNativeInjectorBootStrapper
{
    /// <summary>
    /// Registers the RAG cycle detector, which serves the surviving liveness roles: grant-time prevention
    /// (<c>IWouldCloseCycleDetector</c>, consulted by TrafficControl) and post-hoc detection
    /// (<c>IDeadlockDetector.Detect</c>). The reactive redirect/recovery resolution machinery was removed.
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
        // The single RAG cycle-detection primitive (post-hoc detection role; the prevention role is wired by
        // TrafficControl / the simulation engine factory as IWouldCloseCycleDetector).
        services.AddScoped<IDeadlockDetector, RagCycleDetector>();
    }
}
