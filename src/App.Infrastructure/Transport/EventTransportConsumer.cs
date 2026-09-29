using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// The transport-level consumer: the only code that turns a received broker message into work.
/// </summary>
/// <remarks>
/// <para>
/// Order matters. The message is validated against the envelope first, and nothing in it is trusted before
/// that: an invalid or unsupported message is dead-lettered with its stable reason and never reaches a
/// handler. For a valid message the tenant is restored from the validated envelope alone, as a
/// trusted-service context, in a fresh dependency scope and flow per message, and only then is the
/// downstream <see cref="ITransportMessageHandler"/> resolved and invoked.
/// </para>
/// <para>
/// Nothing is settled before the handler reports. Success completes the message; a transient failure, or a
/// handler that throws, is retried with bounded backoff up to the policy's attempt limit and then
/// dead-lettered; a permanent failure is dead-lettered at once. The adapter applies the settlement; if the
/// process dies first, the broker redelivers the message, and every delivery counts toward the limit.
/// </para>
/// <para>
/// Hygiene: logs carry ids, codes, counts and exception type names only, never the body or an exception
/// message. An invalid message is identified by its broker sequence number, never by the text it carries.
/// Metrics count every settlement by kind and catalogued reason code, and record each message's delivery
/// attempt, which is where retries and head-of-line blocking show.
/// </para>
/// </remarks>
internal sealed class EventTransportConsumer(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantContextAccessor,
    EventTransportOptions options,
    EventBackboneMetrics metrics,
    ILogger<EventTransportConsumer> logger) : IEventTransportConsumer
{
    /// <inheritdoc />
    public async Task<TransportSettlement> ConsumeAsync(
        ReceivedTransportMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (tenantContextAccessor.Current.IsResolved)
        {
            // The tenant comes from the validated message. Running inside a scope that already carries one
            // would let that tenant stand in for the message's.
            throw new InvalidOperationException(
                "The transport consumer must run in a scope without a resolved tenant; the tenant comes from the validated message.");
        }

        var read = TransportEnvelope.Read(message);
        if (read.IsFailure)
        {
            logger.LogError(
                "Transport dead-lettered message {SequenceNumber} on delivery {DeliveryCount}: {ReasonCode}; it failed envelope validation and reached no handler.",
                message.SequenceNumber,
                message.DeliveryCount,
                read.Error.Code);
            var invalid = TransportSettlement.DeadLetter(read.Error);
            metrics.Settled(invalid, result: null, message.DeliveryCount);
            return invalid;
        }

        return await HandleAsync(read.Value, message.DeliveryCount, cancellationToken);
    }

    private async Task<TransportSettlement> HandleAsync(
        TransportEnvelope envelope,
        int deliveryCount,
        CancellationToken cancellationToken)
    {
        // A fresh dependency scope, and this method's own asynchronous flow, per message: the restored tenant
        // is visible to the handler and never to the adapter or the next message.
        await using var scope = scopeFactory.CreateAsyncScope();
        tenantContextAccessor.SetResolved(
            TenantContext.Resolved(envelope.TenantId, TenantResolutionSource.TrustedServiceIdentity));

        TransportHandlingResult? result;
        try
        {
            var handler = scope.ServiceProvider.GetRequiredService<ITransportMessageHandler>();
            result = await handler.HandleAsync(envelope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the message is left unsettled and the broker redelivers it.
            throw;
        }
        catch (Exception exception)
        {
            // Only the type is logged: a handler's exception message could quote what it was working on.
            logger.LogWarning(
                "The downstream handler failed with {ExceptionType} on message {MessageId} for tenant {TenantId} (delivery {DeliveryCount}, correlation {CorrelationId}); treated as transient.",
                exception.GetType().Name,
                envelope.MessageId,
                envelope.TenantId,
                deliveryCount,
                envelope.CorrelationId);
            result = TransportHandlingResult.RetryLater(TransportErrors.HandlerFailed);
        }

        if (result is null || result.Status == TransportHandlingStatus.None)
        {
            result = TransportHandlingResult.RetryLater(TransportErrors.HandlerFailed);
        }

        var settlement = options.RetryPolicy.Settle(result, deliveryCount, Random.Shared.NextDouble());
        Log(settlement, result, envelope, deliveryCount);
        metrics.Settled(settlement, result, deliveryCount);
        return settlement;
    }

    private void Log(
        TransportSettlement settlement,
        TransportHandlingResult result,
        TransportEnvelope envelope,
        int deliveryCount)
    {
        switch (settlement.Kind)
        {
            case TransportSettlementKind.Complete:
                logger.LogInformation(
                    "Transport completed message {MessageId} for tenant {TenantId} (delivery {DeliveryCount}, correlation {CorrelationId}).",
                    envelope.MessageId,
                    envelope.TenantId,
                    deliveryCount,
                    envelope.CorrelationId);
                break;

            case TransportSettlementKind.Abandon:
                logger.LogWarning(
                    "Transport will redeliver message {MessageId} for tenant {TenantId} after {RetryAfterMilliseconds} ms: {ReasonCode} (delivery {DeliveryCount} of {MaxAttempts}, correlation {CorrelationId}).",
                    envelope.MessageId,
                    envelope.TenantId,
                    (long)settlement.RetryAfter.TotalMilliseconds,
                    SafeCode(result),
                    deliveryCount,
                    options.RetryPolicy.MaxAttempts,
                    envelope.CorrelationId);
                break;

            case TransportSettlementKind.DeadLetter:
                logger.LogError(
                    "Transport dead-lettered message {MessageId} for tenant {TenantId}: {ReasonCode} (delivery {DeliveryCount}, correlation {CorrelationId}).",
                    envelope.MessageId,
                    envelope.TenantId,
                    settlement.DeadLetterReason!.Code,
                    deliveryCount,
                    envelope.CorrelationId);
                break;
        }
    }

    private static string SafeCode(TransportHandlingResult result) =>
        result.Reason is { } reason && TransportContract.IsSafeReason(reason.Code)
            ? reason.Code
            : TransportContract.UnrecognizedReason;
}
