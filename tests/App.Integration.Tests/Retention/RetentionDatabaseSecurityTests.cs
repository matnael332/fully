using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Retention;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Retention;

/// <summary>
/// The tenant-less function Gate 2F adds for retention (owner decision 1), the purge's statements and the receipt-time
/// stamp, pinned against the real database. The database's clock alone decides and alone stamps the age basis: every
/// receipt time is the database's now at insert, whatever an insert supplies, and never changes; the function takes no
/// cutoff and reports only tenants holding content the database shows at least 672 hours old; the purge statement
/// clears exactly that content and reports each cleared receipt's age by the same clock; and what is left is measured
/// by it too. Each boundary is proven to the microsecond inside one transaction, where the database's now does not
/// move. Only the runtime role may execute the function; it returns only tenant ids, oldest due content first, at
/// most 100; it works under a non-superuser owner only through that owner's own due-content read policy; the runtime
/// role reads no receipt without a tenant; and the purge statement clears only its own tenant's due content, keeping
/// the payload and fingerprint cleared together.
/// </summary>
/// <remarks>
/// Receipts at chosen times are seeded in the local superuser's replica mode, which skips the receipt-time trigger. That
/// is test-only: neither the runtime role nor a non-superuser owner can do it, as the tests below show.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed partial class RetentionDatabaseSecurityTests(DatabaseFixture fixture)
{
    private const string Function = "public.retention_expired_tenants(integer)";
    private const string Stamp = "stamp_receipt_time_raw_receipts";

    // Far older than anything the other tests write, so these tenants sort first.
    private static readonly DateTimeOffset Epoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TimeSpan Microsecond => RetentionHarness.Microsecond;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_the_owner_and_the_runtime_role_may_execute_the_retention_function()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var owner = (string)(await ScalarAsync(
            connection, transaction: null, $"SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid = '{Function}'::regprocedure;"))!;

        Assert.Equal(
            new[] { $"{owner}:EXECUTE", $"{PersistenceOptions.RuntimeRoleName}:EXECUTE" }.Order(StringComparer.Ordinal),
            (await GrantsAsync(connection, Function)).Order(StringComparer.Ordinal));

        var probe = "wce_retention_probe_" + Guid.NewGuid().ToString("N")[..12];
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN;");
            var allowed = (bool)(await ScalarAsync(
                connection, transaction, $"SELECT has_function_privilege('{probe}', '{Function}', 'EXECUTE');"))!;
            Assert.False(allowed);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    [Fact]
    public async Task The_receipt_time_is_the_database_clock_at_insert_whatever_is_supplied_and_can_never_change()
    {
        // One transaction as the runtime role in one tenant, always rolled back, where the database's now does not
        // move. Whatever receipt time an insert supplies, ten years back, ten years ahead or none, the database stores
        // its own now, so no application clock can make content look older or younger; and no update can move a stored
        // receipt time.
        var tenant = TenantId.New();
        var (behind, ahead, omitted) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await SetTenantAsync(connection, transaction, tenant);
            var now = await TimeAsync(connection, transaction, "SELECT now();");
            await SupplyReceiptTimeAsync(connection, transaction, tenant, behind, "now() - interval '3650 days'");
            await SupplyReceiptTimeAsync(connection, transaction, tenant, ahead, "now() + interval '3650 days'");
            await SupplyReceiptTimeAsync(connection, transaction, tenant, omitted, received: null);

            foreach (var receiptId in new[] { behind, ahead, omitted })
            {
                Assert.Equal(now, await TimeAsync(connection, transaction, $"SELECT received_at_utc FROM public.raw_receipts WHERE receipt_id = '{receiptId}';"));
            }

            // Nothing can move a stored receipt time: here the runtime role tries to make its content look younger.
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                connection, transaction, $"UPDATE public.raw_receipts SET received_at_utc = received_at_utc + interval '30 days' WHERE receipt_id = '{behind}';"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            Assert.Equal(Stamp, refused.ConstraintName);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    [Fact]
    public async Task Neither_the_owner_nor_the_runtime_role_can_supply_move_or_switch_off_the_receipt_time_stamp()
    {
        // The owner, outside replica mode, is stamped and refused like everyone else, always rolled back.
        var tenant = TenantId.New();
        var receiptId = Guid.CreateVersion7();
        await using (var owner = await fixture.OpenAdministrativeConnectionAsync(Token))
        {
            await using var transaction = await owner.BeginTransactionAsync(Token);
            try
            {
                var now = await TimeAsync(owner, transaction, "SELECT now();");
                await SupplyReceiptTimeAsync(owner, transaction, tenant, receiptId, "timestamptz '2001-01-01 00:00:00+00'");
                Assert.Equal(now, await TimeAsync(owner, transaction, $"SELECT received_at_utc FROM public.raw_receipts WHERE receipt_id = '{receiptId}';"));

                var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    owner, transaction, $"UPDATE public.raw_receipts SET received_at_utc = received_at_utc - interval '30 days' WHERE receipt_id = '{receiptId}';"));
                Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
                Assert.Equal(Stamp, refused.ConstraintName);
            }
            finally
            {
                await transaction.RollbackAsync(Token);
            }
        }

        // The runtime role can neither disable the trigger nor skip it.
        await using var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        var disable = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            runtime, transaction: null, $"ALTER TABLE public.raw_receipts DISABLE TRIGGER {Stamp};"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, disable.SqlState);
        var replica = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            runtime, transaction: null, "SET session_replication_role = replica;"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, replica.SqlState);
    }

    [Fact]
    public async Task The_receipt_time_stamp_is_one_enabled_trigger_whose_function_is_no_definer_and_reads_nothing()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);

        // The column's default is the database's now.
        Assert.Equal(
            "now()",
            await ScalarAsync(
                connection,
                transaction: null,
                "SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'raw_receipts' AND column_name = 'received_at_utc';"));

        // The receipt-time trigger is enabled, before every insert and every update of the receipt time.
        await using var command = new NpgsqlCommand(
            """
            SELECT t.tgname, pg_get_triggerdef(t.oid), t.tgenabled::text, p.oid::regprocedure::text, p.prosecdef,
                   p.proconfig, l.lanname, p.prosrc, pg_get_userbyid(p.proowner),
                   (SELECT pg_get_userbyid(c.relowner) FROM pg_class c WHERE c.oid = 'public.raw_receipts'::regclass)
            FROM pg_trigger t
            JOIN pg_proc p ON p.oid = t.tgfoid
            JOIN pg_language l ON l.oid = p.prolang
            WHERE t.tgrelid = 'public.raw_receipts'::regclass AND NOT t.tgisinternal;
            """,
            connection);
        var triggers = new List<(string Name, string Definition, string Enabled, string Function, bool Definer, string[] Config, string Language, string Body, string Owner, string TableOwner)>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                triggers.Add((
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                    reader.GetFieldValue<string[]>(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)));
            }
        }

        // Exactly the two reviewed triggers: this receipt-time stamp, and Gate 3F's ingress-position keeper, whose own
        // checks are in InitialSyncDatabaseSecurityTests.
        Assert.Equal(["keep_ingress_sequence_raw_receipts", Stamp], triggers.Select(candidate => candidate.Name).Order(StringComparer.Ordinal));
        var trigger = Assert.Single(triggers, candidate => candidate.Name == Stamp);
        Assert.Equal(Stamp, trigger.Name);
        Assert.Contains(
            "BEFORE INSERT OR UPDATE OF received_at_utc ON public.raw_receipts FOR EACH ROW EXECUTE FUNCTION",
            trigger.Definition,
            StringComparison.Ordinal);
        Assert.EndsWith("stamp_receipt_time()", trigger.Definition, StringComparison.Ordinal);
        Assert.Equal("O", trigger.Enabled);
        Assert.Equal("stamp_receipt_time()", trigger.Function);
        Assert.False(trigger.Definer);
        Assert.Equal(["search_path=pg_catalog, pg_temp"], trigger.Config);
        Assert.Equal("plpgsql", trigger.Language);
        Assert.Contains("NEW.received_at_utc := now();", trigger.Body, StringComparison.Ordinal);
        Assert.Contains("ELSIF NEW.received_at_utc IS DISTINCT FROM OLD.received_at_utc THEN", trigger.Body, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "SELECT", "EXECUTE", "PERFORM", "DELETE", "COPY", "current_setting" })
        {
            Assert.DoesNotContain(forbidden, trigger.Body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(trigger.TableOwner, trigger.Owner);

        // EXECUTE is revoked from PUBLIC: no role but the owner holds it, and a trigger needs no grant to fire.
        Assert.Equal([$"{trigger.Owner}:EXECUTE"], await GrantsAsync(connection, "public.stamp_receipt_time()"));

        // Besides what extensions bring, the schema's only functions are the three approved tenant-less functions, this
        // trigger's, and Gate 3F's ingress-position trigger's.
        Assert.Equal(
            ["keep_ingress_sequence", "outbox_pending_tenants", "resolve_ingress_connection", "retention_expired_tenants", "stamp_receipt_time"],
            await NamesAsync(
                connection,
                """
                SELECT p.proname
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public'
                  AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
                ORDER BY 1;
                """));
    }

    [Fact]
    public async Task The_application_never_writes_the_receipt_time_and_reads_back_the_databases()
    {
        var (_, context) = fixture.CreateTenantScoped(TenantId.New());
        await using (context)
        {
            // The full model: the read-optimized runtime model does not carry the default's SQL.
            var receivedAt = context.GetService<IDesignTimeModel>().Model
                .FindEntityType(typeof(RawReceiptRecord))!
                .FindProperty(nameof(RawReceiptRecord.ReceivedAtUtc))!;

            Assert.Equal(ValueGenerated.OnAdd, receivedAt.ValueGenerated);
            Assert.Equal("now()", receivedAt.GetDefaultValueSql());
            Assert.Equal(PropertySaveBehavior.Ignore, receivedAt.GetBeforeSaveBehavior());
            Assert.Equal(PropertySaveBehavior.Throw, receivedAt.GetAfterSaveBehavior());
        }
    }

    [Fact]
    public async Task The_function_returns_only_ids_of_tenants_with_expired_content_oldest_first_and_at_most_one_hundred()
    {
        var (tenantA, tenantB, purged, fresh, notYetDue) = (TenantId.New(), TenantId.New(), TenantId.New(), TenantId.New(), TenantId.New());
        var seeded = new List<Guid>
        {
            await SeedAsync(tenantA, Epoch, withContent: true),
            await SeedAsync(tenantA, Epoch.AddMinutes(5), withContent: true),
            await SeedAsync(tenantB, Epoch.AddMinutes(2), withContent: true),
            await SeedAsync(purged, Epoch.AddMinutes(1), withContent: false),
            await SeedAsync(fresh, DateTimeOffset.UtcNow.AddHours(-1), withContent: true),
            await SeedAsync(notYetDue, DateTimeOffset.UtcNow.AddDays(-27), withContent: true),
        };

        try
        {
            // As the runtime role with no tenant set: exactly the state the purge is in before discovery.
            await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
            await connection.OpenAsync(Token);

            var (columns, firstTwo) = await DiscoverAsync(connection, 2);
            Assert.Equal(["tenant_id"], columns);
            Assert.Equal([tenantA.Value, tenantB.Value], firstTwo);
            Assert.Equal([tenantA.Value], (await DiscoverAsync(connection, 1)).Tenants);

            var (_, many) = await DiscoverAsync(connection, 1000);
            Assert.InRange(many.Count, 2, 100);
            Assert.Equal(many.Count, many.Distinct().Count());
            Assert.Equal([tenantA.Value, tenantB.Value], many.Take(2));

            // Purged content is never reported, and neither is content the database's clock shows younger than the
            // 672-hour purge age: no caller can ask for more, so the function cannot reveal recent activity.
            Assert.DoesNotContain(purged.Value, many);
            Assert.DoesNotContain(fresh.Value, many);
            Assert.DoesNotContain(notYetDue.Value, many);

            Assert.Empty((await DiscoverAsync(connection, 0)).Tenants);
            Assert.Empty((await DiscoverAsync(connection, -5)).Tenants);
            Assert.Empty((await DiscoverAsync(connection, limit: null)).Tenants);

            // The function is the only way in: the runtime role reads, and can lock, no receipt directly.
            Assert.Equal(0L, await ScalarAsync(connection, transaction: null, "SELECT count(*) FROM raw_receipts;"));
            Assert.Equal(
                0L,
                await ScalarAsync(connection, transaction: null, "SELECT count(*) FROM (SELECT 1 FROM raw_receipts FOR UPDATE SKIP LOCKED) AS locked;"));
        }
        finally
        {
            await DeleteAsync(seeded);
        }
    }

    [Fact]
    public async Task The_function_reports_content_from_exactly_the_purge_age_by_the_database_clock_and_not_a_microsecond_younger()
    {
        // Rehearsed in one transaction that is always rolled back, where the database's now does not move: a
        // non-superuser owner whose only policy shows exactly this test's rows, so nothing but the function's own
        // bound decides what is reported. Content exactly 672 hours old by the database's clock is reported, oldest
        // first after content long past the limit; content a microsecond younger is not.
        var (ancient, pastLimit, exactlyDue, justYounger, fresh) = (TenantId.New(), TenantId.New(), TenantId.New(), TenantId.New(), TenantId.New());
        var probe = "wce_retention_clamp_probe_" + Guid.NewGuid().ToString("N")[..12];

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        List<Guid> reported;
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN NOSUPERUSER NOBYPASSRLS;");
            await ExecuteAsync(connection, transaction, $"ALTER TABLE public.raw_receipts OWNER TO {probe};");
            await ExecuteAsync(connection, transaction, $"ALTER FUNCTION {Function} OWNER TO {probe};");
            await InsertReceiptAsync(connection, transaction, ancient, Epoch, withContent: true);
            foreach (var (tenant, received) in new[]
                     {
                         (pastLimit, "now() - interval '721 hours'"),
                         (exactlyDue, "now() - interval '672 hours'"),
                         (justYounger, "now() - interval '672 hours' + interval '1 microsecond'"),
                         (fresh, "now() - interval '1 hour'"),
                     })
            {
                await InsertAtAsync(connection, transaction, tenant, Guid.CreateVersion7(), received);
            }

            var ours = string.Join(", ", new[] { ancient, pastLimit, exactlyDue, justYounger, fresh }.Select(tenant => $"'{tenant.Value}'"));
            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE POLICY probe_owner_only_these ON public.raw_receipts AS PERMISSIVE FOR SELECT TO {probe} USING (tenant_id IN ({ours}));");

            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {PersistenceOptions.RuntimeRoleName};");
            reported = await IdsAsync(connection, transaction, "SELECT tenant_id FROM public.retention_expired_tenants(100);");
            await ExecuteAsync(connection, transaction, "RESET ROLE;");
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        Assert.Equal([ancient.Value, pastLimit.Value, exactlyDue.Value], reported);
    }

    [Fact]
    public async Task The_purge_statements_decide_and_measure_by_the_database_clock_to_the_microsecond_at_the_purge_age_and_at_the_limit()
    {
        // One transaction, always rolled back, where the database's now does not move: the receipts are seeded at exact
        // ages, then the statements run as the runtime role in the receipts' tenant. Content exactly 672 hours old is
        // cleared and a microsecond younger is not. Each cleared receipt reports its exact age by the same clock, so
        // content exactly 720 hours old is past the limit and a microsecond younger is only overdue; and what is left,
        // before and after, is measured by the same clock.
        var tenant = TenantId.New();
        var (atLimit, underLimit, atPurgeAge, underPurgeAge, fresh) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            foreach (var (receiptId, received) in new[]
                     {
                         (atLimit, "now() - interval '720 hours'"),
                         (underLimit, "now() - interval '720 hours' + interval '1 microsecond'"),
                         (atPurgeAge, "now() - interval '672 hours'"),
                         (underPurgeAge, "now() - interval '672 hours' + interval '1 microsecond'"),
                         (fresh, "now() - interval '1 hour'"),
                     })
            {
                await InsertAtAsync(connection, transaction, tenant, receiptId, received);
            }

            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {PersistenceOptions.RuntimeRoleName};");
            await SetTenantAsync(connection, transaction, tenant);

            var oldestBefore = (TimeSpan)(await ScalarAsync(connection, transaction, OldestContentStatement()))!;
            Assert.Equal(TimeSpan.FromHours(720), oldestBefore);
            Assert.Equal(RawContentRetentionStatus.LimitExceeded, RawContentRetention.StatusOf(oldestBefore));

            var cleared = new List<(string State, TimeSpan Age)>();
            await using (var purge = new NpgsqlCommand(PurgeStatement(), connection, transaction))
            {
                purge.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = 100 });
                await using var reader = await purge.ExecuteReaderAsync(Token);
                while (await reader.ReadAsync(Token))
                {
                    cleared.Add((reader.GetString(0), reader.GetFieldValue<TimeSpan>(1)));
                }
            }

            var ages = cleared.Select(receipt => receipt.Age).OrderDescending().ToList();
            Assert.Equal([TimeSpan.FromHours(720), TimeSpan.FromHours(720) - Microsecond, TimeSpan.FromHours(672)], ages);
            Assert.Equal(
                [RawContentRetentionStatus.LimitExceeded, RawContentRetentionStatus.Overdue, RawContentRetentionStatus.WithinLimit],
                ages.Select(age => RawContentRetention.StatusOf(age)));
            Assert.All(cleared, receipt => Assert.Equal("Processed", receipt.State));

            var holding = await IdsAsync(connection, transaction, "SELECT receipt_id FROM public.raw_receipts WHERE payload IS NOT NULL ORDER BY received_at_utc;");
            Assert.Equal([underPurgeAge, fresh], holding);

            var oldestAfter = (TimeSpan)(await ScalarAsync(connection, transaction, OldestContentStatement()))!;
            Assert.Equal(TimeSpan.FromHours(672) - Microsecond, oldestAfter);
            Assert.Equal(RawContentRetentionStatus.WithinLimit, RawContentRetention.StatusOf(oldestAfter));
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    [Fact]
    public async Task The_purge_age_in_the_database_is_the_fixed_purge_age_of_the_code_and_only_the_database_clock_applies_it()
    {
        // 672 hours is exactly RawContentRetention.PurgeAge, 28 days; the function, the owner policy and the purge
        // statement all state it in hours against the database's own now, which no session time zone can stretch.
        // None still states 720, and nothing takes a cutoff: the function's one argument and the statements' one
        // parameter are counts.
        Assert.Equal(TimeSpan.FromHours(672), RawContentRetention.PurgeAge);

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        Assert.Equal(
            "p_limit integer",
            await ScalarAsync(connection, transaction: null, $"SELECT pg_get_function_identity_arguments('{Function}'::regprocedure);"));
        Assert.Equal(
            1L,
            await ScalarAsync(connection, transaction: null, "SELECT count(*) FROM pg_proc WHERE proname = 'retention_expired_tenants';"));

        var body = (string)(await ScalarAsync(connection, transaction: null, $"SELECT prosrc FROM pg_proc WHERE oid = '{Function}'::regprocedure;"))!;
        var policy = (string)(await ScalarAsync(
            connection,
            transaction: null,
            "SELECT qual FROM pg_policies WHERE schemaname = 'public' AND policyname = 'retention_discovery_raw_receipts';"))!;

        Assert.Contains("AND r.received_at_utc <= now() - interval '672 hours'", body, StringComparison.Ordinal);
        Assert.Contains("(now() - '672:00:00'::interval)", policy, StringComparison.Ordinal);
        Assert.Contains("AND received_at_utc <= now() - interval '672 hours'", PurgeStatement(), StringComparison.Ordinal);
        Assert.Contains("RETURNING receipt.processing_state, now() - receipt.received_at_utc", PurgeStatement(), StringComparison.Ordinal);
        Assert.StartsWith("SELECT now() - min(received_at_utc)", OldestContentStatement(), StringComparison.Ordinal);
        foreach (var text in new[] { body, policy, PurgeStatement(), OldestContentStatement() })
        {
            Assert.DoesNotContain("720", text, StringComparison.Ordinal);
            Assert.DoesNotContain("cutoff", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(["@batch"], StatementParameter().Matches(PurgeStatement()).Select(match => match.Value).Distinct());
        Assert.Empty(StatementParameter().Matches(OldestContentStatement()));
    }

    [Fact]
    public async Task A_non_superuser_owner_finds_expired_tenants_only_through_its_own_read_policy()
    {
        // In Azure the migrating role that owns the table and the function is not a superuser, so forced
        // row-level security binds it. This rehearses that inside a transaction that is always rolled back: a
        // non-superuser role takes ownership of both, and the function finds the expired tenant only once that
        // owner holds the migration's own expired-content read policy, which still hides content younger than
        // the purge age.
        var (expired, young) = (TenantId.New(), TenantId.New());
        var probe = "wce_retention_owner_probe_" + Guid.NewGuid().ToString("N")[..12];

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var ownerPredicate = (string)(await ScalarAsync(
            connection,
            transaction: null,
            """
            SELECT qual FROM pg_policies
            WHERE schemaname = 'public' AND tablename = 'raw_receipts'
              AND policyname = 'retention_discovery_raw_receipts';
            """))!;

        bool expiredWithoutPolicy;
        bool expiredWithPolicy;
        bool youngWithPolicy;
        long ownerSeesYoung;
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN NOSUPERUSER NOBYPASSRLS;");
            await ExecuteAsync(connection, transaction, $"ALTER TABLE public.raw_receipts OWNER TO {probe};");
            await ExecuteAsync(connection, transaction, $"ALTER FUNCTION {Function} OWNER TO {probe};");
            await InsertReceiptAsync(connection, transaction, expired, Epoch, withContent: true);
            await InsertReceiptAsync(connection, transaction, young, DateTimeOffset.UtcNow.AddHours(-1), withContent: true);

            expiredWithoutPolicy = await DiscoversAsRuntimeRoleAsync(connection, transaction, expired);

            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE POLICY probe_owner_expired ON public.raw_receipts AS PERMISSIVE FOR SELECT TO {probe} USING ({ownerPredicate});");

            expiredWithPolicy = await DiscoversAsRuntimeRoleAsync(connection, transaction, expired);
            youngWithPolicy = await DiscoversAsRuntimeRoleAsync(connection, transaction, young);

            // The owner itself, reading directly, sees no content younger than the purge age either.
            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {probe};");
            ownerSeesYoung = (long)(await ScalarAsync(
                connection, transaction, $"SELECT count(*) FROM public.raw_receipts WHERE tenant_id = '{young.Value}';"))!;
            await ExecuteAsync(connection, transaction, "RESET ROLE;");
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        Assert.False(expiredWithoutPolicy);
        Assert.True(expiredWithPolicy);
        Assert.False(youngWithPolicy);
        Assert.Equal(0L, ownerSeesYoung);
    }

    [Fact]
    public async Task The_purge_statement_clears_only_its_own_tenants_expired_content_and_both_columns_together()
    {
        var (tenantA, tenantB) = (TenantId.New(), TenantId.New());
        var expiredA = await SeedAsync(tenantA, Epoch, withContent: true);
        var youngA = await SeedAsync(tenantA, DateTimeOffset.UtcNow.AddHours(-1), withContent: true);
        var expiredB = await SeedAsync(tenantB, Epoch, withContent: true);
        var seeded = new List<Guid> { expiredA, youngA, expiredB };

        try
        {
            // The production statement itself, run as the runtime role in tenant A's transaction.
            await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
            await connection.OpenAsync(Token);
            await using (var transaction = await connection.BeginTransactionAsync(Token))
            {
                await SetTenantAsync(connection, transaction, tenantA);

                await using var purge = new NpgsqlCommand(PurgeStatement(), connection, transaction);
                purge.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = 100 });
                var cleared = 0;
                await using (var reader = await purge.ExecuteReaderAsync(Token))
                {
                    while (await reader.ReadAsync(Token))
                    {
                        cleared++;
                    }
                }

                Assert.Equal(1, cleared);
                await transaction.CommitAsync(Token);
            }

            Assert.Equal((false, false), await ContentOfAsync(expiredA));
            Assert.Equal((true, true), await ContentOfAsync(youngA));
            Assert.Equal((true, true), await ContentOfAsync(expiredB));

            // Half-cleared content is refused, whoever tries.
            var refused = await Assert.ThrowsAsync<PostgresException>(() => IngressData.ExecuteAdministrativeAsync(
                fixture, $"UPDATE raw_receipts SET payload = NULL WHERE receipt_id = '{youngA}';", Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            Assert.Equal("ck_raw_receipts_content", refused.ConstraintName);
        }
        finally
        {
            await DeleteAsync(seeded);
        }
    }

    private static string PurgeStatement() => PurgeConstant("PurgeBatchSql");

    private static string OldestContentStatement() => PurgeConstant("OldestContentAgeSql");

    private static string PurgeConstant(string name) =>
        (string)typeof(RawContentRetentionRegistration).Assembly
            .GetType("WorkContinuity.Infrastructure.Retention.RawContentPurge", throwOnError: true)!
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

    [GeneratedRegex("@[a-z_]+")]
    private static partial Regex StatementParameter();

    private async Task<Guid> SeedAsync(TenantId tenant, DateTimeOffset receivedAtUtc, bool withContent)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        var receiptId = await InsertReceiptAsync(connection, transaction, tenant, receivedAtUtc, withContent);
        await transaction.CommitAsync(Token);
        return receiptId;
    }

    /// <summary>Seeds a receipt at a chosen receipt time, in the local superuser's replica mode.</summary>
    private async Task<Guid> InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenant,
        DateTimeOffset receivedAtUtc,
        bool withContent)
    {
        var receiptId = Guid.CreateVersion7();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = replica;");
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.raw_receipts (receipt_id, tenant_id, connection_reference, source_system, source_event_id,
                schema_version, received_at_utc, payload, payload_fingerprint, processing_state)
            VALUES (@receipt, @tenant, @connection, 'synthetic', @event, '1', @received,
                CASE WHEN @content THEN '{"note":"retention-seed"}'::jsonb END,
                CASE WHEN @content THEN 'fingerprint' END,
                'Processed');
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("receipt", receiptId);
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("connection", "synthetic:" + Guid.CreateVersion7().ToString("D"));
            command.Parameters.AddWithValue("event", "evt-retention-" + receiptId.ToString("N"));
            command.Parameters.Add(new NpgsqlParameter("received", NpgsqlDbType.TimestampTz) { Value = receivedAtUtc.ToUniversalTime() });
            command.Parameters.AddWithValue("content", withContent);
            await command.ExecuteNonQueryAsync(Token);
        }

        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = origin;");
        return receiptId;
    }

    /// <summary>
    /// Seeds a receipt holding content at a time the database computes from its own now, in the local superuser's
    /// replica mode.
    /// </summary>
    private async Task InsertAtAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenant, Guid receiptId, string received)
    {
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = replica;");
        await SupplyReceiptTimeAsync(connection, transaction, tenant, receiptId, received);
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = origin;");
    }

    /// <summary>Inserts a receipt holding content, supplying the given receipt time, or none.</summary>
    private async Task SupplyReceiptTimeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenant, Guid receiptId, string? received)
    {
        var (column, value) = received is null ? (string.Empty, string.Empty) : (" received_at_utc,", $" {received},");
        await using var command = new NpgsqlCommand(
            $$"""
            INSERT INTO public.raw_receipts (receipt_id, tenant_id, connection_reference, source_system, source_event_id,
                schema_version,{{column}} payload, payload_fingerprint, processing_state)
            VALUES (@receipt, @tenant, 'synthetic:boundary', 'synthetic', @event, '1',{{value}}
                '{"note":"retention-boundary"}'::jsonb, 'fingerprint', 'Processed');
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("receipt", receiptId);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("event", "evt-boundary-" + receiptId.ToString("N"));
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenant)
    {
        await using var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true);", connection, transaction);
        scope.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
        await scope.ExecuteScalarAsync(Token);
    }

    private async Task DeleteAsync(IEnumerable<Guid> receiptIds)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("DELETE FROM raw_receipts WHERE receipt_id = ANY(@receipts);", connection);
        command.Parameters.AddWithValue("receipts", receiptIds.ToArray());
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<(bool Payload, bool Fingerprint)> ContentOfAsync(Guid receiptId)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            "SELECT payload IS NOT NULL, payload_fingerprint IS NOT NULL FROM raw_receipts WHERE receipt_id = @receipt;", connection);
        command.Parameters.AddWithValue("receipt", receiptId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task<(string[] Columns, List<Guid> Tenants)> DiscoverAsync(NpgsqlConnection connection, int? limit)
    {
        await using var command = new NpgsqlCommand("SELECT * FROM public.retention_expired_tenants(@limit);", connection);
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit is { } count ? count : DBNull.Value });

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
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.retention_expired_tenants(100) AS t WHERE t.tenant_id = @tenant);",
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        var found = (bool)(await command.ExecuteScalarAsync(Token))!;
        await ExecuteAsync(connection, transaction, "RESET ROLE;");
        return found;
    }

    private async Task<List<string>> GrantsAsync(NpgsqlConnection connection, string function)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, a.privilege_type
            FROM pg_proc p
            CROSS JOIN LATERAL aclexplode(p.proacl) a
            WHERE p.oid = '{function}'::regprocedure;
            """,
            connection);

        var grants = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            grants.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }

        return grants;
    }

    private async Task<List<string>> NamesAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var names = new List<string>();
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<Guid>> IdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Token))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<DateTimeOffset> TimeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return reader.GetFieldValue<DateTimeOffset>(0);
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
