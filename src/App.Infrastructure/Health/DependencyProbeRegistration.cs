using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WorkContinuity.Infrastructure.Health;

/// <summary>Composition-root registration for the dependency probe.</summary>
public static class DependencyProbeRegistration
{
    /// <summary>
    /// Registers a single long-lived <see cref="DependencyProbe"/> configured from
    /// <c>ConnectionStrings:Postgres</c>, the <c>ServiceBus</c> section and <c>AZURE_CLIENT_ID</c>.
    /// The host disposes it, and with it the Service Bus client, on shutdown.
    /// </summary>
    public static IServiceCollection AddDependencyProbe(
        this IServiceCollection services,
        IConfiguration configuration,
        ServiceBusReadinessMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(DependencyProbeOptions.FromConfiguration(configuration, mode));
        services.AddSingleton<DependencyProbe>();

        return services;
    }
}
