using Npgsql;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// Gate 3B: the authorization store against the real database, as the non-owner runtime role. Every state change is a
/// compare-and-swap on durable state, a transaction is consumed once, instants are the database's own clock, and no
/// tenant can see or change another tenant's authorization.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConnectorAuthorizationStoreTests(DatabaseFixture fixture)
{
    private const string Secret = "state-secret-not-a-real-state";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_opened_transaction_keeps_hashes_and_database_times_only_and_is_consumed_once()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var transaction = ConnectorAuthorizationData.Transaction(user, Secret);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);

        var opened = await scoped.Store.OpenTransactionAsync(transaction, TimeSpan.FromMinutes(10), Token);
        Assert.True(opened.IsSuccess && opened.Value);

        // Stored: the hash, never the state; the lifetime by the database clock.
        await using (var admin = await fixture.OpenAdministrativeConnectionAsync(Token))
        await using (var command = new NpgsqlCommand(
            """
            SELECT state_hash, redirect_hash, requested_scopes, purpose, source_system,
                   expires_at_utc - created_at_utc, abs(extract(epoch FROM created_at_utc - now())) < 60, consumed_at_utc IS NULL
            FROM connector_authorization_transactions WHERE transaction_id = @id;
            """,
            admin))
        {
            command.Parameters.AddWithValue("id", transaction.TransactionId);
            await using var reader = await command.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(ConnectorAuthorizationData.Hash(Secret), reader.GetFieldValue<byte[]>(0));
            Assert.Equal(32, reader.GetFieldValue<byte[]>(1).Length);
            Assert.Equal("boards:read me:read", reader.GetString(2));
            Assert.Equal("Connect", reader.GetString(3));
            Assert.Equal("monday", reader.GetString(4));
            Assert.Equal(TimeSpan.FromMinutes(10), reader.GetTimeSpan(5));
            Assert.True(reader.GetBoolean(6));
            Assert.True(reader.GetBoolean(7));
        }

        var consumed = await scoped.Store.ConsumeTransactionAsync(transaction.TransactionId, user, ConnectorAuthorizationData.Hash(Secret), Token);
        Assert.Equal(ConnectorTransactionOutcome.Consumed, consumed.Value.Outcome);
        Assert.Equal(transaction.ConnectionId, consumed.Value.Transaction!.ConnectionId);
        Assert.Equal(ConnectorAuthorizationPurpose.Connect, consumed.Value.Transaction.Purpose);
        Assert.Equal(transaction.RedirectHash.ToArray(), consumed.Value.Transaction.RedirectHash.ToArray());

        var replayed = await scoped.Store.ConsumeTransactionAsync(transaction.TransactionId, user, ConnectorAuthorizationData.Hash(Secret), Token);
        Assert.Equal(ConnectorTransactionOutcome.AlreadyConsumed, replayed.Value.Outcome);
    }

    [Fact]
    public async Task A_lifetime_beyond_ten_minutes_is_refused_by_the_store_and_by_the_database()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);

        var refused = await scoped.Store.OpenTransactionAsync(
            ConnectorAuthorizationData.Transaction(user, Secret), TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)), Token);
        Assert.Equal(ConnectorAuthorizationErrors.InvalidRequest, refused.Error);

        // Even the schema owner cannot store a longer lifetime.
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO connector_authorization_transactions
                (transaction_id, tenant_id, canonical_user_id, connection_id, source_system, purpose, state_hash, redirect_hash,
                 requested_scopes, created_at_utc, expires_at_utc)
            VALUES (@id, @tenant, @user, @connection, 'monday', 'Connect', @hash, @hash, 'me:read', now(), now() + interval '11 minutes');
            """,
            admin);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("user", user.Value);
        command.Parameters.AddWithValue("connection", Guid.CreateVersion7());
        command.Parameters.AddWithValue("hash", ConnectorAuthorizationData.Hash(Secret));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_connector_authorization_transactions_lifetime", exception.ConstraintName);
    }

    [Fact]
    public async Task Only_an_active_user_of_the_trusted_tenant_can_open_a_transaction()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var other = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var inactive = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token, isActive: false);
        var foreign = await ConnectorAuthorizationData.SeedUserAsync(fixture, other, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);

        foreach (var user in new[] { inactive, foreign, UserId.New() })
        {
            var opened = await scoped.Store.OpenTransactionAsync(ConnectorAuthorizationData.Transaction(user, Secret), TimeSpan.FromMinutes(5), Token);
            Assert.True(opened.IsSuccess && !opened.Value);
        }

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM connector_authorization_transactions WHERE tenant_id = @tenant;", tenant));
    }

    [Fact]
    public async Task Consuming_checks_existence_expiry_user_state_and_consumption_in_that_order()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var intruder = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var right = ConnectorAuthorizationData.Hash(Secret);

        Assert.Equal(
            ConnectorTransactionOutcome.NotFound,
            (await scoped.Store.ConsumeTransactionAsync(Guid.CreateVersion7(), user, right, Token)).Value.Outcome);

        // A wrong user or a wrong state leaves the transaction to its owner: it can still be consumed afterwards.
        var transaction = ConnectorAuthorizationData.Transaction(user, Secret);
        Assert.True((await scoped.Store.OpenTransactionAsync(transaction, TimeSpan.FromMinutes(10), Token)).Value);
        Assert.Equal(
            ConnectorTransactionOutcome.BindingMismatch,
            (await scoped.Store.ConsumeTransactionAsync(transaction.TransactionId, intruder, right, Token)).Value.Outcome);
        Assert.Equal(
            ConnectorTransactionOutcome.StateMismatch,
            (await scoped.Store.ConsumeTransactionAsync(transaction.TransactionId, user, ConnectorAuthorizationData.Hash("wrong"), Token)).Value.Outcome);
        Assert.Equal(
            ConnectorTransactionOutcome.Consumed,
            (await scoped.Store.ConsumeTransactionAsync(transaction.TransactionId, user, right, Token)).Value.Outcome);

        // Expiry is the database's: an expired transaction is refused whatever else is right, before the user check.
        var expired = ConnectorAuthorizationData.Transaction(user, Secret);
        Assert.True((await scoped.Store.OpenTransactionAsync(expired, TimeSpan.FromMinutes(5), Token)).Value);
        await AgeTransactionAsync(expired.TransactionId, TimeSpan.FromMinutes(6));
        Assert.Equal(
            ConnectorTransactionOutcome.Expired,
            (await scoped.Store.ConsumeTransactionAsync(expired.TransactionId, intruder, right, Token)).Value.Outcome);
        Assert.Equal(
            ConnectorTransactionOutcome.Expired,
            (await scoped.Store.ConsumeTransactionAsync(expired.TransactionId, user, right, Token)).Value.Outcome);
    }

    [Fact]
    public async Task Concurrent_callbacks_consume_a_transaction_exactly_once()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var transaction = ConnectorAuthorizationData.Transaction(user, Secret);
        await using (var opener = ConnectorAuthorizationData.Store(fixture, tenant))
        {
            Assert.True((await opener.Store.OpenTransactionAsync(transaction, TimeSpan.FromMinutes(10), Token)).Value);
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
                var result = await scoped.Store.ConsumeTransactionAsync(transaction.TransactionId, user, ConnectorAuthorizationData.Hash(Secret), Token);
                return result.Value.Outcome;
            },
            Token)));

        Assert.Single(outcomes, outcome => outcome == ConnectorTransactionOutcome.Consumed);
        Assert.All(outcomes.Where(outcome => outcome != ConnectorTransactionOutcome.Consumed), outcome =>
            Assert.Equal(ConnectorTransactionOutcome.AlreadyConsumed, outcome));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_consume_read_claim_renew_or_disconnect_what_it_does_not_own()
    {
        var tenantA = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var tenantB = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var (userA, grant) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, tenantA, Token);
        var transaction = ConnectorAuthorizationData.Transaction(userA, Secret);
        await using (var ownStore = ConnectorAuthorizationData.Store(fixture, tenantA))
        {
            Assert.True((await ownStore.Store.OpenTransactionAsync(transaction, TimeSpan.FromMinutes(10), Token)).Value);
        }

        var before = await SnapshotAsync(grant.ConnectionId);
        await using var foreign = ConnectorAuthorizationData.Store(fixture, tenantB);

        Assert.Equal(
            ConnectorTransactionOutcome.NotFound,
            (await foreign.Store.ConsumeTransactionAsync(transaction.TransactionId, userA, ConnectorAuthorizationData.Hash(Secret), Token)).Value.Outcome);
        Assert.Null((await foreign.Store.ReadAsync(grant.ConnectionId, Token)).Value);
        Assert.False((await foreign.Store.ClaimRefreshAsync(grant.ConnectionId, 1, Guid.CreateVersion7(), Token)).Value);
        Assert.False((await foreign.Store.RequireReauthorizationAsync(grant.ConnectionId, 1, claim: null, Token)).Value);
        Assert.False((await foreign.Store.ReauthorizeAsync(grant, Token)).Value);
        Assert.Equal(ConnectorDisconnectOutcome.NotFound, (await foreign.Store.DisconnectAsync(grant.ConnectionId, userA, Token)).Value.Outcome);
        Assert.False((await foreign.Store.RecordProviderRevocationAsync(grant.ConnectionId, confirmed: true, Token)).Value);
        Assert.Empty((await foreign.Store.CloseExpiredTransactionsAsync(100, Token)).Value);

        Assert.Equal(before, await SnapshotAsync(grant.ConnectionId));
        await using var owner = ConnectorAuthorizationData.Store(fixture, tenantA);
        Assert.Equal(
            ConnectorTransactionOutcome.Consumed,
            (await owner.Store.ConsumeTransactionAsync(transaction.TransactionId, userA, ConnectorAuthorizationData.Hash(Secret), Token)).Value.Outcome);
    }

    [Fact]
    public async Task A_row_can_never_name_another_tenants_user_or_connection()
    {
        var tenantA = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var tenantB = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var userA = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenantA, Token);
        var userB = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenantB, Token);
        var connectionA = await Ingress.IngressData.RegisterConnectionAsync(fixture, tenantA, Infrastructure.Persistence.Entities.ConnectionStatus.Active, "monday", Token);
        var connectionB = await Ingress.IngressData.RegisterConnectionAsync(fixture, tenantB, Infrastructure.Persistence.Entities.ConnectionStatus.Active, "monday", Token);

        // Foreign keys are checked without row-level security, so each includes the tenant: a row of tenant A can name
        // neither tenant B's user nor tenant B's connection, even when written by the schema owner.
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        foreach (var (user, connection, constraint) in new[]
                 {
                     (userB, connectionA, "fk_connector_authorizations_user"),
                     (userA, connectionB, "fk_connector_authorizations_connection"),
                 })
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO connector_authorizations
                    (connection_id, tenant_id, canonical_user_id, state, provider_subject, provider_account, generation,
                     material_version, access_expires_at_utc, granted_scopes, authorized_at_utc)
                VALUES (@connection, @tenant, @user, 'Active', '1', '2', 1, @version, now(), 'me:read', now());
                """,
                admin);
            command.Parameters.AddWithValue("connection", connection);
            command.Parameters.AddWithValue("tenant", tenantA.Value);
            command.Parameters.AddWithValue("user", user.Value);
            command.Parameters.AddWithValue("version", Guid.CreateVersion7());
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
            Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, constraint), (exception.SqlState, exception.ConstraintName));
        }

        // Through the store in tenant A's scope, tenant B's user cannot delegate anything.
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenantA);
        Assert.False((await scoped.Store.ActivateAsync(ConnectorAuthorizationData.Grant(userB), Token)).Value);
        Assert.False((await scoped.Store.OpenTransactionAsync(ConnectorAuthorizationData.Transaction(userB, Secret), TimeSpan.FromMinutes(5), Token)).Value);
    }

    [Fact]
    public async Task Activation_registers_an_enabled_connection_and_its_active_authorization_at_generation_one()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var (user, grant) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);

        var authorization = (await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal(tenant, authorization.Tenant);
        Assert.Equal(user, authorization.CanonicalUser);
        Assert.Equal(ConnectorAuthorizationState.Active, authorization.State);
        Assert.True(authorization.ConnectionEnabled && authorization.CanonicalUserActive);
        Assert.Equal((1L, grant.MaterialVersion, "58201774", "9001", "monday"), (authorization.Generation, authorization.MaterialVersion, authorization.ProviderSubject, authorization.ProviderAccount, authorization.SourceSystem));
        Assert.Null(authorization.RefreshClaim);
        Assert.Equal(new ConnectorIdentity(tenant, grant.ConnectionId, ConnectorIdentityKind.DelegatedUser, "58201774"), authorization.DelegatedIdentity);

        // The Gate 2 contract: ingress resolves the connection to its tenant.
        Assert.Equal((tenant.Value, "monday"), await ResolveAsync(grant.ConnectionId));

        // An inactive user delegates nothing, and nothing is written.
        var inactive = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token, isActive: false);
        var refused = ConnectorAuthorizationData.Grant(inactive);
        Assert.False((await scoped.Store.ActivateAsync(refused, Token)).Value);
        Assert.Null((await scoped.Store.ReadAsync(refused.ConnectionId, Token)).Value);
        Assert.Null(await ResolveAsync(refused.ConnectionId));
    }

    [Fact]
    public async Task Exactly_one_of_many_concurrent_callers_claims_a_renewal()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var (_, grant) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, tenant, Token);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
                return (await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, Guid.CreateVersion7(), Token)).Value;
            },
            Token)));

        Assert.Single(claims, claimed => claimed);
    }

    [Fact]
    public async Task A_renewal_completes_only_from_its_generation_with_its_claim_and_its_age_is_the_database_clocks()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var (_, grant) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var claim = Guid.CreateVersion7();
        var expiry = DateTimeOffset.UtcNow.AddHours(2);

        Assert.True((await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, claim, Token)).Value);
        Assert.False((await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, Guid.CreateVersion7(), Token)).Value);
        await AgeClaimAsync(grant.ConnectionId, TimeSpan.FromMinutes(3));
        Assert.True((await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!.RefreshClaimAge >= TimeSpan.FromMinutes(3));

        // A stale generation or a foreign claim changes nothing.
        Assert.False((await scoped.Store.CompleteRefreshAsync(grant.ConnectionId, 2, claim, expiry, ConnectorAuthorizationData.Scopes, Token)).Value);
        Assert.False((await scoped.Store.CompleteRefreshAsync(grant.ConnectionId, 1, Guid.CreateVersion7(), expiry, ConnectorAuthorizationData.Scopes, Token)).Value);
        Assert.False((await scoped.Store.ReleaseRefreshAsync(grant.ConnectionId, Guid.CreateVersion7(), Token)).Value);

        Assert.True((await scoped.Store.CompleteRefreshAsync(grant.ConnectionId, 1, claim, expiry, ConnectorAuthorizationData.Scopes, Token)).Value);
        var renewed = (await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal((2L, claim), (renewed.Generation, renewed.MaterialVersion));
        Assert.Null(renewed.RefreshClaim);
        Assert.Null(renewed.RefreshClaimAge);
        Assert.Equal(expiry.ToUnixTimeMilliseconds() / 1000, renewed.AccessExpiresAt.ToUnixTimeMilliseconds() / 1000);

        // The completed renewal cannot complete twice.
        Assert.False((await scoped.Store.CompleteRefreshAsync(grant.ConnectionId, 1, claim, expiry, ConnectorAuthorizationData.Scopes, Token)).Value);
    }

    [Fact]
    public async Task Reauthorization_is_required_by_compare_and_swap_and_only_the_same_user_and_identity_restore_it()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var (user, grant) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, tenant, Token);
        var otherUser = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var claim = Guid.CreateVersion7();

        // Held claim: only that claim, at that generation, moves it.
        Assert.True((await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, claim, Token)).Value);
        Assert.False((await scoped.Store.RequireReauthorizationAsync(grant.ConnectionId, 1, claim: null, Token)).Value);
        Assert.False((await scoped.Store.RequireReauthorizationAsync(grant.ConnectionId, 2, claim, Token)).Value);
        Assert.True((await scoped.Store.RequireReauthorizationAsync(grant.ConnectionId, 1, claim, Token)).Value);
        var required = (await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, required.State);
        Assert.Null(required.RefreshClaim);
        Assert.False((await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, Guid.CreateVersion7(), Token)).Value);

        // Restoring needs the same canonical user and the same provider identity.
        Assert.False((await scoped.Store.ReauthorizeAsync(ConnectorAuthorizationData.Grant(otherUser, grant.ConnectionId), Token)).Value);
        Assert.False((await scoped.Store.ReauthorizeAsync(ConnectorAuthorizationData.Grant(user, grant.ConnectionId, subject: "11111111"), Token)).Value);
        Assert.False((await scoped.Store.ReauthorizeAsync(ConnectorAuthorizationData.Grant(user, grant.ConnectionId, account: "12345"), Token)).Value);
        var restoring = ConnectorAuthorizationData.Grant(user, grant.ConnectionId);
        Assert.True((await scoped.Store.ReauthorizeAsync(restoring, Token)).Value);
        var restored = (await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal((ConnectorAuthorizationState.Active, 2L, restoring.MaterialVersion), (restored.State, restored.Generation, restored.MaterialVersion));

        // An active authorization is not reauthorized again.
        Assert.False((await scoped.Store.ReauthorizeAsync(ConnectorAuthorizationData.Grant(user, grant.ConnectionId), Token)).Value);
    }

    [Fact]
    public async Task Disconnecting_revokes_and_disables_in_one_transaction_only_for_the_owner_and_nothing_restores_it()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var (user, grant) = await ConnectorAuthorizationData.SeedActiveAsync(fixture, tenant, Token);
        var otherUser = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var claim = Guid.CreateVersion7();
        Assert.True((await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, claim, Token)).Value);

        Assert.Equal(ConnectorDisconnectOutcome.NotPermitted, (await scoped.Store.DisconnectAsync(grant.ConnectionId, otherUser, Token)).Value.Outcome);
        Assert.Equal(ConnectorAuthorizationState.Active, (await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!.State);

        var disconnected = (await scoped.Store.DisconnectAsync(grant.ConnectionId, user, Token)).Value;
        Assert.Equal(ConnectorDisconnectOutcome.Disconnected, disconnected.Outcome);
        Assert.Equal((grant.MaterialVersion, claim), (disconnected.MaterialVersion!.Value, disconnected.PendingMaterialVersion!.Value));

        var revoked = (await scoped.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal(ConnectorAuthorizationState.Revoked, revoked.State);
        Assert.False(revoked.ConnectionEnabled);
        Assert.Null(revoked.RefreshClaim);
        Assert.Null(await ResolveAsync(grant.ConnectionId));

        // Final: nothing claims, completes, reauthorizes or re-enables it.
        Assert.Equal(ConnectorDisconnectOutcome.AlreadyDisconnected, (await scoped.Store.DisconnectAsync(grant.ConnectionId, user, Token)).Value.Outcome);
        Assert.False((await scoped.Store.ClaimRefreshAsync(grant.ConnectionId, 1, Guid.CreateVersion7(), Token)).Value);
        Assert.False((await scoped.Store.CompleteRefreshAsync(grant.ConnectionId, 1, claim, DateTimeOffset.UtcNow.AddHours(1), ConnectorAuthorizationData.Scopes, Token)).Value);
        Assert.False((await scoped.Store.ReauthorizeAsync(ConnectorAuthorizationData.Grant(user, grant.ConnectionId), Token)).Value);

        Assert.True((await scoped.Store.RecordProviderRevocationAsync(grant.ConnectionId, confirmed: false, Token)).Value);
        Assert.Equal(false, await ScalarAsync<bool?>("SELECT provider_revocation_confirmed FROM connector_authorizations WHERE connection_id = @id;", grant.ConnectionId));
    }

    [Fact]
    public async Task Expired_transactions_are_closed_in_bounded_batches_and_returned_for_cleanup()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var expired = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var transaction = ConnectorAuthorizationData.Transaction(user, Secret);
            Assert.True((await scoped.Store.OpenTransactionAsync(transaction, TimeSpan.FromMinutes(5), Token)).Value);
            await AgeTransactionAsync(transaction.TransactionId, TimeSpan.FromMinutes(6));
            expired.Add(transaction.TransactionId);
        }

        var open = ConnectorAuthorizationData.Transaction(user, Secret);
        Assert.True((await scoped.Store.OpenTransactionAsync(open, TimeSpan.FromMinutes(5), Token)).Value);

        Assert.Equal(ConnectorAuthorizationErrors.InvalidRequest, (await scoped.Store.CloseExpiredTransactionsAsync(0, Token)).Error);
        var first = (await scoped.Store.CloseExpiredTransactionsAsync(2, Token)).Value;
        var second = (await scoped.Store.CloseExpiredTransactionsAsync(100, Token)).Value;
        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Equal(expired.Order(), first.Concat(second).Order());
        Assert.Empty((await scoped.Store.CloseExpiredTransactionsAsync(100, Token)).Value);
        Assert.Equal(
            ConnectorTransactionOutcome.Consumed,
            (await scoped.Store.ConsumeTransactionAsync(open.TransactionId, user, ConnectorAuthorizationData.Hash(Secret), Token)).Value.Outcome);
    }

    [Fact]
    public async Task The_tables_hold_only_the_metadata_decision_D3B_2_allows()
    {
        // The runtime role's privileges are proven on fresh databases (ConnectorAuthorizationFreshDatabaseTests): this
        // shared database cannot show them, because the suite reruns the global bootstrap here on every run.
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using (var command = new NpgsqlCommand(
            """
            SELECT table_name || '.' || column_name || ':' || data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('connector_authorizations', 'connector_authorization_transactions')
            ORDER BY (table_name || '.' || column_name) COLLATE "C";
            """,
            admin))
        {
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                columns.Add(reader.GetString(0));
            }

            Assert.Equal(
                [
                    "connector_authorization_transactions.canonical_user_id:uuid",
                    "connector_authorization_transactions.connection_id:uuid",
                    "connector_authorization_transactions.consumed_at_utc:timestamp with time zone",
                    "connector_authorization_transactions.created_at_utc:timestamp with time zone",
                    "connector_authorization_transactions.expires_at_utc:timestamp with time zone",
                    "connector_authorization_transactions.purpose:character varying",
                    "connector_authorization_transactions.redirect_hash:bytea",
                    "connector_authorization_transactions.requested_scopes:character varying",
                    "connector_authorization_transactions.source_system:character varying",
                    "connector_authorization_transactions.state_hash:bytea",
                    "connector_authorization_transactions.tenant_id:uuid",
                    "connector_authorization_transactions.transaction_id:uuid",
                    "connector_authorizations.access_expires_at_utc:timestamp with time zone",
                    "connector_authorizations.authorized_at_utc:timestamp with time zone",
                    "connector_authorizations.canonical_user_id:uuid",
                    "connector_authorizations.connection_id:uuid",
                    "connector_authorizations.generation:bigint",
                    "connector_authorizations.granted_scopes:character varying",
                    "connector_authorizations.material_version:uuid",
                    "connector_authorizations.provider_account:character varying",
                    "connector_authorizations.provider_revocation_confirmed:boolean",
                    "connector_authorizations.provider_subject:character varying",
                    "connector_authorizations.reauthorization_required_at_utc:timestamp with time zone",
                    "connector_authorizations.refresh_claim_id:uuid",
                    "connector_authorizations.refresh_claimed_at_utc:timestamp with time zone",
                    "connector_authorizations.refreshed_at_utc:timestamp with time zone",
                    "connector_authorizations.revoked_at_utc:timestamp with time zone",
                    "connector_authorizations.state:character varying",
                    "connector_authorizations.tenant_id:uuid",
                ],
                columns);
        }
    }

    private async Task AgeTransactionAsync(Guid transactionId, TimeSpan age)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            UPDATE connector_authorization_transactions
            SET created_at_utc = created_at_utc - @age, expires_at_utc = expires_at_utc - @age
            WHERE transaction_id = @id;
            """,
            admin);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("id", transactionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }

    private async Task AgeClaimAsync(Guid connectionId, TimeSpan age)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            "UPDATE connector_authorizations SET refresh_claimed_at_utc = refresh_claimed_at_utc - @age WHERE connection_id = @id;",
            admin);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("id", connectionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }

    private async Task<(Guid Tenant, string Source)?> ResolveAsync(Guid connectionId)
    {
        await using var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT tenant_id, source_system FROM public.resolve_ingress_connection(@id);", runtime);
        command.Parameters.AddWithValue("id", connectionId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        return await reader.ReadAsync(Token) ? (reader.GetGuid(0), reader.GetString(1)) : null;
    }

    private async Task<string> SnapshotAsync(Guid connectionId)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT row_to_json(a)::text || coalesce((SELECT row_to_json(c)::text FROM connections c WHERE c.connection_id = a.connection_id), '')
            FROM connector_authorizations a WHERE a.connection_id = @id;
            """,
            admin);
        command.Parameters.AddWithValue("id", connectionId);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<long> CountAsync(string sql, TenantId tenant)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(sql, admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<T?> ScalarAsync<T>(string sql, Guid id)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(sql, admin);
        command.Parameters.AddWithValue("id", id);
        var value = await command.ExecuteScalarAsync(Token);
        return value is DBNull or null ? default : (T)value;
    }
}
