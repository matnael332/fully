using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3B: starting and completing a monday OAuth 2.1 authorization. The server generates the state and the PKCE
/// verifier, binds the transaction to its tenant, user, connection, redirect address and scopes, validates the callback
/// in the documented order before anything is exchanged, exchanges the code exactly once, and refuses and revokes any
/// grant it cannot keep.
/// </summary>
public sealed class MondayConnectionAuthorizerTests
{
    private static readonly Regex StateForm = new("^[0-9a-f]{32}\\.[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_sends_the_browser_to_monday_with_server_chosen_parameters_only()
    {
        var harness = new OAuthHarness();

        var redirect = await harness.StartAsync();

        var address = redirect.AuthorizationUri;
        Assert.Equal("https://auth.monday.com/oauth2/authorize", address.GetLeftPart(UriPartial.Path));
        var query = OAuthHarness.Query(address);
        Assert.Equal(
            ["client_id", "code_challenge", "code_challenge_method", "redirect_uri", "response_type", "scope", "state"],
            query.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(OAuthHarness.ClientId, query["client_id"]);
        Assert.Equal(OAuthHarness.Redirect.AbsoluteUri, query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("boards:read me:read", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Matches(StateForm, query["state"]);
        Assert.Equal(MondayPkce.ChallengeOf(StoredVerifier(harness, query["state"])), query["code_challenge"]);

        // A new connection that exists nowhere yet.
        Assert.NotEqual(Guid.Empty, redirect.ConnectionId);
        Assert.Null((await harness.Store.ReadAsync(redirect.ConnectionId, Cancel)).Value);
        Assert.DoesNotContain(query["state"], redirect.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_binds_the_transaction_and_stores_only_the_hashes_of_the_state_and_redirect()
    {
        var harness = new OAuthHarness();

        var redirect = await harness.StartAsync();

        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var stored = Assert.Single(harness.Database.Transactions.Values);
        var transaction = stored.Transaction;
        Assert.Equal(harness.Tenant, stored.Tenant);
        Assert.Equal(harness.User, transaction.CanonicalUser);
        Assert.Equal(redirect.ConnectionId, transaction.ConnectionId);
        Assert.Equal(ConnectorAuthorizationPurpose.Connect, transaction.Purpose);
        Assert.Equal("monday", transaction.SourceSystem);
        Assert.Equal("boards:read me:read", transaction.RequestedScopes);
        Assert.Equal(Guid.ParseExact(state[..32], "N"), transaction.TransactionId);
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(state[33..])), transaction.StateHash.ToArray());
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(OAuthHarness.Redirect.AbsoluteUri)), transaction.RedirectHash.ToArray());
        Assert.Equal(harness.Clock.UtcNow + TimeSpan.FromMinutes(10), stored.ExpiresAt);
        Assert.DoesNotContain(state, transaction.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_start_has_its_own_random_state_and_43_character_verifier_that_only_secret_storage_holds()
    {
        var harness = new OAuthHarness();
        var states = new HashSet<string>(StringComparer.Ordinal);
        var verifiers = new HashSet<string>(StringComparer.Ordinal);

        for (var start = 0; start < 50; start++)
        {
            var redirect = await harness.StartAsync();
            var query = OAuthHarness.Query(redirect.AuthorizationUri);
            var verifier = StoredVerifier(harness, query["state"]);

            Assert.True(MondayPkce.IsVerifier(verifier));
            Assert.Equal(MondayPkce.VerifierLength, verifier.Length);
            Assert.Equal(32, Base64Url.DecodeFromChars(verifier).Length);
            Assert.DoesNotContain(verifier, redirect.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
            Assert.True(states.Add(query["state"][33..]));
            Assert.True(verifiers.Add(verifier));
        }

        // 32 CSPRNG bytes each: across 50 starts every byte value shows up, which a constant or a counter would not do.
        var bytes = verifiers.SelectMany(verifier => Base64Url.DecodeFromChars(verifier)).ToArray();
        Assert.True(bytes.Distinct().Count() > 200, "The verifiers do not look random.");
    }

    [Fact]
    public async Task Start_fails_closed_without_a_trusted_tenant_or_an_authenticated_active_user()
    {
        var harness = new OAuthHarness();
        var unresolved = new MondayConnectionAuthorizer(
            harness.Store, harness.Secrets, harness.Options, harness.Http, harness.Reader,
            new FixedTenantContextAccessor(TenantContext.Unresolved), harness.Clock);

        Assert.Equal(ConnectorAuthorizationErrors.TenantUnresolved, (await unresolved.StartAsync(harness.Principal, null, Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.UserNotEligible, (await harness.Authorizer.StartAsync(Principal.Anonymous, null, Cancel)).Error);

        var inactive = harness.AddUser(harness.Tenant, active: false);
        var foreign = harness.AddUser(TenantId.New());
        foreach (var user in new[] { inactive, foreign, UserId.New() })
        {
            Assert.Equal(
                ConnectorAuthorizationErrors.UserNotEligible,
                (await harness.Authorizer.StartAsync(OAuthHarness.PrincipalOf(user), null, Cancel)).Error);
        }

        // Nothing was opened, and no verifier was kept.
        Assert.Empty(harness.Database.Transactions);
        Assert.DoesNotContain(harness.Secrets.Names, name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_fails_closed_when_the_transaction_or_the_verifier_cannot_be_stored()
    {
        var harness = new OAuthHarness();
        harness.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.OpenTransactionAsync);
        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, (await harness.Authorizer.StartAsync(harness.Principal, null, Cancel)).Error);
        Assert.DoesNotContain(harness.Secrets.Names, name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));

        harness.Store.FailBefore = _ => false;
        harness.Secrets.SetOverride = _ => MondaySecretStoreStatus.Unavailable;
        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, (await harness.Authorizer.StartAsync(harness.Principal, null, Cancel)).Error);
    }

    [Fact]
    public async Task Starting_closes_expired_transactions_and_deletes_their_verifiers()
    {
        var harness = new OAuthHarness();
        var abandoned = await harness.StartAsync();
        var abandonedState = OAuthHarness.Query(abandoned.AuthorizationUri)["state"];
        var abandonedName = VerifierName(harness, abandonedState);
        Assert.NotNull(harness.Secrets.Peek(abandonedName));

        harness.Database.Now += TimeSpan.FromMinutes(11);
        await harness.StartAsync();

        Assert.Null(harness.Secrets.Peek(abandonedName));
        Assert.NotNull(harness.Database.Transactions[Guid.ParseExact(abandonedState[..32], "N")].ConsumedAt);
        Assert.NotNull(harness.Database.Transactions[Guid.ParseExact(abandonedState[..32], "N")].VerifierDeletedAt);
    }

    [Fact]
    public async Task A_verifier_left_by_a_completion_that_died_is_removed_once_its_code_can_no_longer_be_exchanged()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var name = VerifierName(harness, state);

        // The winning completion consumed the transaction, then its process died before it removed the verifier.
        harness.Database.Transactions[Guid.ParseExact(state[..32], "N")].ConsumedAt = harness.Database.Now;

        // While a code issued for it could still be exchanged, a cleanup leaves the verifier to that completion.
        harness.Database.Now += TimeSpan.FromMinutes(9);
        await harness.StartAsync();
        Assert.NotNull(harness.Secrets.Peek(name));

        // Once no code for it can be exchanged any more, the next cleanup removes it, with no callback and no retry.
        harness.Database.Now += TimeSpan.FromMinutes(2);
        await harness.StartAsync();
        Assert.Null(harness.Secrets.Peek(name));
    }

    [Fact]
    public async Task A_successful_completion_deletes_its_verifier_and_records_it_so_no_cleanup_is_due()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        harness.ScriptExchange();

        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);

        var stored = Transaction(harness, state);
        Assert.Null(harness.Secrets.Peek(VerifierName(harness, state)));
        Assert.NotNull(stored.ConsumedAt);
        Assert.NotNull(stored.VerifierDeletedAt);
        harness.Database.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
    }

    [Fact]
    public async Task After_a_restart_a_cleanup_with_no_callback_deletes_an_orphaned_verifier_once_and_keeps_the_transaction_consumed()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var name = VerifierName(harness, state);
        var stored = Transaction(harness, state);

        // The winning completion consumed the transaction, then its process died before it removed the verifier.
        stored.ConsumedAt = harness.Database.Now;

        // A new process, with its own authorizer and nothing in memory, waits while a code could still be exchanged.
        var restarted = Restarted(harness);
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await restarted.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.NotNull(harness.Secrets.Peek(name));

        // Then it deletes the verifier and records it; the transaction stays consumed.
        harness.Database.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await restarted.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.Null(harness.Secrets.Peek(name));
        Assert.NotNull(stored.VerifierDeletedAt);
        Assert.NotNull(stored.ConsumedAt);

        // Running it again is safe and finds nothing to do.
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await restarted.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.Equal(new MondayVerifierCleanup(0, 0), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
    }

    [Fact]
    public async Task A_cleanup_while_the_winner_is_completing_leaves_the_winners_verifier()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var verifierName = VerifierName(harness, state);
        var verifier = StoredVerifier(harness, state);
        harness.ScriptExchange();

        // The winner consumed the transaction and is held just before it reads its verifier.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        harness.Secrets.GetOverride = name =>
        {
            if (name.Value == verifierName && reading.TrySetResult())
            {
                release.Wait(TimeSpan.FromSeconds(30), Cancel);
            }

            return null;
        };
        var winner = Task.Run(() => harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel), Cancel);
        await reading.Task.WaitAsync(Cancel);

        try
        {
            // Another process's cleanup now, and a start's: neither touches the verifier the winner still needs.
            harness.Database.Now += TimeSpan.FromMinutes(9);
            Assert.Equal(new MondayVerifierCleanup(0, 0), (await Restarted(harness).CleanUpVerifiersAsync(Cancel)).Value);
            await harness.StartAsync();
            Assert.Equal(verifier, StoredVerifier(harness, state));
        }
        finally
        {
            release.Set();
        }

        // The winner completes with it, then deletes it and records the deletion.
        Assert.True((await winner).IsSuccess);
        Assert.Null(harness.Secrets.Peek(verifierName));
        Assert.NotNull(Transaction(harness, state).VerifierDeletedAt);
        Assert.Single(harness.Server.TokenRequests);
    }

    [Fact]
    public async Task A_verifier_whose_deletion_fails_stays_due_and_a_later_cleanup_deletes_it()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var name = VerifierName(harness, state);
        harness.ScriptExchange();
        harness.Secrets.DeleteOverride = candidate => candidate.Value == name ? MondaySecretStoreStatus.Unavailable : null;

        // The completion succeeds; the storage refuses to delete its verifier, so nothing records a deletion.
        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);
        var stored = Transaction(harness, state);
        Assert.NotNull(harness.Secrets.Peek(name));
        Assert.Null(stored.VerifierDeletedAt);

        // Once no code can be exchanged, a cleanup tries again; the storage still fails, and the verifier stays due.
        harness.Database.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(new MondayVerifierCleanup(0, 1), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.NotNull(harness.Secrets.Peek(name));
        Assert.Null(stored.VerifierDeletedAt);

        // A storage that raises instead of answering has not deleted anything either.
        harness.Secrets.DeleteOverride = candidate => candidate.Value == name ? throw new HttpRequestException() : (MondaySecretStoreStatus?)null;
        Assert.Equal(new MondayVerifierCleanup(0, 1), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.NotNull(harness.Secrets.Peek(name));
        Assert.Null(stored.VerifierDeletedAt);

        // The storage recovers: the next cleanup deletes and records it.
        harness.Secrets.DeleteOverride = _ => null;
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.Null(harness.Secrets.Peek(name));
        Assert.NotNull(stored.VerifierDeletedAt);
        Assert.NotNull(stored.ConsumedAt);
    }

    [Fact]
    public async Task A_deletion_the_database_failed_to_record_stays_due_and_is_confirmed_by_a_later_cleanup()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var stored = Transaction(harness, state);
        stored.ConsumedAt = harness.Database.Now;
        harness.Database.Now += TimeSpan.FromMinutes(10);
        harness.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.RecordVerifierDeletedAsync);

        // The verifier is deleted but the record fails: the transaction stays due, and the next cleanup confirms it.
        Assert.Equal(new MondayVerifierCleanup(0, 1), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.Null(harness.Secrets.Peek(VerifierName(harness, state)));
        Assert.Null(stored.VerifierDeletedAt);

        harness.Store.FailBefore = _ => false;
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);
        Assert.NotNull(stored.VerifierDeletedAt);
    }

    [Fact]
    public async Task A_cleanup_never_makes_a_code_or_a_state_usable_again()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var stored = Transaction(harness, state);
        stored.ConsumedAt = harness.Database.Now;
        harness.Database.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);

        // The transaction stays consumed: its callback, delivered again, is refused and nothing is exchanged.
        harness.ScriptExchange();
        var replayed = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);
        Assert.True(replayed.IsFailure);
        Assert.Contains(replayed.Error, new[] { ConnectorAuthorizationErrors.TransactionExpired, ConnectorAuthorizationErrors.Replayed });
        Assert.Empty(harness.Server.TokenRequests);
        Assert.NotNull(stored.ConsumedAt);
        Assert.Empty(harness.Database.Authorizations);
    }

    [Fact]
    public async Task A_cleanup_changes_no_authorization_or_transaction_binding()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        harness.ScriptExchange();
        harness.Secrets.DeleteOverride = candidate => candidate.Value == VerifierName(harness, state) ? MondaySecretStoreStatus.Unavailable : null;
        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);
        var row = harness.Row(redirect.ConnectionId);
        var authorization = (row.User, row.Subject, row.Account, row.State, row.Generation, row.MaterialVersion, row.Scopes);
        var stored = Transaction(harness, state);
        var transaction = stored.Transaction;
        var consumedAt = stored.ConsumedAt;

        harness.Secrets.DeleteOverride = _ => null;
        harness.Database.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(new MondayVerifierCleanup(1, 0), (await harness.Authorizer.CleanUpVerifiersAsync(Cancel)).Value);

        Assert.Equal(authorization, (row.User, row.Subject, row.Account, row.State, row.Generation, row.MaterialVersion, row.Scopes));
        Assert.Same(transaction, stored.Transaction);
        Assert.Equal(consumedAt, stored.ConsumedAt);
        Assert.Equal((harness.User, redirect.ConnectionId), (transaction.CanonicalUser, transaction.ConnectionId));
    }

    [Fact]
    public async Task A_cleanup_deletes_only_the_verifiers_of_due_transactions_each_by_its_own_name()
    {
        var harness = new OAuthHarness();
        var orphanState = OAuthHarness.Query((await harness.StartAsync()).AuthorizationUri)["state"];
        Transaction(harness, orphanState).ConsumedAt = harness.Database.Now;
        harness.Database.Now += TimeSpan.FromMinutes(1);
        var secondOrphanState = OAuthHarness.Query((await harness.StartAsync()).AuthorizationUri)["state"];
        Transaction(harness, secondOrphanState).ConsumedAt = harness.Database.Now;
        harness.Database.Now += TimeSpan.FromMinutes(10);

        // A transaction still open, and one a completion consumed a moment ago: both verifiers may still be needed.
        var openState = OAuthHarness.Query((await harness.StartAsync()).AuthorizationUri)["state"];
        var activeState = OAuthHarness.Query((await harness.StartAsync()).AuthorizationUri)["state"];
        Transaction(harness, activeState).ConsumedAt = harness.Database.Now;

        _ = await harness.Authorizer.CleanUpVerifiersAsync(Cancel);

        Assert.Null(harness.Secrets.Peek(VerifierName(harness, orphanState)));
        Assert.Null(harness.Secrets.Peek(VerifierName(harness, secondOrphanState)));
        Assert.NotNull(harness.Secrets.Peek(VerifierName(harness, openState)));
        Assert.NotNull(harness.Secrets.Peek(VerifierName(harness, activeState)));
        Assert.Equal(
            ["delete " + VerifierName(harness, orphanState), "delete " + VerifierName(harness, secondOrphanState)],
            harness.Secrets.Operations.Where(operation => operation.StartsWith("delete", StringComparison.Ordinal)));
        Assert.Null(Transaction(harness, openState).VerifierDeletedAt);
        Assert.Null(Transaction(harness, activeState).VerifierDeletedAt);
    }

    [Fact]
    public async Task Reauthorization_starts_only_for_the_owner_of_a_connection_that_needs_it()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var other = harness.AddUser(harness.Tenant);

        Assert.Equal(ConnectorAuthorizationErrors.NotReauthorizable, (await harness.Authorizer.StartAsync(harness.Principal, connection, Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.InvalidRequest, (await harness.Authorizer.StartAsync(harness.Principal, Guid.Empty, Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.NotReauthorizable, (await harness.Authorizer.StartAsync(harness.Principal, Guid.NewGuid(), Cancel)).Error);

        harness.Row(connection).State = ConnectorAuthorizationState.ReauthorizationRequired;
        Assert.Equal(
            ConnectorAuthorizationErrors.NotReauthorizable,
            (await harness.Authorizer.StartAsync(OAuthHarness.PrincipalOf(other), connection, Cancel)).Error);

        var restoring = await harness.StartAsync(connection);
        Assert.Equal(connection, restoring.ConnectionId);
        Assert.Equal(ConnectorAuthorizationPurpose.Reauthorize, harness.Database.Transactions.Values.Last().Transaction.Purpose);
    }

    [Fact]
    public async Task A_valid_callback_exchanges_the_code_once_verifies_the_identity_and_activates_the_delegation()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var verifier = StoredVerifier(harness, state);
        var (access, refresh) = harness.ScriptExchange();

        var completed = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect, code: "auth-code-not-real"), Cancel);

        Assert.True(completed.IsSuccess, completed.IsSuccess ? string.Empty : completed.Error.Code);
        Assert.Equal(new ConnectorIdentity(harness.Tenant, redirect.ConnectionId, ConnectorIdentityKind.DelegatedUser, "58201774"), completed.Value);

        // Exactly one exchange: JSON to the token endpoint, the verifier and the registered redirect, client credentials
        // in the body only.
        var exchange = Assert.Single(harness.Server.TokenRequests);
        Assert.Equal(HttpMethod.Post, exchange.Method);
        Assert.StartsWith("application/json", exchange.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.False(exchange.Headers.ContainsKey("Authorization"));
        var body = TestTokens.Body(exchange);
        Assert.Equal(
            ["client_id", "client_secret", "code", "code_verifier", "grant_type", "redirect_uri"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("authorization_code", body.GetProperty("grant_type").GetString());
        Assert.Equal(OAuthHarness.ClientId, body.GetProperty("client_id").GetString());
        Assert.Equal(OAuthHarness.ClientSecretValue, body.GetProperty("client_secret").GetString());
        Assert.Equal("auth-code-not-real", body.GetProperty("code").GetString());
        Assert.Equal(OAuthHarness.Redirect.AbsoluteUri, body.GetProperty("redirect_uri").GetString());
        Assert.Equal(verifier, body.GetProperty("code_verifier").GetString());

        // The identity is read with the new token, through the pinned API version, as a single read query.
        var probe = Assert.Single(harness.Server.ApiRequests);
        Assert.Equal(access, probe.Headers["Authorization"]);
        Assert.Equal(MondayApiVersion.Pinned, probe.Headers[MondayApiVersion.HeaderName]);
        Assert.Contains("me { id account { id } }", probe.Body, StringComparison.Ordinal);

        // Active at generation 1; the pair is in secret storage under the row's version, the verifier is gone.
        var row = harness.Row(redirect.ConnectionId);
        Assert.Equal((ConnectorAuthorizationState.Active, 1L, "58201774", "9001", harness.User), (row.State, row.Generation, row.Subject, row.Account, row.User));
        var stored = harness.Secrets.Peek($"wce-monday-tokens-{harness.Tenant.Value:N}-{redirect.ConnectionId:N}-{row.MaterialVersion:N}");
        Assert.NotNull(stored);
        Assert.Contains(refresh, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.Null(harness.Secrets.Peek(VerifierName(harness, state)));
        Assert.Empty(harness.Server.RevokeRequests);
    }

    [Fact]
    public async Task A_malformed_or_unknown_state_names_no_transaction_and_reaches_no_exchange()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];

        foreach (var forged in new[]
                 {
                     null, string.Empty, "x", state + "a", state[..^1], state.ToUpperInvariant(), state.Replace('.', '-'),
                     Guid.Empty.ToString("N") + state[32..], Guid.NewGuid().ToString("N") + state[32..], state[..33] + new string('*', 43),
                 })
        {
            var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect, state: forged ?? "\u0000"), Cancel);
            Assert.Equal(ConnectorAuthorizationErrors.TransactionNotFound, result.Error);
        }

        Assert.Empty(harness.Server.TokenRequests);
        Assert.Null(harness.Database.Transactions.Values.Single().ConsumedAt);
    }

    [Fact]
    public async Task Another_user_or_a_wrong_state_is_refused_and_leaves_the_transaction_to_its_owner()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var intruder = OAuthHarness.PrincipalOf(harness.AddUser(harness.Tenant));
        var wrongSecret = state[..33] + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        Assert.Equal(ConnectorAuthorizationErrors.BindingMismatch, (await harness.Authorizer.CompleteAsync(intruder, OAuthHarness.CallbackFor(redirect), Cancel)).Error);
        Assert.Equal(
            ConnectorAuthorizationErrors.StateMismatch,
            (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect, state: wrongSecret), Cancel)).Error);
        Assert.Empty(harness.Server.TokenRequests);
        Assert.NotNull(harness.Secrets.Peek(VerifierName(harness, state)));

        harness.ScriptExchange();
        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);
    }

    [Fact]
    public async Task An_expired_transaction_is_refused_before_anything_else_and_its_verifier_destroyed()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        harness.Database.Now += TimeSpan.FromMinutes(10);

        var intruder = OAuthHarness.PrincipalOf(harness.AddUser(harness.Tenant));
        Assert.Equal(ConnectorAuthorizationErrors.TransactionExpired, (await harness.Authorizer.CompleteAsync(intruder, OAuthHarness.CallbackFor(redirect), Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.TransactionExpired, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).Error);
        Assert.Null(harness.Secrets.Peek(VerifierName(harness, state)));
        Assert.Empty(harness.Server.TokenRequests);
    }

    [Fact]
    public async Task A_replayed_callback_is_refused_without_a_second_exchange()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.ScriptExchange();
        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);

        var replayed = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.Replayed, replayed.Error);
        Assert.Single(harness.Server.TokenRequests);
    }

    [Fact]
    public async Task Concurrent_copies_of_one_callback_exchange_the_code_exactly_once()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.ScriptExchange();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel),
            Cancel)));

        Assert.Single(results, result => result.IsSuccess);
        Assert.All(results.Where(result => result.IsFailure), result => Assert.Equal(ConnectorAuthorizationErrors.Replayed, result.Error));
        Assert.Single(harness.Server.TokenRequests);
    }

    [Fact]
    public async Task A_copy_refused_as_a_replay_while_the_winner_is_completing_leaves_the_winners_verifier()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];
        var verifierName = VerifierName(harness, state);
        var verifier = StoredVerifier(harness, state);
        harness.ScriptExchange();

        // Callback A takes ownership of the transaction and is held just before it reads its verifier.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        harness.Secrets.GetOverride = name =>
        {
            if (name.Value == verifierName && reading.TrySetResult())
            {
                release.Wait(TimeSpan.FromSeconds(30), Cancel);
            }

            return null;
        };
        var winner = Task.Run(() => harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel), Cancel);
        await reading.Task.WaitAsync(Cancel);

        try
        {
            // Copy B arrives now: refused as a replay, it gains nothing and leaves A's verifier where it is.
            var copy = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);
            Assert.Equal(ConnectorAuthorizationErrors.Replayed, copy.Error);
            Assert.Equal(verifier, StoredVerifier(harness, state));
            Assert.Empty(harness.Server.TokenRequests);
        }
        finally
        {
            release.Set();
        }

        // A goes on: it reads its verifier, exchanges the code exactly once and completes; only A removes the verifier.
        var completed = await winner;
        Assert.True(completed.IsSuccess, completed.IsSuccess ? string.Empty : completed.Error.Code);
        var exchange = Assert.Single(harness.Server.TokenRequests);
        Assert.Equal(verifier, TestTokens.Body(exchange).GetProperty("code_verifier").GetString());
        Assert.Null(harness.Secrets.Peek(verifierName));
        Assert.Equal(
            ["set " + verifierName, "get " + verifierName, "delete " + verifierName],
            harness.Secrets.Operations.Where(operation => operation.EndsWith(verifierName, StringComparison.Ordinal)));

        // No later copy succeeds either.
        Assert.Equal(ConnectorAuthorizationErrors.Replayed, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).Error);
        Assert.Single(harness.Server.TokenRequests);
    }

    public static TheoryData<string, string?, string?, string?> RefusedResponses => new()
    {
        // case, code, error, iss
        { "denied", "auth-code-not-real", "access_denied", null },
        { "denied without a code", null, "access_denied", null },
        { "another issuer", "auth-code-not-real", null, "https://auth.example.test" },
        { "issuer with a trailing slash", "auth-code-not-real", null, "https://auth.monday.com/" },
        { "no code", null, null, null },
        { "empty code", string.Empty, null, null },
        { "code with a space", "auth code", null, null },
        { "code with a line break", "auth-code\r\nX-Injected: 1", null, null },
        { "oversized code", new string('c', 2049), null, null },
    };

    [Theory]
    [MemberData(nameof(RefusedResponses))]
    public async Task A_response_that_is_not_an_approval_from_monday_with_a_code_is_refused_and_consumes_the_transaction(
        string name,
        string? code,
        string? error,
        string? issuer)
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var state = OAuthHarness.Query(redirect.AuthorizationUri)["state"];

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect, code, error, issuer), Cancel);

        var expected = error is not null ? ConnectorAuthorizationErrors.Denied
            : issuer is not null ? ConnectorAuthorizationErrors.IssuerMismatch
            : ConnectorAuthorizationErrors.CodeMissing;
        Assert.True(expected == result.Error, name);
        Assert.Empty(harness.Server.TokenRequests);
        Assert.Null(harness.Secrets.Peek(VerifierName(harness, state)));

        // Consumed: the same state can never be tried again.
        harness.ScriptExchange();
        Assert.Equal(ConnectorAuthorizationErrors.Replayed, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).Error);
    }

    [Fact]
    public async Task Monday_as_the_issuer_is_accepted_and_the_absence_of_an_issuer_too()
    {
        var withIssuer = new OAuthHarness();
        var redirect = await withIssuer.StartAsync();
        withIssuer.ScriptExchange();
        Assert.True((await withIssuer.Authorizer.CompleteAsync(withIssuer.Principal, OAuthHarness.CallbackFor(redirect, issuer: "https://auth.monday.com"), Cancel)).IsSuccess);
    }

    [Fact]
    public async Task A_redirect_address_that_changed_since_the_start_is_refused()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var moved = new MondayConnectionAuthorizer(
            harness.Store, harness.Secrets, new MondayOAuthOptions(OAuthHarness.ClientId, new Uri("https://other.example.test/callback")),
            harness.Http, harness.Reader, harness.TenantContext, harness.Clock);

        var result = await moved.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.RedirectMismatch, result.Error);
        Assert.Empty(harness.Server.TokenRequests);
    }

    [Fact]
    public async Task Another_tenant_cannot_complete_the_authorization_and_the_owner_still_can()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        var otherTenant = TenantId.New();
        var foreign = new MondayConnectionAuthorizer(
            new InMemoryAuthorizationStore(harness.Database, otherTenant), harness.Secrets, harness.Options, harness.Http, harness.Reader,
            new FixedTenantContextAccessor(TenantContext.Resolved(otherTenant, TenantResolutionSource.AuthenticatedPrincipal)), harness.Clock);

        Assert.Equal(ConnectorAuthorizationErrors.TransactionNotFound, (await foreign.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).Error);
        Assert.Empty(harness.Server.TokenRequests);

        harness.ScriptExchange();
        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);
    }

    [Fact]
    public async Task Without_its_verifier_or_the_client_secret_nothing_is_exchanged()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Secrets.Remove(VerifierName(harness, OAuthHarness.Query(redirect.AuthorizationUri)["state"]));
        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).Error);

        var second = await harness.StartAsync();
        harness.Secrets.Remove(MondaySecretName.ClientSecret.Value);
        Assert.Equal(ConnectorAuthorizationErrors.ClientUnavailable, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(second), Cancel)).Error);

        var third = await harness.StartAsync();
        harness.Secrets.Put(MondaySecretName.ClientSecret, Encoding.UTF8.GetBytes("with a space"));
        Assert.Equal(ConnectorAuthorizationErrors.ClientUnavailable, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(third), Cancel)).Error);

        Assert.Empty(harness.Server.TokenRequests);
    }

    public static TheoryData<string, HttpStatusCode, string> FailedExchanges => new()
    {
        { "rejected", HttpStatusCode.BadRequest, "connector.authorization.exchange_rejected" },
        { "unauthorized client", HttpStatusCode.Unauthorized, "connector.authorization.exchange_rejected" },
        { "throttled", HttpStatusCode.TooManyRequests, "connector.authorization.exchange_failed" },
        { "server error", HttpStatusCode.InternalServerError, "connector.authorization.exchange_failed" },
        { "unavailable", HttpStatusCode.ServiceUnavailable, "connector.authorization.exchange_failed" },
        { "redirect", HttpStatusCode.Found, "connector.authorization.exchange_failed" },
    };

    [Theory]
    [MemberData(nameof(FailedExchanges))]
    public async Task A_failed_exchange_is_never_retried(string name, HttpStatusCode status, string code)
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Server.ThenToken(status, """{"error":"SENTINEL-provider-text"}""");

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.True(code == result.Error.Code, name);
        Assert.Single(harness.Server.TokenRequests);
        Assert.Null(harness.Database.Authorizations.Values.SingleOrDefault());
        Assert.DoesNotContain("SENTINEL", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_answer_from_another_address_is_never_trusted()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Server.ThenToken((_, _) =>
        {
            var response = FakeMondayAuthServer.Json(
                HttpStatusCode.OK, TestTokens.Response(TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "a"), "refresh-a"));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://elsewhere.example/oauth/token");
            return Task.FromResult(response);
        });

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.ExchangeFailed, result.Error);
        Assert.Empty(harness.Server.ApiRequests);
        Assert.Empty(harness.Database.Authorizations);
    }

    [Fact]
    public async Task A_network_failure_or_a_timeout_of_the_exchange_is_never_retried()
    {
        var unreachable = new OAuthHarness();
        var first = await unreachable.StartAsync();
        unreachable.Server.ThenToken((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "SENTINEL"));
        Assert.Equal(ConnectorAuthorizationErrors.ExchangeFailed, (await unreachable.Authorizer.CompleteAsync(unreachable.Principal, OAuthHarness.CallbackFor(first), Cancel)).Error);
        Assert.Single(unreachable.Server.TokenRequests);

        var slow = new OAuthHarness(new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, httpTimeout: TimeSpan.FromSeconds(1)));
        var second = await slow.StartAsync();
        // The answer never comes: only the one-second bound of the OAuth call ends the wait.
        slow.Server.ThenToken(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable.");
        });
        Assert.Equal(ConnectorAuthorizationErrors.ExchangeFailed, (await slow.Authorizer.CompleteAsync(slow.Principal, OAuthHarness.CallbackFor(second), Cancel)).Error);
        Assert.Single(slow.Server.TokenRequests);
    }

    public static TheoryData<string, string, bool, bool> UnusableGrants
    {
        get
        {
            var hour = MondayHarness.Now.AddHours(1);
            var access = TestTokens.Access(hour, "a");
            return new()
            {
                // case, body, carries a refresh token to revoke, carries an access token to revoke
                { "no refresh token", "{\"access_token\":\"" + access + "\",\"token_type\":\"Bearer\",\"scope\":\"boards:read me:read\"}", false, true },
                { "no access token", "{\"refresh_token\":\"refresh-x\",\"token_type\":\"Bearer\",\"scope\":\"boards:read me:read\"}", true, false },
                { "not bearer", TestTokens.Response(access, "refresh-x", tokenType: "mac"), true, true },
                { "no scope", "{\"access_token\":\"" + access + "\",\"refresh_token\":\"refresh-x\",\"token_type\":\"Bearer\"}", true, true },
                { "access token not a JWT", TestTokens.Response("opaque-access-token", "refresh-x"), true, true },
                { "no exp", TestTokens.Response(TestTokens.Jwt("{\"sub\":\"1\"}"), "refresh-x"), true, true },
                { "exp is text", TestTokens.Response(TestTokens.Jwt("{\"exp\":\"tomorrow\"}"), "refresh-x"), true, true },
                { "already expired", TestTokens.Response(TestTokens.Access(MondayHarness.Now.AddSeconds(-1), "a"), "refresh-x"), true, true },
                { "expires now", TestTokens.Response(TestTokens.Access(MondayHarness.Now, "a"), "refresh-x"), true, true },
                { "absurd expiry", TestTokens.Response(TestTokens.Access(MondayHarness.Now.AddDays(400), "a"), "refresh-x"), true, true },
                { "malformed expires_in", TestTokens.Response(access, "refresh-x", extra: ",\"expires_in\":\"soon\""), true, true },
                { "negative expires_in", TestTokens.Response(access, "refresh-x", extra: ",\"expires_in\":-5"), true, true },
                { "token with a space", TestTokens.Response(access, "refresh x"), false, true },
                { "not JSON", "access_token=" + access, false, false },
            };
        }
    }

    [Theory]
    [MemberData(nameof(UnusableGrants))]
    public async Task An_unusable_grant_is_refused_and_whatever_it_issued_is_revoked(string name, string body, bool refreshIssued, bool accessIssued)
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Server.ThenToken(HttpStatusCode.OK, body);

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.True(ConnectorAuthorizationErrors.GrantUnusable == result.Error, name);
        var revoked = harness.Server.RevokeRequests.Select(request => TestTokens.Body(request).GetProperty("token_type_hint").GetString()).ToList();
        Assert.Equal((refreshIssued ? 1 : 0) + (accessIssued ? 1 : 0), revoked.Count);
        Assert.Empty(harness.Database.Authorizations);
        Assert.Empty(harness.Server.ApiRequests);
        Assert.DoesNotContain(harness.Secrets.Names, stored => stored.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expires_in_may_only_shorten_the_jwt_expiry()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Server.ThenToken(HttpStatusCode.OK, TestTokens.Response(TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "a"), "refresh-a", extra: ",\"expires_in\":600"));
        harness.Server.ThenApi(HttpStatusCode.OK, TestTokens.Me());

        Assert.True((await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel)).IsSuccess);
        Assert.Equal(harness.Clock.UtcNow.AddSeconds(600), harness.Row(redirect.ConnectionId).AccessExpiresAt);

        var longer = new OAuthHarness();
        var second = await longer.StartAsync();
        longer.Server.ThenToken(HttpStatusCode.OK, TestTokens.Response(TestTokens.Access(longer.Clock.UtcNow.AddMinutes(30), "b"), "refresh-b", extra: ",\"expires_in\":86400"));
        longer.Server.ThenApi(HttpStatusCode.OK, TestTokens.Me());
        Assert.True((await longer.Authorizer.CompleteAsync(longer.Principal, OAuthHarness.CallbackFor(second), Cancel)).IsSuccess);
        Assert.Equal(longer.Clock.UtcNow.AddMinutes(30), longer.Row(second.ConnectionId).AccessExpiresAt);
    }

    [Theory]
    [InlineData("boards:read me:read boards:write")]
    [InlineData("boards:read me:read webhooks:write")]
    [InlineData("boards:read me:read ai:consume")]
    [InlineData("boards:read me:read unknown:read")]
    [InlineData("boards:read")]
    [InlineData("me:read")]
    public async Task A_grant_beyond_the_read_allowlist_or_short_of_the_required_scopes_is_refused_and_revoked(string scope)
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Server.ThenToken(HttpStatusCode.OK, TestTokens.Response(TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "a"), "refresh-a", scope));

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.ScopeRefused, result.Error);
        Assert.Equal(
            ["refresh_token", "access_token"],
            harness.Server.RevokeRequests.Select(request => TestTokens.Body(request).GetProperty("token_type_hint").GetString()));
        Assert.Empty(harness.Database.Authorizations);
        Assert.Empty(harness.Server.ApiRequests);
    }

    public static TheoryData<string, HttpStatusCode, string> UnverifiableIdentities => new()
    {
        { "rejected token", HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Unauthorized\"}]}" },
        { "an email as the id", HttpStatusCode.OK, "{\"data\":{\"me\":{\"id\":\"user@example.test\",\"account\":{\"id\":\"9001\"}}}}" },
        { "a name as the id", HttpStatusCode.OK, "{\"data\":{\"me\":{\"id\":\"Jane\",\"account\":{\"id\":\"9001\"}}}}" },
        { "no account", HttpStatusCode.OK, "{\"data\":{\"me\":{\"id\":\"58201774\"}}}" },
        { "no me", HttpStatusCode.OK, "{\"data\":{}}" },
        { "a numeric id", HttpStatusCode.OK, "{\"data\":{\"me\":{\"id\":58201774,\"account\":{\"id\":\"9001\"}}}}" },
    };

    [Theory]
    [MemberData(nameof(UnverifiableIdentities))]
    public async Task A_delegated_identity_that_cannot_be_verified_is_refused_and_the_grant_revoked(string name, HttpStatusCode status, string body)
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.Server.ThenToken(HttpStatusCode.OK, TestTokens.Response(TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "a"), "refresh-a"));
        harness.Server.ThenApi(status, body);

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.True(ConnectorAuthorizationErrors.IdentityUnverified == result.Error, name);
        Assert.Single(harness.Server.ApiRequests);
        Assert.Equal(2, harness.Server.RevokeRequests.Count());
        Assert.Empty(harness.Database.Authorizations);
    }

    [Fact]
    public async Task Reauthorization_restores_only_the_same_monday_user_and_account_and_destroys_the_old_version()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var oldVersion = harness.Row(connection).MaterialVersion;
        harness.Row(connection).State = ConnectorAuthorizationState.ReauthorizationRequired;

        foreach (var (user, account) in new[] { ("11111111", "9001"), ("58201774", "1234") })
        {
            var attempt = await harness.StartAsync(connection);
            harness.ScriptExchange("x", userId: user, accountId: account);
            Assert.Equal(ConnectorAuthorizationErrors.IdentityMismatch, (await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(attempt), Cancel)).Error);
            Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, harness.Row(connection).State);
        }

        Assert.Equal(4, harness.Server.RevokeRequests.Count());
        var restoring = await harness.StartAsync(connection);
        var (_, refresh) = harness.ScriptExchange("2");
        var restored = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(restoring), Cancel);

        Assert.True(restored.IsSuccess);
        var row = harness.Row(connection);
        Assert.Equal((ConnectorAuthorizationState.Active, 2L), (row.State, row.Generation));
        Assert.Null(harness.Secrets.Peek($"wce-monday-tokens-{harness.Tenant.Value:N}-{connection:N}-{oldVersion:N}"));
        Assert.Contains(refresh, Encoding.UTF8.GetString(harness.Secrets.Peek($"wce-monday-tokens-{harness.Tenant.Value:N}-{connection:N}-{row.MaterialVersion:N}")!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pair_that_cannot_be_stored_is_revoked_and_nothing_is_activated()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.ScriptExchange();
        harness.Secrets.SetOverride = name => name.Value.StartsWith("wce-monday-tokens-", StringComparison.Ordinal) ? MondaySecretStoreStatus.Unavailable : null;

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, result.Error);
        Assert.Equal(2, harness.Server.RevokeRequests.Count());
        Assert.Empty(harness.Database.Authorizations);
    }

    [Fact]
    public async Task An_activation_that_did_not_commit_revokes_and_destroys_the_pair()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.ScriptExchange();
        harness.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.ActivateAsync);

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, result.Error);
        Assert.Equal(2, harness.Server.RevokeRequests.Count());
        Assert.DoesNotContain(harness.Secrets.Names, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_activation_whose_commit_outcome_is_unknown_is_decided_by_a_fresh_read_and_never_guessed()
    {
        // Committed although reported failed: the fresh read shows the pair is current, so it is kept.
        var committed = new OAuthHarness();
        var first = await committed.StartAsync();
        committed.ScriptExchange();
        committed.Store.FailAfter = operation => operation == nameof(IConnectorAuthorizationStore.ActivateAsync);
        Assert.True((await committed.Authorizer.CompleteAsync(committed.Principal, OAuthHarness.CallbackFor(first), Cancel)).IsSuccess);
        Assert.Empty(committed.Server.RevokeRequests);

        // The fresh read fails too: the pair may be current, so it is neither revoked nor destroyed.
        var unknown = new OAuthHarness();
        var second = await unknown.StartAsync();
        unknown.ScriptExchange();
        unknown.Store.FailAfter = operation => operation == nameof(IConnectorAuthorizationStore.ActivateAsync);
        unknown.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.ReadAsync);
        Assert.Equal(ConnectorAuthorizationErrors.StorageFailed, (await unknown.Authorizer.CompleteAsync(unknown.Principal, OAuthHarness.CallbackFor(second), Cancel)).Error);
        Assert.Empty(unknown.Server.RevokeRequests);
        Assert.Contains(unknown.Secrets.Names, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_deactivated_during_the_authorization_delegates_nothing()
    {
        var harness = new OAuthHarness();
        var redirect = await harness.StartAsync();
        harness.ScriptExchange();
        harness.SetUserActive(harness.User, active: false);

        var result = await harness.Authorizer.CompleteAsync(harness.Principal, OAuthHarness.CallbackFor(redirect), Cancel);

        Assert.Equal(ConnectorAuthorizationErrors.UserNotEligible, result.Error);
        Assert.Equal(2, harness.Server.RevokeRequests.Count());
        Assert.Empty(harness.Database.Authorizations);
    }

    private static InMemoryAuthorizationDatabase.StoredTransaction Transaction(OAuthHarness harness, string state)
    {
        lock (harness.Database.Gate)
        {
            return harness.Database.Transactions[Guid.ParseExact(state[..32], "N")];
        }
    }

    // A new process: its own store and authorizer, nothing in memory, the same database and secret storage.
    private static MondayConnectionAuthorizer Restarted(OAuthHarness harness) =>
        new(new InMemoryAuthorizationStore(harness.Database, harness.Tenant), harness.Secrets, harness.Options, harness.Http, harness.Reader, harness.TenantContext, harness.Clock);

    private static string VerifierName(OAuthHarness harness, string state) =>
        $"wce-monday-pkce-{harness.Tenant.Value:N}-{state[..32]}";

    private static string StoredVerifier(OAuthHarness harness, string state)
    {
        var stored = harness.Secrets.Peek(VerifierName(harness, state)) ?? throw new InvalidOperationException("No verifier stored.");
        using var document = JsonDocument.Parse(stored);
        Assert.Equal(harness.Tenant.Value.ToString("D"), document.RootElement.GetProperty("tenant").GetString());
        Assert.Equal(Guid.ParseExact(state[..32], "N").ToString("D"), document.RootElement.GetProperty("transaction").GetString());
        return document.RootElement.GetProperty("verifier").GetString()!;
    }
}
