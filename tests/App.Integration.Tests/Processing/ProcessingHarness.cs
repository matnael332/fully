using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Processing;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>A delivery that ingress stored, and the envelope the relay publishes for it.</summary>
internal sealed record Delivered(TenantId Tenant, Guid ReceiptId, Guid OutboxId, CorrelationId Correlation, TransportEnvelope Envelope);

/// <summary>A ledger entry as the database holds it.</summary>
internal sealed record LedgerRow(
    Guid EventId,
    Guid ReceiptId,
    string SourceKind,
    string ConnectionReference,
    string? SourceEventId,
    string? ObjectId,
    long? SourceRevision,
    string CorrelationId,
    string Outcome,
    string? QuarantineReason,
    string? ReasonCode,
    Guid? DuplicateCandidateOf);

/// <summary>A field's ordering state as the database holds it.</summary>
internal sealed record FieldStateRow(string ObjectId, string Field, Guid LastEventId, long? SourceRevision, DateTimeOffset? SourceUpdatedAtUtc);

/// <summary>
/// Composes the real ingress, transport and processing pipeline with the synthetic normalizer, submits
/// deliveries through ingress, runs the processor in a tenant scope exactly as the transport consumer does,
/// and reads back what was stored with the schema owner.
/// </summary>
internal static class ProcessingHarness
{
    public static TransportHost Host(
        DatabaseFixture fixture,
        InMemoryTransport transport,
        Action<IServiceCollection>? normalizers = null) =>
        TransportHost.InMemory(fixture, transport, handlers: services =>
        {
            services.AddEventProcessing();
            if (normalizers is null)
            {
                services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            }
            else
            {
                normalizers(services);
            }
        });

    public static async Task<Delivered> SubmitAsync(
        this TransportHost host,
        DatabaseFixture fixture,
        TenantId tenant,
        Guid connection,
        SyntheticEvent synthetic,
        string? sourceEventId) =>
        await host.SubmitRawAsync(fixture, tenant, connection, synthetic.ToPayload(), sourceEventId);

    public static async Task<Delivered> SubmitRawAsync(
        this TransportHost host,
        DatabaseFixture fixture,
        TenantId tenant,
        Guid connection,
        string payload,
        string? sourceEventId,
        string schemaVersion = SyntheticSource.SchemaVersion)
    {
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, sourceEventId, payload, schemaVersion: schemaVersion));
        Assert.Equal(IngressStatus.Accepted, outcome.Status);
        var receiptId = outcome.ReceiptId!.Value;
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptId, TestContext.Current.CancellationToken);
        var envelope = TransportEnvelope.FromOutbox(
            outboxId, tenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptId), outcome.CorrelationId);
        Assert.True(envelope.IsSuccess);
        return new Delivered(tenant, receiptId, outboxId, outcome.CorrelationId, envelope.Value);
    }

    /// <summary>Runs the processor in the envelope's tenant scope, in its own flow, as the transport consumer does.</summary>
    public static Task<Result<EventProcessingResult>> ProcessAsync(
        this TransportHost host,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                host.TenantContextAccessor.SetResolved(
                    TenantContext.Resolved(envelope.TenantId, TenantResolutionSource.TrustedServiceIdentity));
                return await scope.ServiceProvider.GetRequiredService<IEventProcessor>().ProcessAsync(envelope, cancellationToken);
            },
            CancellationToken.None);

    /// <summary>Processes and requires a settled outcome.</summary>
    public static async Task<EventProcessingResult> ProcessSettledAsync(this TransportHost host, TransportEnvelope envelope)
    {
        var processed = await host.ProcessAsync(envelope, TestContext.Current.CancellationToken);
        Assert.True(processed.IsSuccess, processed.IsSuccess ? string.Empty : processed.Error.Code);
        return processed.Value;
    }

    /// <summary>The message as a transport would hand it to the consumer.</summary>
    public static ReceivedTransportMessage Received(TransportEnvelope envelope, int deliveryCount = 1)
    {
        var wire = envelope.ToWire();
        return new ReceivedTransportMessage(
            wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body, deliveryCount, SequenceNumber: 1);
    }

    public static async Task<IReadOnlyList<LedgerRow>> LedgerAsync(DatabaseFixture fixture, TenantId tenant)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, receipt_id, source_kind, connection_reference, source_event_id, object_id, source_revision,
                   correlation_id, outcome, quarantine_reason, reason_code, duplicate_candidate_of
            FROM event_ledger WHERE tenant_id = @tenant ORDER BY processed_at_utc, event_id;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);

        var rows = new List<LedgerRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new LedgerRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11)));
        }

        return rows;
    }

    public static async Task<IReadOnlyList<(string Field, string DataType, string Decision)>> LedgerFieldsAsync(DatabaseFixture fixture, Guid eventId)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT field, data_type, decision FROM event_ledger_fields WHERE event_id = @event ORDER BY field;", connection);
        command.Parameters.AddWithValue("event", eventId);

        var rows = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    public static async Task<IReadOnlyList<FieldStateRow>> FieldStatesAsync(DatabaseFixture fixture, TenantId tenant)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT object_id, field, last_event_id, source_revision, source_updated_at_utc
            FROM field_ordering_states WHERE tenant_id = @tenant ORDER BY object_id, field;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);

        var rows = new List<FieldStateRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new FieldStateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    public static async Task<string> ReceiptStateAsync(DatabaseFixture fixture, Guid receiptId)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT processing_state FROM raw_receipts WHERE receipt_id = @receipt;", connection);
        command.Parameters.AddWithValue("receipt", receiptId);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
