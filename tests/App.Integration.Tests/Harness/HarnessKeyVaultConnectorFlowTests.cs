using System.Buffers.Text;
using System.Net;
using System.Text;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Secrets;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline Key Vault adapter (S-1 to S-5): the connector's own flows, unchanged, over the adapter and a fake
/// vault that keeps deleted names reserved, against the real database as the runtime role: authorization, renewal,
/// adoption of an abandoned renewal, verifier cleanup and disconnect. Also the size of the token material the connector
/// actually writes, against the adapter's bound. monday and the vault are fakes; nothing here is provider evidence.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessKeyVaultConnectorFlowTests(DatabaseFixture fixture)
{
    private const string ClientSecret = "synthetic-client-credential-kv";
    private static readonly Uri Redirect = new("https://app.example.test/connectors/monday/callback");

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Authorization_renewal_and_disconnect_run_over_the_adapter_and_a_fake_vault()
    {
        var (tenant, principal) = await TenantAsync();
        var vault = OwnerPlacedVault();
        await using var instance = Instance.Start(fixture, tenant, vault);

        // Authorize: the verifier is written marked, read back exactly, and deleted once the code is exchanged.
        var redirect = (await instance.Authorizer.StartAsync(principal, null, Token)).Value;
        var verifier = Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));
        Assert.Equal(KeyVaultSecretCodec.ContentType, vault.Current(verifier).ContentType);
        instance.Monday.ThenToken(FakeMonday.Tokens(FakeMonday.Jwt(instance.Clock.UtcNow.AddHours(1), "a1"), "refresh-1"));
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        var identity = await instance.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(StateOf(redirect), "code-1", null, null), Token);
        Assert.True(identity.IsSuccess, identity.IsSuccess ? string.Empty : identity.Error.Code);
        Assert.False(vault.IsActive(verifier));
        Assert.True(vault.IsDeleted(verifier));
        var first = Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
        Assert.Equal(KeyVaultSecretCodec.ContentType, vault.Current(first).ContentType);

        // Use, then renew within the margin: a new version is written under a new name, and the old one is deleted.
        var context = new MondayRequestContext(tenant, redirect.ConnectionId, CorrelationId.New());
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Items);
        Assert.True((await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).IsSuccess);
        instance.Clock.UtcNow = instance.Clock.UtcNow.AddMinutes(56);
        instance.Monday.ThenToken(FakeMonday.Tokens(FakeMonday.Jwt(instance.Clock.UtcNow.AddHours(1), "a2"), "refresh-2"));
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Items);
        Assert.True((await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).IsSuccess);
        var second = Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
        Assert.NotEqual(first, second);
        Assert.True(vault.IsDeleted(first));
        Assert.Contains("refresh-1", instance.Monday.Requests.Single(request => request.Body.Contains("refresh_token", StringComparison.Ordinal)).Body, StringComparison.Ordinal);

        // Disconnect: revoked at the fake provider, then deleted from the vault's active namespace.
        var disconnected = await instance.Authorizer.DisconnectAsync(principal, redirect.ConnectionId, Token);
        Assert.True(disconnected.Value.ProviderRevocationConfirmed);
        Assert.False(vault.IsActive(second));
        Assert.True(vault.IsDeleted(second));
        Assert.Equal(ConnectorErrors.CredentialRevoked, (await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).Failure.Error);

        // The connector never rewrote a deleted name, so the reserved names never refused a write; the client secret is
        // the owner's plain-text placement, read and never written.
        AssertNoConflictAndNoClientSecretWrite(vault);
    }

    [Fact]
    public async Task An_abandoned_renewal_is_adopted_from_the_vault_by_the_next_instance()
    {
        var (tenant, principal) = await TenantAsync();
        var vault = OwnerPlacedVault();
        var monday = new FakeMonday();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var first = Instance.Start(fixture, tenant, vault, monday, clock);
        var redirect = (await first.Authorizer.StartAsync(principal, null, Token)).Value;
        monday.ThenToken(FakeMonday.Tokens(FakeMonday.Jwt(clock.UtcNow.AddHours(1), "a1"), "refresh-1"));
        monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        Assert.True((await first.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(StateOf(redirect), "code", null, null), Token)).IsSuccess);
        var original = Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));

        // A second instance renews: monday rotates the pair and the new pair is written to the vault under the claim,
        // and then that instance stops before it completes the renewal.
        clock.UtcNow = clock.UtcNow.AddMinutes(61);
        var renewedAccess = FakeMonday.Jwt(clock.UtcNow.AddHours(1), "a2");
        monday.ThenToken(FakeMonday.Tokens(renewedAccess, "refresh-2"));
        var context = new MondayRequestContext(tenant, redirect.ConnectionId, CorrelationId.New());
        await using (var stopping = Instance.Start(fixture, tenant, vault, monday, clock, stopsBeforeCompletingRenewal: true))
        {
            await Assert.ThrowsAsync<RenewalStoppedException>(() => stopping.Credentials.AcquireAsync(context, Token).AsTask());
        }

        var claimed = vault.ActiveNames.Where(name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, claimed.Count);
        Assert.Contains(original, claimed);
        await AgeRenewalClaimAsync(redirect.ConnectionId, TimeSpan.FromMinutes(5));

        // A third instance finds the claim abandoned, reads the pair from the vault under the claim's name and adopts
        // it: monday is not called again, and the superseded version is deleted.
        await using var third = Instance.Start(fixture, tenant, vault, monday, clock);
        var acquired = await third.Credentials.AcquireAsync(context, Token);
        Assert.NotNull(acquired.Credential);
        Assert.Equal(2, monday.TokenCalls);
        var authorization = (await third.Store.Store.ReadAsync(redirect.ConnectionId, Token)).Value!;
        Assert.Equal(2L, authorization.Generation);
        Assert.Null(authorization.RefreshClaim);
        var adopted = Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
        Assert.NotEqual(original, adopted);
        Assert.True(vault.IsDeleted(original));

        // The adopted pair is the one monday issued: the next call presents it.
        monday.ThenApi(HttpStatusCode.OK, FakeMonday.Items);
        Assert.True((await third.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).IsSuccess);
        Assert.Equal(renewedAccess, monday.Requests.Last(request => request.Uri == MondayEndpoints.Api).Authorization);
        AssertNoConflictAndNoClientSecretWrite(vault);
    }

    [Fact]
    public async Task Verifier_cleanup_runs_over_the_adapter_and_a_refused_deletion_stays_due_until_a_later_cleanup()
    {
        var (tenant, principal) = await TenantAsync();
        var vault = OwnerPlacedVault();
        await using var instance = Instance.Start(fixture, tenant, vault);
        var redirect = (await instance.Authorizer.StartAsync(principal, null, Token)).Value;
        var state = StateOf(redirect);
        var verifier = Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));

        // The completion consumed the transaction, then its process stopped before it deleted the verifier.
        var transactionId = Guid.ParseExact(state[..32], "N");
        var consumed = await instance.Store.Store.ConsumeTransactionAsync(transactionId, principal.UserId, ConnectorAuthorizationData.Hash(state[33..]), Token);
        Assert.Equal(ConnectorTransactionOutcome.Consumed, consumed.Value.Outcome);
        await AgeTransactionAsync(transactionId, TimeSpan.FromMinutes(10));

        // The vault refuses every attempt of the deletion: the cleanup does not confirm it, and it stays due.
        for (var attempt = 0; attempt < KeyVaultSecretStore.MaxAttempts; attempt++)
        {
            vault.Then(FakeVault.Status(HttpStatusCode.ServiceUnavailable));
        }

        Assert.Equal(new MondayVerifierCleanup(0, 1), (await instance.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        Assert.True(vault.IsActive(verifier));
        Assert.Equal("consumed, verifier due", await TransactionStateAsync(transactionId));

        // The vault recovers: the next cleanup deletes it, which means it left the active namespace, and records it.
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await instance.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        Assert.False(vault.IsActive(verifier));
        Assert.True(vault.IsDeleted(verifier));
        Assert.Equal("consumed, verifier deleted", await TransactionStateAsync(transactionId));
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await instance.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        AssertNoConflictAndNoClientSecretWrite(vault);
    }

    [Fact]
    public async Task Token_material_the_connector_writes_fits_the_bound_when_little_is_escaped_and_fails_closed_above_it()
    {
        // Low escaping: both tokens at the connector's maximum of 8,192 characters, made of characters that JSON never
        // escapes. The connector's own MondayTokenMaterial is measured as it reaches the store.
        var (tenant, principal) = await TenantAsync();
        var vault = OwnerPlacedVault();
        var measured = new MeasuringStore(Adapter(vault));
        await using (var low = Instance.Start(fixture, tenant, vault, secrets: measured))
        {
            var redirect = (await low.Authorizer.StartAsync(principal, null, Token)).Value;
            low.Monday.ThenToken(FakeMonday.Tokens(LongAccessToken(low.Clock.UtcNow.AddHours(1)), new string('r', MondayAccessCredential.MaxTokenLength)));
            low.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
            var identity = await low.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(StateOf(redirect), "code", null, null), Token);
            Assert.True(identity.IsSuccess, identity.IsSuccess ? string.Empty : identity.Error.Code);
        }

        var lowSize = Assert.Single(measured.Writes, write => write.Name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal)).Length;
        TestContext.Current.TestOutputHelper?.WriteLine($"token material, low escaping: {lowSize} bytes");
        Assert.InRange(lowSize, 2 * MondayAccessCredential.MaxTokenLength, 16_887);
        Assert.True(lowSize <= KeyVaultSecretCodec.MaxValueBytes);
        Assert.Single(vault.ActiveNames, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));

        // High escaping: a refresh token of the same length made of a character the default JSON encoder escapes to six
        // bytes. The material is above the bound: the adapter refuses it before any request, and the connector fails
        // closed, revoking the pair and keeping nothing.
        var (otherTenant, otherPrincipal) = await TenantAsync();
        var otherVault = OwnerPlacedVault();
        var otherMeasured = new MeasuringStore(Adapter(otherVault));
        await using var high = Instance.Start(fixture, otherTenant, otherVault, secrets: otherMeasured);
        var otherRedirect = (await high.Authorizer.StartAsync(otherPrincipal, null, Token)).Value;
        high.Monday.ThenToken(FakeMonday.Tokens(LongAccessToken(high.Clock.UtcNow.AddHours(1)), new string('<', MondayAccessCredential.MaxTokenLength)));
        high.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        var refused = await high.Authorizer.CompleteAsync(otherPrincipal, new MondayAuthorizationCallback(StateOf(otherRedirect), "code", null, null), Token);
        Assert.True(refused.IsFailure);
        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, refused.Error);

        var highSize = Assert.Single(otherMeasured.Writes, write => write.Name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal)).Length;
        TestContext.Current.TestOutputHelper?.WriteLine($"token material, high escaping: {highSize} bytes");
        Assert.InRange(highSize, KeyVaultSecretCodec.MaxValueBytes + 1, 98_737);
        Assert.DoesNotContain(otherVault.Exchanges, exchange => exchange.Method == HttpMethod.Put && exchange.Uri.AbsolutePath.StartsWith("/secrets/wce-monday-tokens-", StringComparison.Ordinal));
        Assert.DoesNotContain(otherVault.ActiveNames, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
        Assert.True(high.Monday.RevokeCalls >= 1);
        Assert.Null((await high.Store.Store.ReadAsync(otherRedirect.ConnectionId, Token)).Value);
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private static FakeVault OwnerPlacedVault()
    {
        var vault = new FakeVault();
        vault.Place(MondaySecretName.ClientSecret, ClientSecret);
        return vault;
    }

    private static KeyVaultSecretStore Adapter(FakeVault vault) =>
        new(FakeVault.Origin, vault, new FixedTokenSource("synthetic-vault-access-token"), new RecordingDelay(), TimeProvider.System);

    private static void AssertNoConflictAndNoClientSecretWrite(FakeVault vault)
    {
        Assert.NotEmpty(vault.Exchanges);
        Assert.DoesNotContain(vault.Exchanges, exchange => exchange.Method == HttpMethod.Put && exchange.Uri.AbsolutePath.Contains("client-secret", StringComparison.Ordinal));
        Assert.Equal(0, vault.Conflicts);
        Assert.Equal(ClientSecret, vault.Current(MondaySecretName.ClientSecret.Value).Value);
        Assert.Null(vault.Current(MondaySecretName.ClientSecret.Value).ContentType);
    }

    // An access token of the connector's maximum length: a JWT whose expiry the connector reads, padded in its third part.
    private static string LongAccessToken(DateTimeOffset expiresAt)
    {
        var head = Base64Url.EncodeToString("""{"alg":"none"}"""u8) + "."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"exp\":" + expiresAt.ToUnixTimeSeconds() + "}")) + ".";
        return head + new string('s', MondayAccessCredential.MaxTokenLength - head.Length);
    }

    private async Task<(TenantId Tenant, Principal Principal)> TenantAsync()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        return (tenant, Principal.Authenticated(user, new ExternalIdentity(IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N"))));
    }

    private static string StateOf(MondayAuthorizationRedirect redirect) =>
        Uri.UnescapeDataString(redirect.AuthorizationUri.Query.Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);

    private async Task AgeTransactionAsync(Guid transactionId, TimeSpan age)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            UPDATE connector_authorization_transactions
            SET created_at_utc = created_at_utc - @age, expires_at_utc = expires_at_utc - @age, consumed_at_utc = consumed_at_utc - @age
            WHERE transaction_id = @id;
            """,
            admin);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("id", transactionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }

    // Ages a renewal's claim as the passing of time would, so that the next instance judges it abandoned.
    private async Task AgeRenewalClaimAsync(Guid connectionId, TimeSpan age)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            "UPDATE connector_authorizations SET refresh_claimed_at_utc = refresh_claimed_at_utc - @age WHERE connection_id = @id AND refresh_claim_id IS NOT NULL;",
            admin);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("id", connectionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }

    private async Task<string> TransactionStateAsync(Guid transactionId)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT CASE WHEN consumed_at_utc IS NULL THEN 'open' ELSE 'consumed' END || ', verifier '
                   || CASE WHEN verifier_deleted_at_utc IS NULL THEN 'due' ELSE 'deleted' END
            FROM connector_authorization_transactions WHERE transaction_id = @id;
            """,
            admin);
        command.Parameters.AddWithValue("id", transactionId);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>A renewal that stopped after it wrote its new pair and before it completed: its process went away.</summary>
    private sealed class RenewalStoppedException() : Exception("The renewal stopped before it completed.");

    /// <summary>Forwards every call to the real store, except that completing a renewal stops the process.</summary>
    private sealed class StoppingStore(IConnectorAuthorizationStore inner) : IConnectorAuthorizationStore
    {
        public Task<Result<bool>> OpenTransactionAsync(ConnectorAuthorizationTransaction transaction, TimeSpan lifetime, CancellationToken cancellationToken) =>
            inner.OpenTransactionAsync(transaction, lifetime, cancellationToken);

        public Task<Result<ConnectorTransactionConsumption>> ConsumeTransactionAsync(Guid transactionId, UserId canonicalUser, ReadOnlyMemory<byte> stateHash, CancellationToken cancellationToken) =>
            inner.ConsumeTransactionAsync(transactionId, canonicalUser, stateHash, cancellationToken);

        public Task<Result<IReadOnlyList<Guid>>> CloseExpiredTransactionsAsync(int limit, CancellationToken cancellationToken) =>
            inner.CloseExpiredTransactionsAsync(limit, cancellationToken);

        public Task<Result<IReadOnlyList<Guid>>> ListVerifierCleanupAsync(int limit, CancellationToken cancellationToken) =>
            inner.ListVerifierCleanupAsync(limit, cancellationToken);

        public Task<Result<bool>> RecordVerifierDeletedAsync(Guid transactionId, CancellationToken cancellationToken) =>
            inner.RecordVerifierDeletedAsync(transactionId, cancellationToken);

        public Task<Result<ConnectorAuthorization?>> ReadAsync(Guid connectionId, CancellationToken cancellationToken) =>
            inner.ReadAsync(connectionId, cancellationToken);

        public Task<Result<bool>> ActivateAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken) =>
            inner.ActivateAsync(grant, cancellationToken);

        public Task<Result<bool>> ReauthorizeAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken) =>
            inner.ReauthorizeAsync(grant, cancellationToken);

        public Task<Result<bool>> ClaimRefreshAsync(Guid connectionId, long generation, Guid claim, CancellationToken cancellationToken) =>
            inner.ClaimRefreshAsync(connectionId, generation, claim, cancellationToken);

        public Task<Result<bool>> CompleteRefreshAsync(Guid connectionId, long generation, Guid claim, DateTimeOffset accessExpiresAt, string grantedScopes, CancellationToken cancellationToken) =>
            throw new RenewalStoppedException();

        public Task<Result<bool>> ReleaseRefreshAsync(Guid connectionId, Guid claim, CancellationToken cancellationToken) =>
            inner.ReleaseRefreshAsync(connectionId, claim, cancellationToken);

        public Task<Result<bool>> RequireReauthorizationAsync(Guid connectionId, long generation, Guid? claim, CancellationToken cancellationToken) =>
            inner.RequireReauthorizationAsync(connectionId, generation, claim, cancellationToken);

        public Task<Result<ConnectorDisconnection>> DisconnectAsync(Guid connectionId, UserId requestedBy, CancellationToken cancellationToken) =>
            inner.DisconnectAsync(connectionId, requestedBy, cancellationToken);

        public Task<Result<bool>> RecordProviderRevocationAsync(Guid connectionId, bool confirmed, CancellationToken cancellationToken) =>
            inner.RecordProviderRevocationAsync(connectionId, confirmed, cancellationToken);
    }

    /// <summary>Records the size of every value the connector hands to the store, then forwards it.</summary>
    private sealed class MeasuringStore(IMondaySecretStore inner) : IMondaySecretStore
    {
        internal List<(string Name, int Length)> Writes { get; } = [];

        public ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            Writes.Add((name.Value, value.Length));
            return inner.SetAsync(name, value, cancellationToken);
        }

        public ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken) => inner.GetAsync(name, cancellationToken);

        public ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken) => inner.DeleteAsync(name, cancellationToken);
    }

    /// <summary>One application instance over the shared database, the fake vault and the fake monday.</summary>
    private sealed class Instance : IAsyncDisposable
    {
        private Instance(ConnectorAuthorizationData.ScopedStore store, IConnectorAuthorizationStore authorizations, IMondaySecretStore secrets, FakeMonday monday, MovingClock clock)
        {
            Store = store;
            Monday = monday;
            Clock = clock;
            var options = new MondayOAuthOptions("wce-test-client", Redirect);
            var http = new HttpClient(monday);
            Credentials = new MondayConnectionCredentialSource(authorizations, secrets, options, http, clock, new NoWait());
            Reader = new MondayReadClient(http, Credentials, clock, new NoWait(), () => 0.5);
            Authorizer = new MondayConnectionAuthorizer(authorizations, secrets, options, http, Reader, store.Accessor, clock);
        }

        internal ConnectorAuthorizationData.ScopedStore Store { get; }

        internal FakeMonday Monday { get; }

        internal MovingClock Clock { get; }

        internal MondayConnectionCredentialSource Credentials { get; }

        internal MondayReadClient Reader { get; }

        internal MondayConnectionAuthorizer Authorizer { get; }

        internal static Instance Start(
            DatabaseFixture fixture,
            TenantId tenant,
            FakeVault vault,
            FakeMonday? monday = null,
            MovingClock? clock = null,
            bool stopsBeforeCompletingRenewal = false,
            IMondaySecretStore? secrets = null)
        {
            var store = ConnectorAuthorizationData.Store(fixture, tenant);
            IConnectorAuthorizationStore authorizations = stopsBeforeCompletingRenewal ? new StoppingStore(store.Store) : store.Store;
            return new Instance(store, authorizations, secrets ?? Adapter(vault), monday ?? new FakeMonday(), clock ?? new MovingClock(DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}
