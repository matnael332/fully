using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>
/// The tenant-less function Gate 2C adds for the outbox relay (owner decision 1), pinned against the real
/// database: only the runtime role may execute it; it returns only the ids of tenants with pending outbox
/// work, oldest first, at most 100; it works under a non-superuser owner only through that owner's own
/// pending-rows read policy; and the runtime role still reads, locks and marks nothing across tenants.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OutboxDiscoveryDatabaseSecurityTests(DatabaseFixture fixture)
{
    private const string Discovery = "public.outbox_pending_tenants(integer)";

    // Far older than anything the other tests write, so these tenants sort first.
    private static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_the_owner_and_the_runtime_role_may_execute_the_discovery_function()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var owner = (string)(await ScalarAsync(
            connection, transaction: null, $"SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid = '{Discovery}'::regprocedure;"))!;

        await using var command = new NpgsqlCommand(
            $"""
            SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, a.privilege_type
            FROM pg_proc p
            CROSS JOIN LATERAL aclexplode(p.proacl) a
            WHERE p.oid = '{Discovery}'::regprocedure;
            """,
            connection);

        var grants = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                grants.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
            }
        }

        Assert.Equal(
            new[] { $"{owner}:EXECUTE", $"{PersistenceOptions.RuntimeRoleName}:EXECUTE" }.Order(StringComparer.Ordinal),
            grants.Order(StringComparer.Ordinal));

        var probe = "wce_relay_probe_" + Guid.NewGuid().ToString("N")[..12];
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN;");
            var allowed = (bool)(await ScalarAsync(
                connection, transaction, $"SELECT has_function_privilege('{probe}', '{Discovery}', 'EXECUTE');"))!;
            Assert.False(allowed);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    [Fact]
    public async Task The_function_returns_only_ids_of_tenants_with_pending_work_oldest_first_and_at_most_one_hundred()
    {
        var (tenantA, tenantB, published) = (TenantId.New(), TenantId.New(), TenantId.New());
        var seeded = new List<Guid>
        {
            await SeedAsync(tenantA, Epoch),
            await SeedAsync(tenantA, Epoch.AddMinutes(5)),
            await SeedAsync(tenantB, Epoch.AddMinutes(2)),
        };
        var publishedRow = await SeedAsync(published, Epoch.AddMinutes(1));
        seeded.Add(publishedRow);
        await IngressData.ExecuteAdministrativeAsync(
            fixture, $"UPDATE outbox_messages SET published_at_utc = now() WHERE outbox_message_id = '{publishedRow}';", Token);

        try
        {
            // As the runtime role with no tenant set: exactly the state the relay is in before discovery.
            await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
            await connection.OpenAsync(Token);

            var (columns, firstTwo) = await DiscoverAsync(connection, 2);
            Assert.Equal(["tenant_id"], columns);
            Assert.Equal([tenantA.Value, tenantB.Value], firstTwo);

            var (_, one) = await DiscoverAsync(connection, 1);
            Assert.Equal([tenantA.Value], one);

            var (_, many) = await DiscoverAsync(connection, 1000);
            Assert.InRange(many.Count, 2, 100);
            Assert.Equal(many.Count, many.Distinct().Count());
            Assert.DoesNotContain(published.Value, many);

            Assert.Empty((await DiscoverAsync(connection, 0)).Tenants);
            Assert.Empty((await DiscoverAsync(connection, -5)).Tenants);
            Assert.Empty((await DiscoverAsync(connection, limit: null)).Tenants);

            // The function is the only way in: the runtime role reads, and can lock, no outbox row directly.
            Assert.Equal(0L, await ScalarAsync(connection, transaction: null, "SELECT count(*) FROM outbox_messages;"));
            Assert.Equal(
                0L,
                await ScalarAsync(connection, transaction: null, "SELECT count(*) FROM (SELECT 1 FROM outbox_messages FOR UPDATE SKIP LOCKED) AS locked;"));
        }
        finally
        {
            await TransportData.DeleteOutboxRowsAsync(fixture, seeded, Token);
        }
    }

    [Fact]
    public async Task The_runtime_role_cannot_lock_read_or_mark_another_tenants_outbox_rows()
    {
        var (tenantA, tenantB) = (TenantId.New(), TenantId.New());
        var rowB = await SeedAsync(tenantB, IngressHost.Now);

        try
        {
            var (database, context) = fixture.CreateTenantScoped(tenantA);
            await using (context)
            {
                var seen = await database.ExecuteAsync(
                    async (dbContext, token) =>
                    {
                        var locked = (await dbContext.OutboxMessages
                            .FromSql($"SELECT * FROM public.outbox_messages WHERE outbox_message_id = {rowB} FOR UPDATE SKIP LOCKED")
                            .ToListAsync(token)).Count;
                        var marked = await dbContext.OutboxMessages
                            .Where(message => message.OutboxMessageId == rowB)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.PublishedAtUtc, IngressHost.Now), token);
                        return (locked, marked);
                    },
                    Token);

                Assert.True(seen.IsSuccess, seen.IsSuccess ? string.Empty : seen.Error.Code);
                Assert.Equal((0, 0), seen.Value);
            }

            Assert.Null(Assert.Single(await TransportData.RowsAsync(fixture, [rowB], Token)).PublishedAtUtc);
        }
        finally
        {
            await TransportData.DeleteOutboxRowsAsync(fixture, [rowB], Token);
        }
    }

    [Fact]
    public async Task A_non_superuser_owner_finds_pending_tenants_only_through_its_own_read_policy()
    {
        // In Azure the migrating role that owns the table and the function is not a superuser, so forced
        // row-level security binds it. This rehearses that inside a transaction that is always rolled back:
        // a non-superuser role takes ownership of both, and the function finds the pending tenant only once
        // that owner holds the migration's own pending-rows read policy.
        var tenant = TenantId.New();
        var probe = "wce_relay_owner_probe_" + Guid.NewGuid().ToString("N")[..12];

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var ownerPredicate = (string)(await ScalarAsync(
            connection,
            transaction: null,
            """
            SELECT qual FROM pg_policies
            WHERE schemaname = 'public' AND tablename = 'outbox_messages'
              AND policyname = 'relay_discovery_outbox_messages';
            """))!;

        bool withoutPolicy;
        bool withPolicy;
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN NOSUPERUSER NOBYPASSRLS;");
            await ExecuteAsync(connection, transaction, $"ALTER TABLE public.outbox_messages OWNER TO {probe};");
            await ExecuteAsync(connection, transaction, $"ALTER FUNCTION {Discovery} OWNER TO {probe};");
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                INSERT INTO public.outbox_messages (outbox_message_id, tenant_id, message_type, payload, occurred_at_utc, correlation_id)
                VALUES ('{Guid.CreateVersion7()}', '{tenant.Value}', '{IngressMessageTypes.ReceiptReceived}', '{TransportData.Marker(Guid.CreateVersion7())}', '1999-01-01T00:00:00Z', 'rehearsal-correlation');
                """);

            withoutPolicy = await DiscoversAsRuntimeRoleAsync(connection, transaction, tenant);

            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE POLICY probe_owner_pending ON public.outbox_messages AS PERMISSIVE FOR SELECT TO {probe} USING ({ownerPredicate});");

            withPolicy = await DiscoversAsRuntimeRoleAsync(connection, transaction, tenant);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        Assert.False(withoutPolicy);
        Assert.True(withPolicy);
    }

    private async Task<Guid> SeedAsync(TenantId tenant, DateTimeOffset occurredAtUtc) =>
        await TransportData.SeedOutboxRowAsync(
            fixture, tenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(Guid.CreateVersion7()), occurredAtUtc, Token);

    private async Task<(string[] Columns, List<Guid> Tenants)> DiscoverAsync(NpgsqlConnection connection, int? limit)
    {
        await using var command = new NpgsqlCommand("SELECT * FROM public.outbox_pending_tenants(@limit);", connection);
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit is { } value ? value : DBNull.Value });

        var tenants = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        while (await reader.ReadAsync(Token))
        {
            tenants.Add(reader.GetGuid(0));
        }

        return (columns, tenants);
    }

    private async Task<bool> DiscoversAsRuntimeRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenant)
    {
        await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {PersistenceOptions.RuntimeRoleName};");
        var found = (bool)(await ScalarAsync(
            connection,
            transaction,
            $"SELECT EXISTS (SELECT 1 FROM public.outbox_pending_tenants(100) AS t WHERE t.tenant_id = '{tenant.Value}');"))!;
        await ExecuteAsync(connection, transaction, "RESET ROLE;");
        return found;
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(Token);
    }
}
