using System.Net;
using System.Text;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// Gate 3B end to end against the real database, as the non-owner runtime role, with fake monday endpoints and
/// in-memory secret storage: a connection is authorized, used, renewed, renewed after a rejection and disconnected,
/// and no token, code, verifier, state or client secret ever reaches the database. Two instances renewing the same
/// connection at once call monday once.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MondayConnectionLifecycleTests(DatabaseFixture fixture)
{
    private const string ClientSecret = "SENTINEL-client-credential-3b";
    private static readonly Uri Redirect = new("https://app.example.test/connectors/monday/callback");

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_connection_lives_its_whole_lifecycle_and_no_secret_ever_reaches_the_database()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        await using var instance = await Instance.StartAsync(fixture, tenant);
        var principal = Principal.Authenticated(user, new ExternalIdentity(IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        var sentinels = new List<string> { ClientSecret };

        // Authorize.
        var redirect = (await instance.Authorizer.StartAsync(principal, null, Token)).Value;
        var state = Uri.UnescapeDataString(redirect.AuthorizationUri.Query.Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);
        var verifier = instance.Secrets.Names.Single(name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));
        using (var stored = System.Text.Json.JsonDocument.Parse(instance.Secrets.Text(verifier)))
        {
            sentinels.Add(stored.RootElement.GetProperty("verifier").GetString()!);
        }
        var access1 = FakeMonday.Jwt(instance.Clock.UtcNow.AddHours(1), "SENTINEL-access-1");
        instance.Monday.ThenToken(FakeMonday.Tokens(access1, "SENTINEL-refresh-1"));
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        var identity = await instance.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(state, "SENTINEL-code-1", null, null), Token);
        Assert.True(identity.IsSuccess, identity.IsSuccess ? string.Empty : identity.Error.Code);
        Assert.Equal(new ConnectorIdentity(tenant, redirect.ConnectionId, ConnectorIdentityKind.DelegatedUser, "58201774"), identity.Value);
        Assert.DoesNotContain(verifier, instance.Secrets.Names);
        sentinels.AddRange([access1, "SENTINEL-refresh-1", "SENTINEL-code-1", state, state[33..]]);

        // Use, then renew within the margin, then renew after monday rejects the token.
        var context = new MondayRequestContext(tenant, redirect.ConnectionId, CorrelationId.New());
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Items);
        Assert.True((await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).IsSuccess);

        instance.Clock.UtcNow = instance.Clock.UtcNow.AddMinutes(56);
        var access2 = FakeMonday.Jwt(instance.Clock.UtcNow.AddHours(1), "SENTINEL-access-2");
        instance.Monday.ThenToken(FakeMonday.Tokens(access2, "SENTINEL-refresh-2"));
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Items);
        Assert.True((await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).IsSuccess);

        var access3 = FakeMonday.Jwt(instance.Clock.UtcNow.AddHours(1), "SENTINEL-access-3");
        instance.Monday.ThenApi(HttpStatusCode.Unauthorized, """{"errors":[{"message":"Unauthorized"}]}""");
        instance.Monday.ThenToken(FakeMonday.Tokens(access3, "SENTINEL-refresh-3"));
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Items);
        var renewed = await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token);
        Assert.True(renewed.IsSuccess);
        Assert.Equal(2, renewed.Attempts);
        sentinels.AddRange([access2, "SENTINEL-refresh-2", access3, "SENTINEL-refresh-3"]);

        // The rotation presented each refresh token once, the newest each time.
        var refreshes = instance.Monday.Requests.Where(request => request.Body.Contains("\"grant_type\":\"refresh_token\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, refreshes.Count);
        Assert.Contains("SENTINEL-refresh-1", refreshes[0].Body, StringComparison.Ordinal);
        Assert.Contains("SENTINEL-refresh-2", refreshes[1].Body, StringComparison.Ordinal);
        var authorization = (await instance.Store.Store.ReadAsync(redirect.ConnectionId, Token)).Value!;
        Assert.Equal(3L, authorization.Generation);
        Assert.Single(instance.Secrets.Names, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));

        // Disconnect: unusable first, then revoked, destroyed, and closed to ingress.
        var disconnected = await instance.Authorizer.DisconnectAsync(principal, redirect.ConnectionId, Token);
        Assert.True(disconnected.Value.ProviderRevocationConfirmed);
        Assert.Equal(2, instance.Monday.RevokeCalls);
        Assert.DoesNotContain(instance.Secrets.Names, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
        Assert.Equal(ConnectorErrors.CredentialRevoked, (await instance.Reader.ReadItemsByIdAsync(context, ["1111"], Token)).Failure.Error);
        Assert.Equal(0L, await ResolvedCountAsync(redirect.ConnectionId));

        // No column of any table holds any of the secrets that passed through. The first value is a positive control that
        // is stored, the delegating monday user's id, so the search is proven able to find what is there.
        var found = await FindAnywhereAsync(["58201774", .. sentinels]);
        Assert.Contains("connector_authorizations.provider_subject holds sentinel #1", found);
        Assert.DoesNotContain(found, finding => !finding.EndsWith(" #1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_instances_renewing_the_same_connection_at_once_call_monday_once()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var principal = Principal.Authenticated(user, new ExternalIdentity(IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        var secrets = new MemorySecrets();
        var monday = new FakeMonday();
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var first = await Instance.StartAsync(fixture, tenant, secrets, monday, clock);
        await using var second = await Instance.StartAsync(fixture, tenant, secrets, monday, clock);

        var redirect = (await first.Authorizer.StartAsync(principal, null, Token)).Value;
        var state = Uri.UnescapeDataString(redirect.AuthorizationUri.Query.Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);
        monday.ThenToken(FakeMonday.Tokens(FakeMonday.Jwt(clock.UtcNow.AddHours(1), "a1"), "refresh-1"));
        monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        Assert.True((await first.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(state, "code", null, null), Token)).IsSuccess);

        // Past expiry, so neither instance may keep using the old token: one renews, the other waits for it.
        clock.UtcNow = clock.UtcNow.AddMinutes(61);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monday.ThenToken(async token =>
        {
            await release.Task.WaitAsync(token);
            return FakeMonday.Json(HttpStatusCode.OK, FakeMonday.Tokens(FakeMonday.Jwt(clock.UtcNow.AddHours(1), "a2"), "refresh-2"));
        });
        var context = new MondayRequestContext(tenant, redirect.ConnectionId, CorrelationId.New());

        var a = Task.Run(() => first.Credentials.AcquireAsync(context, Token).AsTask(), Token);
        var b = Task.Run(() => second.Credentials.AcquireAsync(context, Token).AsTask(), Token);
        await Task.Delay(200, Token);
        release.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Equal(2, monday.TokenCalls);
        Assert.Equal(2L, (await first.Store.Store.ReadAsync(redirect.ConnectionId, Token)).Value!.Generation);
        Assert.Contains(results, result => result.Credential is not null);
        Assert.All(results, result => Assert.True(result.Credential is not null || result.Failure!.Error == ConnectorErrors.CredentialUnavailable));
    }

    [Fact]
    public async Task A_verifier_orphaned_by_a_completion_that_died_is_deleted_after_a_restart_and_never_reaches_the_database()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var principal = Principal.Authenticated(user, new ExternalIdentity(IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        var secrets = new MemorySecrets();
        string state;
        string verifierName;
        string verifier;
        await using (var first = await Instance.StartAsync(fixture, tenant, secrets))
        {
            var redirect = (await first.Authorizer.StartAsync(principal, null, Token)).Value;
            state = StateOf(redirect);
            verifierName = secrets.Names.Single(name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));
            using (var stored = System.Text.Json.JsonDocument.Parse(secrets.Text(verifierName)))
            {
                verifier = stored.RootElement.GetProperty("verifier").GetString()!;
            }

            // The completion consumed the transaction, then its process died before it deleted the verifier.
            var consumed = await first.Store.Store.ConsumeTransactionAsync(TransactionIdOf(state), user, ConnectorAuthorizationData.Hash(state[33..]), Token);
            Assert.Equal(ConnectorTransactionOutcome.Consumed, consumed.Value.Outcome);
        }

        // A new process with nothing in memory, sharing only the database and the secret storage. While a code could
        // still be exchanged its cleanup waits; once none can, it deletes and records the verifier, with no callback.
        await using var second = await Instance.StartAsync(fixture, tenant, secrets);
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await second.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        Assert.Contains(verifierName, secrets.Names);
        await AgeTransactionAsync(TransactionIdOf(state), TimeSpan.FromMinutes(10));
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await second.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        Assert.DoesNotContain(verifierName, secrets.Names);
        Assert.Equal("consumed, verifier deleted", await TransactionStateAsync(TransactionIdOf(state)));
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await second.Authorizer.CleanUpVerifiersAsync(Token)).Value);

        // Delivered again, the callback is refused and exchanges nothing; no verifier or state is in the database.
        var replayed = await second.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(state, "SENTINEL-code-orphan", null, null), Token);
        Assert.True(replayed.IsFailure);
        Assert.Equal(0, second.Monday.TokenCalls);
        Assert.Empty(await FindAnywhereAsync([verifier, state[33..], "SENTINEL-code-orphan"]));
    }

    [Fact]
    public async Task A_verifier_the_secret_store_failed_to_delete_stays_due_and_a_later_cleanup_deletes_it()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var principal = Principal.Authenticated(user, new ExternalIdentity(IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));
        await using var instance = await Instance.StartAsync(fixture, tenant);
        var redirect = (await instance.Authorizer.StartAsync(principal, null, Token)).Value;
        var state = StateOf(redirect);
        instance.Secrets.DeleteOverride = name => name.Value.StartsWith("wce-monday-pkce-", StringComparison.Ordinal) ? MondaySecretStoreStatus.Unavailable : null;
        instance.Monday.ThenToken(FakeMonday.Tokens(FakeMonday.Jwt(instance.Clock.UtcNow.AddHours(1), "a1"), "refresh-1"));
        instance.Monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);

        // The authorization completes; the storage refuses to delete the verifier, so the transaction stays due.
        Assert.True((await instance.Authorizer.CompleteAsync(principal, new MondayAuthorizationCallback(state, "code", null, null), Token)).IsSuccess);
        var before = (await instance.Store.Store.ReadAsync(redirect.ConnectionId, Token)).Value!;
        Assert.Equal("consumed, verifier due", await TransactionStateAsync(TransactionIdOf(state)));

        await AgeTransactionAsync(TransactionIdOf(state), TimeSpan.FromMinutes(10));
        Assert.Equal(new MondayVerifierCleanup(0, 1), (await instance.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        Assert.Equal("consumed, verifier due", await TransactionStateAsync(TransactionIdOf(state)));

        // The storage recovers: the next cleanup deletes it and records it, and the authorization is exactly as it was.
        instance.Secrets.DeleteOverride = _ => null;
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await instance.Authorizer.CleanUpVerifiersAsync(Token)).Value);
        Assert.DoesNotContain(instance.Secrets.Names, name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));
        Assert.Equal("consumed, verifier deleted", await TransactionStateAsync(TransactionIdOf(state)));
        Assert.Equal(before, (await instance.Store.Store.ReadAsync(redirect.ConnectionId, Token)).Value);
    }

    private static string StateOf(MondayAuthorizationRedirect redirect) =>
        Uri.UnescapeDataString(redirect.AuthorizationUri.Query.Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);

    private static Guid TransactionIdOf(string state) => Guid.ParseExact(state[..32], "N");

    // Ages a transaction as the passing of time would: its creation, expiry and consumption all move back.
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

    private async Task<long> ResolvedCountAsync(Guid connectionId)
    {
        await using var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM public.resolve_ingress_connection(@id);", runtime);
        command.Parameters.AddWithValue("id", connectionId);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    // Every text-like and binary column of every table in the schema, searched for every sentinel.
    private async Task<List<string>> FindAnywhereAsync(IReadOnlyList<string> sentinels)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        var columns = new List<(string Table, string Column, string Type)>();
        await using (var list = new NpgsqlCommand(
            """
            SELECT c.table_name, c.column_name, c.data_type
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'public' AND c.data_type IN ('text', 'character varying', 'jsonb', 'json', 'bytea');
            """,
            admin))
        await using (var reader = await list.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.Contains(columns, column => column.Table == "connector_authorization_transactions" && column.Column == "state_hash");
        var found = new List<string>();
        foreach (var (table, column, type) in columns)
        {
            var predicate = type == "bytea"
                ? $"position(convert_to(v.value, 'UTF8') in t.\"{column}\") > 0"
                : $"position(v.value in t.\"{column}\"::text) > 0";
            await using var search = new NpgsqlCommand(
                $"SELECT DISTINCT v.ordinality FROM public.\"{table}\" AS t, unnest(@values) WITH ORDINALITY AS v(value, ordinality) WHERE {predicate};",
                admin);
            search.Parameters.AddWithValue("values", sentinels.ToArray());
            await using var reader = await search.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                // Names the sentinel by its position only, so a failure never prints a secret.
                found.Add($"{table}.{column} holds sentinel #{reader.GetInt64(0)}");
            }
        }

        return found;
    }

    /// <summary>One application instance: its own database context, sharing the database, the secret store and monday.</summary>
    private sealed class Instance : IAsyncDisposable
    {
        private Instance(ConnectorAuthorizationData.ScopedStore store, MemorySecrets secrets, FakeMonday monday, MovingClock clock)
        {
            Store = store;
            Secrets = secrets;
            Monday = monday;
            Clock = clock;
            var options = new MondayOAuthOptions("wce-test-client", Redirect);
            var http = new HttpClient(monday);
            Credentials = new MondayConnectionCredentialSource(store.Store, secrets, options, http, clock, new NoWait());
            Reader = new MondayReadClient(http, Credentials, clock, new NoWait(), () => 0.5);
            Authorizer = new MondayConnectionAuthorizer(store.Store, secrets, options, http, Reader, store.Accessor, clock);
        }

        internal ConnectorAuthorizationData.ScopedStore Store { get; }

        internal MemorySecrets Secrets { get; }

        internal FakeMonday Monday { get; }

        internal MovingClock Clock { get; }

        internal MondayConnectionCredentialSource Credentials { get; }

        internal MondayReadClient Reader { get; }

        internal MondayConnectionAuthorizer Authorizer { get; }

        internal static Task<Instance> StartAsync(
            DatabaseFixture fixture,
            TenantId tenant,
            MemorySecrets? secrets = null,
            FakeMonday? monday = null,
            MovingClock? clock = null)
        {
            secrets ??= new MemorySecrets();
            secrets.Put(MondaySecretName.ClientSecret, ClientSecret);
            return Task.FromResult(new Instance(ConnectorAuthorizationData.Store(fixture, tenant), secrets, monday ?? new FakeMonday(), clock ?? new MovingClock(DateTimeOffset.UtcNow)));
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}
