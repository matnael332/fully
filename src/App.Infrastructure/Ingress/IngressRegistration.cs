using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Infrastructure.Observability;

namespace WorkContinuity.Infrastructure.Ingress;

/// <summary>Composes the in-process ingress.</summary>
public static class IngressRegistration
{
    /// <summary>Registers the in-process ingress.</summary>
    /// <remarks>
    /// Requires <c>AddPersistence</c>, an <c>ITenantContextAccessor</c> and an <c>IClock</c>. Source
    /// verifiers are registered by each source's own module. Gate 2 registers none in production, so
    /// every real source fails closed until its connector Gate supplies a verifier.
    /// </remarks>
    public static IServiceCollection AddEventIngress(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<EventBackboneMetrics>();
        services.AddScoped<IngressConnectionResolver>();
        services.AddScoped<IEventIngress, EventIngressService>();
        return services;
    }
}
