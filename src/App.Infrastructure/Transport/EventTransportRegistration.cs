using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>Composes the event transport.</summary>
/// <remarks>
/// Gate 2C composes and proves these parts but registers them in no host: no downstream handler exists
/// until Gate 2D, and a consumer with nothing to hand messages to must not run. Activating them in the
/// Worker is a later, separately approved step.
/// </remarks>
public static class EventTransportRegistration
{
    /// <summary>Registers the transport-independent parts: the outbox relay and the transport consumer.</summary>
    /// <remarks>
    /// Requires <c>AddPersistence</c>, the flow-scoped singleton <c>ITenantContextAccessor</c> the hosts
    /// register, an <c>IClock</c> and an <see cref="IEventTransportPublisher"/>, which
    /// <see cref="AddServiceBusEventTransport"/> provides. The consumer resolves an
    /// <see cref="ITransportMessageHandler"/> per message; its owner registers it.
    /// </remarks>
    public static IServiceCollection AddEventTransport(
        this IServiceCollection services,
        Action<EventTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new EventTransportOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton<EventBackboneMetrics>();
        services.AddScoped<OutboxTenantDiscovery>();
        services.AddSingleton<IOutboxRelay, OutboxRelay>();
        services.AddSingleton<IEventTransportConsumer, EventTransportConsumer>();
        return services;
    }

    /// <summary>
    /// Registers the Service Bus adapter: the publisher, the consumer host for <c>event-processing</c> and its
    /// dead-letter queue, sharing one client authenticated as readiness is.
    /// </summary>
    /// <exception cref="InvalidOperationException">Service Bus is not configured.</exception>
    public static IServiceCollection AddServiceBusEventTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = ServiceBusTransportSettings.FromConfiguration(configuration);

        services.AddSingleton(_ => settings.CreateClient());
        services.TryAddSingleton<EventBackboneMetrics>();
        services.AddSingleton<IEventTransportPublisher, ServiceBusEventPublisher>();
        services.AddSingleton<IDeadLetterQueue, ServiceBusDeadLetterQueue>();
        services.AddSingleton(provider => new ServiceBusEventConsumerHost(
            provider.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>(),
            provider.GetRequiredService<IEventTransportConsumer>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<EventTransportOptions>(),
            provider.GetRequiredService<ILogger<ServiceBusEventConsumerHost>>()));
        return services;
    }
}
