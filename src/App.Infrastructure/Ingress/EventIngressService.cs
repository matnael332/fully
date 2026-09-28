using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Ingress;

/// <summary>
/// The durable in-process ingress (architecture §3: ingress, source verification, tenant resolution,
/// durable receipt, acknowledge).
/// </summary>
/// <remarks>
/// <para>
/// Order matters. The delivery's shape is checked first, then its connection is identified through the
/// single approved tenant-less function, then the source is matched and verified. Only then is the
/// tenant set, server-side and from the connection alone, and only then is anything written: the raw
/// receipt and its outbox marker in one transaction. A delivery refused before that point leaves no
/// payload anywhere (Gate 2 owner decision 6).
/// </para>
/// <para>
/// Idempotency: a repeated source event id on the same connection hits the receipt's unique key, the
/// write rolls back as a whole, and the delivery is acknowledged as a duplicate of the stored receipt;
/// no second receipt or marker exists. Crash safety: nothing is acknowledged before the commit, so a
/// crash before it leaves nothing and the source retries, and a crash after it is absorbed by that
/// duplicate path.
/// </para>
/// <para>
/// Hygiene: logs carry ids, codes and reasons only, never the payload or the proof.
/// </para>
/// </remarks>
internal sealed class EventIngressService(
    ITenantScopedDatabase database,
    ITenantContextAccessor tenantContextAccessor,
    IngressConnectionResolver connectionResolver,
    IEnumerable<ISourceVerifier> verifiers,
    IClock clock,
    ILogger<EventIngressService> logger) : IEventIngress
{
    private static readonly JsonDocumentOptions PayloadParsing = new()
    {
        // jsonb keeps only the last of repeated keys, so a document that repeats a key would be stored
        // with content silently dropped. Such a document is ambiguous, so it is refused instead.
        AllowDuplicateProperties = false,
    };

    private readonly ISourceVerifier[] _verifiers = verifiers.ToArray();

    private enum WriteResult
    {
        Written,
        TenantInactive,
    }

    /// <inheritdoc />
    public async Task<IngressOutcome> SubmitAsync(
        IngressSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (tenantContextAccessor.Current.IsResolved)
        {
            // The tenant is resolved from the connection. Running inside a scope that already carries a
            // tenant would let a caller's tenant stand in for the connection's.
            throw new InvalidOperationException(
                "Ingress must run in a scope without a resolved tenant; the tenant comes from the connection.");
        }

        var correlationId = submission.CorrelationId.IsEmpty ? CorrelationId.New() : submission.CorrelationId;

        var shapeError = submission.Validate()
            ?? (IsStorableJson(submission.Payload) ? null : IngressErrors.PayloadNotStorable);
        if (shapeError is not null)
        {
            return Reject(shapeError, submission.ConnectionId, correlationId);
        }

        ResolvedIngressConnection? connection;
        try
        {
            connection = await connectionResolver.ResolveAsync(submission.ConnectionId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Ingress could not resolve connection {ConnectionId}; the delivery was not stored (correlation {CorrelationId}).",
                submission.ConnectionId,
                correlationId);
            return IngressOutcome.Failed(correlationId);
        }

        if (connection is null)
        {
            return Reject(IngressErrors.UnknownConnection, submission.ConnectionId, correlationId);
        }

        if (!string.Equals(connection.SourceSystem, submission.SourceSystem, StringComparison.Ordinal))
        {
            return Reject(IngressErrors.SourceMismatch, submission.ConnectionId, correlationId);
        }

        if (!await IsAuthenticAsync(submission, correlationId, cancellationToken))
        {
            return Reject(IngressErrors.SourceNotVerified, submission.ConnectionId, correlationId);
        }

        tenantContextAccessor.SetResolved(
            TenantContext.Resolved(connection.TenantId, TenantResolutionSource.TrustedServiceIdentity));

        var receiptId = Guid.CreateVersion7();
        var receivedAt = clock.UtcNow;
        var connectionReference = ConnectionRecord.ReferenceFor(submission.ConnectionId);
        var fingerprint = Fingerprint(submission.Payload);

        var write = await database.ExecuteAsync(
            async (dbContext, token) =>
            {
                var tenantStatus = await dbContext.Tenants
                    .AsNoTracking()
                    .Where(tenant => tenant.TenantId == connection.TenantId)
                    .Select(tenant => (TenantStatus?)tenant.Status)
                    .SingleOrDefaultAsync(token);
                if (tenantStatus != TenantStatus.Active)
                {
                    return WriteResult.TenantInactive;
                }

                dbContext.RawReceipts.Add(RawReceiptRecord.Receive(
                    receiptId,
                    connection.TenantId,
                    connectionReference,
                    submission.SourceSystem,
                    submission.SourceEventId,
                    submission.SchemaVersion,
                    receivedAt,
                    submission.Payload,
                    fingerprint));
                dbContext.OutboxMessages.Add(OutboxMessageRecord.Queue(
                    Guid.CreateVersion7(),
                    connection.TenantId,
                    IngressMessageTypes.ReceiptReceived,
                    MarkerPayload(receiptId),
                    receivedAt,
                    correlationId));
                return WriteResult.Written;
            },
            cancellationToken);

        if (write.IsSuccess)
        {
            if (write.Value == WriteResult.TenantInactive)
            {
                return Reject(IngressErrors.TenantInactive, submission.ConnectionId, correlationId);
            }

            logger.LogInformation(
                "Ingress stored receipt {ReceiptId} for tenant {TenantId} on connection {ConnectionId} (correlation {CorrelationId}).",
                receiptId,
                connection.TenantId,
                submission.ConnectionId,
                correlationId);
            return IngressOutcome.Accepted(receiptId, correlationId);
        }

        if (write.Error == PersistenceErrors.Duplicate && submission.SourceEventId is not null)
        {
            return await AcknowledgeDuplicateAsync(
                connectionReference,
                submission.SourceEventId,
                fingerprint,
                submission.ConnectionId,
                correlationId,
                cancellationToken);
        }

        if (write.Error == PersistenceErrors.InvalidData)
        {
            return Reject(IngressErrors.PayloadNotStorable, submission.ConnectionId, correlationId);
        }

        return Fail(write.Error, submission.ConnectionId, correlationId);
    }

    private async Task<IngressOutcome> AcknowledgeDuplicateAsync(
        string connectionReference,
        string sourceEventId,
        string fingerprint,
        Guid connectionId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var existing = await database.ExecuteAsync(
            (dbContext, token) => dbContext.RawReceipts
                .AsNoTracking()
                .Where(receipt => receipt.ConnectionReference == connectionReference
                    && receipt.SourceEventId == sourceEventId)
                .Select(receipt => new StoredReceipt(receipt.ReceiptId, receipt.PayloadFingerprint))
                .SingleOrDefaultAsync(token),
            cancellationToken);

        if (existing.IsFailure)
        {
            return Fail(existing.Error, connectionId, correlationId);
        }

        if (existing.Value is not { } stored)
        {
            // The conflicting receipt is gone again, so nothing is stored: the source must retry rather
            // than be told the delivery is safe.
            return Fail(PersistenceErrors.Unavailable, connectionId, correlationId);
        }

        if (!string.Equals(stored.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
        {
            // The provider event id is the authoritative key, so the first stored delivery stands. The
            // differing content is not stored, and this warning keeps that from being silent.
            logger.LogWarning(
                "Ingress received a repeated source event on connection {ConnectionId} with a different payload fingerprint; receipt {ReceiptId} stands and the repeated content was not stored (correlation {CorrelationId}).",
                connectionId,
                stored.ReceiptId,
                correlationId);
        }

        logger.LogInformation(
            "Ingress acknowledged a repeated delivery of receipt {ReceiptId} on connection {ConnectionId} (correlation {CorrelationId}).",
            stored.ReceiptId,
            connectionId,
            correlationId);
        return IngressOutcome.Duplicate(stored.ReceiptId, correlationId);
    }

    private async Task<bool> IsAuthenticAsync(
        IngressSubmission submission,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var candidates = Array.FindAll(
            _verifiers,
            verifier => string.Equals(verifier.SourceSystem, submission.SourceSystem, StringComparison.Ordinal));

        // No verifier, or an ambiguous pair of them, never verifies anything: fail closed.
        if (candidates.Length != 1)
        {
            return false;
        }

        try
        {
            return await candidates[0].VerifyAsync(submission, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the exception type is logged: a verifier's message could quote the proof.
            logger.LogWarning(
                "The source verifier failed with {ExceptionType}; the delivery on connection {ConnectionId} is treated as not verified (correlation {CorrelationId}).",
                exception.GetType().Name,
                submission.ConnectionId,
                correlationId);
            return false;
        }
    }

    private IngressOutcome Reject(Error error, Guid connectionId, CorrelationId correlationId)
    {
        var outcome = IngressOutcome.Rejected(error, correlationId);
        logger.LogWarning(
            "Ingress rejected a delivery on connection {ConnectionId}: {ErrorCode} ({RejectionReason}); nothing was stored (correlation {CorrelationId}).",
            connectionId,
            error.Code,
            outcome.RejectionReason,
            correlationId);
        return outcome;
    }

    private IngressOutcome Fail(Error cause, Guid connectionId, CorrelationId correlationId)
    {
        logger.LogError(
            "Ingress could not store the delivery on connection {ConnectionId}: {ErrorCode}; it was not acknowledged (correlation {CorrelationId}).",
            connectionId,
            cause.Code,
            correlationId);
        return IngressOutcome.Failed(correlationId);
    }

    private static string Fingerprint(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    // The marker names the receipt and nothing else, so it carries no content that must expire.
    private static string MarkerPayload(Guid receiptId) => $$"""{"receiptId":"{{receiptId:D}}"}""";

    private static bool IsStorableJson(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, PayloadParsing);
            return IsStorable(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not JSON, not well-formed UTF-16, or an escape that decodes to malformed text.
            return false;
        }
    }

    private static bool IsStorable(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .All(property => IsStorableText(property.Name) && IsStorable(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(IsStorable),
        JsonValueKind.String => IsStorableText(element.GetString()),
        _ => true,
    };

    // PostgreSQL jsonb cannot hold U+0000, and text must be well-formed UTF-16 to reach it as UTF-8.
    private static bool IsStorableText(string? text)
    {
        if (text is null)
        {
            return true;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record StoredReceipt(Guid ReceiptId, string PayloadFingerprint);
}
