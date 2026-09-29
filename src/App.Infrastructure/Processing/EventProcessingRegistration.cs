using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;

namespace WorkContinuity.Infrastructure.Processing;

/// <summary>Settings for the processing pipeline.</summary>
public sealed class EventProcessingOptions
{
    /// <summary>
    /// How far a source time may lie after the receipt, or an occurrence after the object's update, before the
    /// order counts as impossible: it absorbs clock skew between the source and the product.
    /// </summary>
    public TimeSpan ClockSkewTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Checks every value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tolerance is not positive or exceeds a day.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ClockSkewTolerance, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ClockSkewTolerance, TimeSpan.FromDays(1));
    }
}

/// <summary>Hands validated transport messages to the processing pipeline and settles them by its outcome.</summary>
/// <remarks>
/// Every durable outcome, a quarantine and a duplicate included, is a handled message and completes it. A
/// receipt that is not visible in the message's tenant, or a tenant mismatch, cannot be processed as delivered
/// and is dead-lettered. A failure to commit is transient and retried.
/// </remarks>
internal sealed class EventProcessingHandler(IEventProcessor processor) : ITransportMessageHandler
{
    public async Task<TransportHandlingResult> HandleAsync(TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        var processed = await processor.ProcessAsync(envelope, cancellationToken);
        if (processed.IsSuccess)
        {
            return TransportHandlingResult.Succeeded;
        }

        return processed.Error == ProcessingErrors.Unavailable
            ? TransportHandlingResult.RetryLater(processed.Error)
            : TransportHandlingResult.Rejected(processed.Error);
    }
}

/// <summary>Composes the processing pipeline.</summary>
/// <remarks>
/// Registers the pipeline as the transport's downstream handler. Requires <c>AddPersistence</c>, the
/// flow-scoped tenant accessor, an <c>IClock</c> and <c>AddEventTransport</c>. Source normalizers are registered
/// by each source's own module; Gate 2 registers none in production. Nothing registers this in a host yet.
/// </remarks>
public static class EventProcessingRegistration
{
    /// <summary>Registers the processing pipeline as the transport's downstream handler.</summary>
    public static IServiceCollection AddEventProcessing(
        this IServiceCollection services,
        Action<EventProcessingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new EventProcessingOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton<EventBackboneMetrics>();
        services.AddScoped<IEventProcessor, EventProcessor>();
        services.AddScoped<ITransportMessageHandler, EventProcessingHandler>();
        return services;
    }
}
