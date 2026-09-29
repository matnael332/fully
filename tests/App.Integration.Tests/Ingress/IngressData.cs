using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Ingress;

/// <summary>A durable receipt as the database holds it.</summary>
internal sealed record StoredReceipt(
    Guid ReceiptId,
    Guid TenantId,
    string ConnectionReference,
    string SourceSystem,
    string? SourceEventId,
    string SchemaVersion,
    DateTimeOffset ReceivedAtUtc,
    string? Payload,
    string? PayloadFingerprint,
    string ProcessingState);

/// <summary>An outbox marker as the database holds it.</summary>
internal sealed record StoredMarker(
    Guid OutboxMessageId,
    Guid TenantId,
    string MessageType,
    string Payload,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    DateTimeOffset? PublishedAtUtc);

/// <summary>
/// Seeds tenants and connections and reads back what ingress stored. Connections are registered through
/// the runtime role under the tenant's own scope, the way the product will register them; assertions read
/// with the schema owner so that "nothing was stored anywhere" is checked across every tenant.
/// </summary>
internal static class IngressData
{
    public static string NewMarker() => "m" + Guid.NewGuid().ToString("N");

    public static string PayloadWith(string marker) =>
        $$$"""{"note":"{{{marker}}}","item":{"id":"1001","status":"working"}}""";

    public static async Task SeedTenantAsync(
        DatabaseFixture fixture,
        TenantId tenantId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tenants (tenant_id, status, created_at_utc)
            VALUES (@tenant_id, @status, now());
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId.Value);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<Guid> RegisterConnectionAsync(
        DatabaseFixture fixture,
        TenantId tenantId,
        ConnectionStatus status,
        string sourceSystem,
        CancellationToken cancellationToken)
    {
        var connectionId = Guid.CreateVersion7();
        var (database, context) = fixture.CreateTenantScoped(tenantId);
        await using (context)
        {
            var written = await database.ExecuteAsync(
                (dbContext, _) =>
                {
                    dbContext.Connections.Add(ConnectionRecord.Register(
                        connectionId, tenantId, sourceSystem, status, IngressHost.Now));
                    return Task.CompletedTask;
                },
                cancellationToken);

            Assert.True(written.IsSuccess, written.IsSuccess ? string.Empty : written.Error.Code);
        }

        return connectionId;
    }

    public static async Task<(TenantId Tenant, Guid Connection)> NewConnectionAsync(
        DatabaseFixture fixture,
        CancellationToken cancellationToken,
        string tenantStatus = "Active",
        ConnectionStatus connectionStatus = ConnectionStatus.Active,
        string sourceSystem = SyntheticSource.System)
    {
        var tenant = TenantId.New();
        await SeedTenantAsync(fixture, tenant, tenantStatus, cancellationToken);
        var connection = await RegisterConnectionAsync(fixture, tenant, connectionStatus, sourceSystem, cancellationToken);
        return (tenant, connection);
    }

    public static async Task<IReadOnlyList<StoredReceipt>> ReceiptsAsync(
        DatabaseFixture fixture,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_id, tenant_id, connection_reference, source_system, source_event_id,
                   schema_version, received_at_utc, payload::text, payload_fingerprint, processing_state
            FROM raw_receipts
            WHERE tenant_id = @tenant_id
            ORDER BY receipt_id;
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId.Value);

        var receipts = new List<StoredReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new StoredReceipt(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9)));
        }

        return receipts;
    }

    /// <summary>The database's own clock, which stamps every receipt time.</summary>
    public static async Task<DateTimeOffset> DatabaseNowAsync(DatabaseFixture fixture, CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT now();", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>The receipt time the database stamped when it stored the receipt.</summary>
    public static async Task<DateTimeOffset> ReceiptTimeAsync(DatabaseFixture fixture, Guid receiptId, CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT received_at_utc FROM raw_receipts WHERE receipt_id = @receipt;", connection);
        command.Parameters.AddWithValue("receipt", receiptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    public static async Task<IReadOnlyList<StoredMarker>> MarkersAsync(
        DatabaseFixture fixture,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT outbox_message_id, tenant_id, message_type, payload::text, occurred_at_utc,
                   correlation_id, published_at_utc
            FROM outbox_messages
            WHERE tenant_id = @tenant_id
            ORDER BY outbox_message_id;
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId.Value);

        var markers = new List<StoredMarker>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            markers.Add(new StoredMarker(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return markers;
    }

    /// <summary>Counts stored rows, in any tenant, whose payload contains the marker.</summary>
    public static async Task<long> CountStoredContentAsync(
        DatabaseFixture fixture,
        string marker,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM raw_receipts WHERE strpos(payload::text, @marker) > 0)
                 + (SELECT count(*) FROM outbox_messages WHERE strpos(payload::text, @marker) > 0);
            """;
        command.Parameters.AddWithValue("marker", marker);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task ExecuteAdministrativeAsync(
        DatabaseFixture fixture,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
