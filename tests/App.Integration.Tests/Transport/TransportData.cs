using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>An outbox row as the database holds it.</summary>
internal sealed record OutboxRow(Guid OutboxMessageId, Guid TenantId, string MessageType, DateTimeOffset? PublishedAtUtc);

/// <summary>
/// Reads and seeds outbox rows with the schema owner, so assertions see every tenant. Rows a test seeds
/// directly are removed again by exact id.
/// </summary>
internal static class TransportData
{
    /// <summary>The outbox message id of the marker ingress wrote for a receipt.</summary>
    public static async Task<Guid> OutboxIdForReceiptAsync(DatabaseFixture fixture, Guid receiptId, CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT outbox_message_id FROM outbox_messages WHERE payload ->> 'receiptId' = @receipt_id;",
            connection);
        command.Parameters.AddWithValue("receipt_id", receiptId.ToString("D"));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Whether every named outbox message is marked published.</summary>
    public static async Task<bool> AllPublishedAsync(
        DatabaseFixture fixture,
        IReadOnlyCollection<Guid> outboxMessageIds,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(fixture, outboxMessageIds, cancellationToken);
        return rows.Count == outboxMessageIds.Count && rows.All(row => row.PublishedAtUtc is not null);
    }

    /// <summary>The named outbox rows.</summary>
    public static async Task<IReadOnlyList<OutboxRow>> RowsAsync(
        DatabaseFixture fixture,
        IReadOnlyCollection<Guid> outboxMessageIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT outbox_message_id, tenant_id, message_type, published_at_utc
            FROM outbox_messages
            WHERE outbox_message_id = ANY (@ids)
            ORDER BY occurred_at_utc, outbox_message_id;
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = outboxMessageIds.ToArray() });

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    /// <summary>Seeds one outbox row directly, bypassing ingress, as only a defect or a forger could write it.</summary>
    public static async Task<Guid> SeedOutboxRowAsync(
        DatabaseFixture fixture,
        TenantId tenantId,
        string messageType,
        string payload,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var outboxMessageId = Guid.CreateVersion7();
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages (outbox_message_id, tenant_id, message_type, payload, occurred_at_utc, correlation_id)
            VALUES (@id, @tenant_id, @message_type, @payload::jsonb, @occurred_at, @correlation_id);
            """,
            connection);
        command.Parameters.AddWithValue("id", outboxMessageId);
        command.Parameters.AddWithValue("tenant_id", tenantId.Value);
        command.Parameters.AddWithValue("message_type", messageType);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("occurred_at", occurredAtUtc);
        command.Parameters.AddWithValue("correlation_id", CorrelationId.New().Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return outboxMessageId;
    }

    /// <summary>Removes rows this test seeded, by exact id.</summary>
    public static async Task DeleteOutboxRowsAsync(
        DatabaseFixture fixture,
        IReadOnlyCollection<Guid> outboxMessageIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM outbox_messages WHERE outbox_message_id = ANY (@ids);", connection);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = outboxMessageIds.ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A content-free receipt marker, as ingress writes it.</summary>
    public static string Marker(Guid receiptId) => $$"""{"receiptId":"{{receiptId:D}}"}""";
}
