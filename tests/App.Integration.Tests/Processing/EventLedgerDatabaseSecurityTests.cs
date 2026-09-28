using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>
/// The Gate 2D ledger tables, pinned against the real database: no column can hold raw content; the runtime role
/// holds only what processing needs; the ledger is append-only even for its own tenant and even if privileges
/// were re-granted; and a foreign key can never point across tenants.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventLedgerDatabaseSecurityTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_ledger_tables_have_exactly_their_metadata_columns_and_none_that_could_hold_content()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["event_ledger"] =
            [
                "connection_reference", "correlation_id", "duplicate_candidate_of", "event_id", "event_type", "object_connection",
                "object_id", "object_system", "object_type", "occurred_at_utc", "outcome", "processed_at_utc", "quarantine_reason",
                "reason_code", "receipt_id", "received_at_utc", "schema_version", "source_event_id", "source_kind", "source_revision",
                "source_system", "source_updated_at_utc", "tenant_id",
            ],
            ["event_ledger_fields"] = ["data_type", "decision", "event_id", "field", "tenant_id"],
            ["field_ordering_states"] =
            [
                "accepted_at_utc", "field", "last_event_id", "object_connection", "object_id", "object_system", "object_type",
                "occurred_at_utc", "source_event_id", "source_revision", "source_updated_at_utc", "tenant_id",
            ],
        };

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        foreach (var (table, columns) in expected)
        {
            await using var command = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table ORDER BY column_name;",
                connection);
            command.Parameters.AddWithValue("table", table);

            var actual = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                actual.Add(reader.GetString(0));
            }

            Assert.Equal(columns.Order(StringComparer.Ordinal), actual);
            Assert.DoesNotContain(actual, column =>
                column.Contains("payload", StringComparison.Ordinal)
                || column.Contains("value", StringComparison.Ordinal)
                || column.Contains("before", StringComparison.Ordinal)
                || column.Contains("after", StringComparison.Ordinal)
                || column.Contains("actor", StringComparison.Ordinal)
                || column.Contains("reference_url", StringComparison.Ordinal)
                || column.Contains("fingerprint", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_runtime_role_holds_what_processing_needs_and_never_truncate()
    {
        // UPDATE and DELETE are revoked by the migration but re-granted by any later bootstrap run, which grants
        // DML on every table; the restrictive policies, tested below, are what makes the guarantee durable.
        // TRUNCATE bypasses row-level security, and no path ever grants it.
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var expected = new (string Table, string Privilege, bool Granted)[]
        {
            ("event_ledger", "SELECT", true), ("event_ledger", "INSERT", true), ("event_ledger", "TRUNCATE", false),
            ("event_ledger_fields", "SELECT", true), ("event_ledger_fields", "INSERT", true), ("event_ledger_fields", "TRUNCATE", false),
            ("field_ordering_states", "SELECT", true), ("field_ordering_states", "INSERT", true), ("field_ordering_states", "UPDATE", true),
            ("field_ordering_states", "TRUNCATE", false),
        };

        foreach (var (table, privilege, granted) in expected)
        {
            await using var command = new NpgsqlCommand("SELECT has_table_privilege(@role, @table, @privilege);", connection);
            command.Parameters.AddWithValue("role", PersistenceOptions.RuntimeRoleName);
            command.Parameters.AddWithValue("table", "public." + table);
            command.Parameters.AddWithValue("privilege", privilege);
            Assert.True(granted == (bool)(await command.ExecuteScalarAsync(Token))!, $"{privilege} on {table}");
        }
    }

    [Fact]
    public async Task The_ledger_is_append_only_even_for_its_own_tenant_and_even_with_privileges_re_granted()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var delivered = await host.SubmitAsync(fixture, tenant, connection, new SyntheticEvent { Revision = 1 }, "evt-append-only");
        var result = await host.ProcessSettledAsync(delivered.Envelope);

        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var transaction = await admin.BeginTransactionAsync(Token);
        try
        {
            // Rehearse a later bootstrap that re-grants broad privileges; the restrictive policies still hold.
            await ExecuteAsync(admin, transaction, $"GRANT UPDATE, DELETE ON event_ledger, event_ledger_fields, field_ordering_states TO {PersistenceOptions.RuntimeRoleName};");
            await ExecuteAsync(admin, transaction, $"SET LOCAL ROLE {PersistenceOptions.RuntimeRoleName};");
            await ExecuteAsync(admin, transaction, $"SELECT set_config('app.tenant_id', '{tenant.Value}', true);");

            Assert.Equal(1, await CountAsync(admin, transaction, $"SELECT count(*) FROM event_ledger WHERE event_id = '{result.EventId.Value}';"));
            Assert.Equal(0, await AffectedAsync(admin, transaction, $"UPDATE event_ledger SET reason_code = 'rewritten' WHERE event_id = '{result.EventId.Value}';"));
            Assert.Equal(0, await AffectedAsync(admin, transaction, $"DELETE FROM event_ledger WHERE event_id = '{result.EventId.Value}';"));
            Assert.Equal(0, await AffectedAsync(admin, transaction, $"DELETE FROM event_ledger_fields WHERE event_id = '{result.EventId.Value}';"));
            Assert.Equal(0, await AffectedAsync(admin, transaction, $"UPDATE event_ledger_fields SET decision = 'Stale' WHERE event_id = '{result.EventId.Value}';"));
            Assert.Equal(0, await AffectedAsync(admin, transaction, $"DELETE FROM field_ordering_states WHERE last_event_id = '{result.EventId.Value}';"));
            Assert.Equal(1, await CountAsync(admin, transaction, $"SELECT count(*) FROM field_ordering_states WHERE last_event_id = '{result.EventId.Value}';"));
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        // Whatever the runtime role's current grants, the ledger entry, its field and the ordering state are intact.
        Assert.Equal("AcceptedForProjection", Assert.Single(await ProcessingHarness.LedgerAsync(fixture, tenant)).Outcome);
        Assert.Single(await ProcessingHarness.LedgerFieldsAsync(fixture, result.EventId.Value));
        Assert.Single(await ProcessingHarness.FieldStatesAsync(fixture, tenant));
    }

    [Fact]
    public async Task A_field_or_ordering_state_can_never_point_at_another_tenants_event()
    {
        await using var host = ProcessingHarness.Host(fixture, new InMemoryTransport());
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var tenantB = TenantId.New();
        var eventA = (await host.ProcessSettledAsync((await host.SubmitAsync(fixture, tenantA, connectionA, new SyntheticEvent { Revision = 1 }, "evt-fk")).Envelope)).EventId.Value;

        // Even the schema owner, which bypasses row-level security here, is stopped by the tenant-consistent keys.
        // The field is one event A never changed, so no primary key collides and only the foreign key can refuse.
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        var field = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            admin,
            transaction: null,
            $"INSERT INTO event_ledger_fields (event_id, field, tenant_id, data_type, decision) VALUES ('{eventA}', 'due_date', '{tenantB.Value}', 'Deadline', 'Advance');"));
        var state = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            admin,
            transaction: null,
            $"""
            INSERT INTO field_ordering_states (tenant_id, object_system, object_connection, object_type, object_id, field, last_event_id, accepted_at_utc)
            VALUES ('{tenantB.Value}', 'synthetic', 'c', 'task', '1', 'status', '{eventA}', now());
            """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, field.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, state.SqlState);
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<int> AffectedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
}
