using Npgsql;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>
/// The Gate 2E Snapshot tables, pinned against the real database. They have exactly their columns, with a
/// bounded value, and the runtime role holds only what projection needs. No Snapshot row can be deleted, even
/// with privileges re-granted. The database itself refuses a source kind, data type or authority that does not
/// exist, and an empty or oversized value. No foreign key can point across tenants. Every refusal is paired with
/// a positive control, so each proves its own constraint.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SnapshotDatabaseSecurityTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_snapshot_tables_have_exactly_their_columns_and_the_value_is_bounded()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["snapshot_objects"] =
            [
                "last_event_id", "object_connection", "object_id", "object_system", "object_type", "processed_at_utc", "tenant_id", "version",
            ],
            ["snapshot_fields"] =
            [
                "authority", "connection_reference", "data_type", "field", "field_version", "last_event_id", "object_connection", "object_id",
                "object_system", "object_type", "object_version", "observed_at_utc", "occurred_at_utc", "processed_at_utc", "source_event_id",
                "source_kind", "source_revision", "source_system", "source_updated_at_utc", "tenant_id", "value",
            ],
        };

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        foreach (var (table, columns) in expected)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT column_name, data_type, character_maximum_length
                FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table ORDER BY column_name;
                """,
                connection);
            command.Parameters.AddWithValue("table", table);

            var actual = new List<(string Name, string Type, int? Length)>();
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                actual.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }

            Assert.Equal(columns.Order(StringComparer.Ordinal), actual.Select(column => column.Name));
            Assert.DoesNotContain(actual, column =>
                column.Name.Contains("before", StringComparison.Ordinal)
                || column.Name.Contains("actor", StringComparison.Ordinal)
                || column.Name.Contains("payload", StringComparison.Ordinal)
                || column.Name.Contains("reference_url", StringComparison.Ordinal)
                || column.Name.Contains("fingerprint", StringComparison.Ordinal)
                || column.Name.Contains("body", StringComparison.Ordinal));

            if (table == "snapshot_fields")
            {
                var value = actual.Single(column => column.Name == "value");
                Assert.Equal("character varying", value.Type);
                Assert.Equal(EventText.MaxValueLength, value.Length);
            }
        }
    }

    [Fact]
    public async Task The_runtime_role_reads_inserts_and_updates_the_snapshot_and_never_truncates()
    {
        // DELETE is revoked by the migration but re-granted by any later bootstrap run; the restrictive policies,
        // tested below, are what makes that guarantee durable. TRUNCATE bypasses row-level security and no path
        // ever grants it.
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        foreach (var table in new[] { "snapshot_objects", "snapshot_fields" })
        {
            foreach (var (privilege, granted) in new[] { ("SELECT", true), ("INSERT", true), ("UPDATE", true), ("TRUNCATE", false) })
            {
                await using var command = new NpgsqlCommand("SELECT has_table_privilege(@role, @table, @privilege);", connection);
                command.Parameters.AddWithValue("role", PersistenceOptions.RuntimeRoleName);
                command.Parameters.AddWithValue("table", "public." + table);
                command.Parameters.AddWithValue("privilege", privilege);
                Assert.True(granted == (bool)(await command.ExecuteScalarAsync(Token))!, $"{privilege} on {table}");
            }
        }
    }

    [Fact]
    public async Task No_snapshot_row_can_be_deleted_even_with_privileges_re_granted()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-no-delete")).Envelope);

        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var transaction = await admin.BeginTransactionAsync(Token);
        try
        {
            // Rehearse a later bootstrap that re-grants broad privileges; the restrictive policies still hold.
            await AffectedAsync(admin, transaction, $"GRANT DELETE ON snapshot_objects, snapshot_fields TO {PersistenceOptions.RuntimeRoleName};");
            await AffectedAsync(admin, transaction, $"SET LOCAL ROLE {PersistenceOptions.RuntimeRoleName};");
            await AffectedAsync(admin, transaction, $"SELECT set_config('app.tenant_id', '{tenant.Value}', true);");

            Assert.Equal(0, await AffectedAsync(admin, transaction, "DELETE FROM snapshot_fields;"));
            Assert.Equal(0, await AffectedAsync(admin, transaction, "DELETE FROM snapshot_objects;"));

            // The same role, in the same tenant, may still update: the Snapshot is current state, not a log.
            Assert.Equal(1, await AffectedAsync(admin, transaction, "UPDATE snapshot_fields SET field_version = field_version;"));
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        Assert.Single(await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant));
        Assert.Single(await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));
    }

    [Fact]
    public async Task The_database_itself_refuses_facts_no_source_or_authority_could_state()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var accepted = await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-refusals")).Envelope);
        var probe = new FieldProbe(tenant.Value, ConnectionRecord.ReferenceFor(connection), "1001", accepted.EventId.Value);

        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);

        // The probe itself is a valid row, so each refusal below is its own constraint's.
        Assert.Equal(Inserted, await InsertFieldAsync(admin, probe));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await InsertFieldAsync(admin, probe with { SourceKind = "AiModel" }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await InsertFieldAsync(admin, probe with { SourceKind = "Inference" }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await InsertFieldAsync(admin, probe with { DataType = "Guess" }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await InsertFieldAsync(admin, probe with { Authority = "Model" }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await InsertFieldAsync(admin, probe with { Value = string.Empty }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await InsertFieldAsync(admin, probe with { FieldVersion = 0 }));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, await InsertFieldAsync(admin, probe with { Value = new string('v', EventText.MaxValueLength + 1) }));
        Assert.Equal(Inserted, await InsertFieldAsync(admin, probe with { Value = null }));
    }

    [Fact]
    public async Task Snapshot_provenance_can_never_point_at_another_tenant()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        var eventA = (await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 1 }, "evt-cross-a")).Envelope)).EventId.Value;
        var eventB = (await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantB, connectionB, new SyntheticEvent { Revision = 1 }, "evt-cross-b")).Envelope)).EventId.Value;
        var referenceA = ConnectionRecord.ReferenceFor(connectionA);
        var referenceB = ConnectionRecord.ReferenceFor(connectionB);

        // Even the schema owner, which bypasses row-level security here, is stopped by the tenant-consistent keys.
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        var fieldOfA = new FieldProbe(tenantA.Value, referenceA, "1001", eventA);

        Assert.Equal(Inserted, await InsertFieldAsync(admin, fieldOfA));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await InsertFieldAsync(admin, fieldOfA with { EventId = eventB }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await InsertFieldAsync(admin, fieldOfA with { Tenant = tenantB.Value, EventId = eventB }));

        Assert.Equal(Inserted, await InsertObjectAsync(admin, tenantB.Value, referenceB, "9999", eventB));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await InsertObjectAsync(admin, tenantB.Value, referenceB, "9999", eventA));
    }

    private const string Inserted = "inserted";

    private async Task<int> AffectedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(Token);
    }

    // Each probe runs in its own transaction that is always rolled back: it reports the outcome and leaves nothing.
    private async Task<string> InsertFieldAsync(NpgsqlConnection admin, FieldProbe probe)
    {
        await using var transaction = await admin.BeginTransactionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO snapshot_fields (tenant_id, object_system, object_connection, object_type, object_id, field, data_type, value,
                authority, source_kind, source_system, connection_reference, last_event_id, source_event_id, source_revision,
                source_updated_at_utc, occurred_at_utc, observed_at_utc, processed_at_utc, field_version, object_version)
            VALUES (@tenant, 'synthetic', @connection, 'task', @object, 'probe', @dataType, @value, @authority, @sourceKind,
                'synthetic', @connection, @event, NULL, 1, NULL, NULL, now(), now(), @fieldVersion, 1);
            """,
            admin,
            transaction);
        command.Parameters.AddWithValue("tenant", probe.Tenant);
        command.Parameters.AddWithValue("connection", probe.Connection);
        command.Parameters.AddWithValue("object", probe.ObjectId);
        command.Parameters.AddWithValue("dataType", probe.DataType);
        command.Parameters.AddWithValue("value", (object?)probe.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("authority", probe.Authority);
        command.Parameters.AddWithValue("sourceKind", probe.SourceKind);
        command.Parameters.AddWithValue("event", probe.EventId);
        command.Parameters.AddWithValue("fieldVersion", probe.FieldVersion);
        return await OutcomeAsync(command, transaction);
    }

    private async Task<string> InsertObjectAsync(NpgsqlConnection admin, Guid tenant, string connection, string objectId, Guid eventId)
    {
        await using var transaction = await admin.BeginTransactionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO snapshot_objects (tenant_id, object_system, object_connection, object_type, object_id, version, last_event_id, processed_at_utc)
            VALUES (@tenant, 'synthetic', @connection, 'task', @object, 1, @event, now());
            """,
            admin,
            transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("connection", connection);
        command.Parameters.AddWithValue("object", objectId);
        command.Parameters.AddWithValue("event", eventId);
        return await OutcomeAsync(command, transaction);
    }

    private async Task<string> OutcomeAsync(NpgsqlCommand command, NpgsqlTransaction transaction)
    {
        try
        {
            await command.ExecuteNonQueryAsync(Token);
            return Inserted;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    /// <summary>A Snapshot field row that is valid by default; each test breaks exactly one part of it.</summary>
    private sealed record FieldProbe(Guid Tenant, string Connection, string ObjectId, Guid EventId)
    {
        public string DataType { get; init; } = "TaskStatus";

        public string Authority { get; init; } = "SourceTaskSystem";

        public string SourceKind { get; init; } = "SaasConnector";

        public string? Value { get; init; } = "probe";

        public long FieldVersion { get; init; } = 1;
    }
}
