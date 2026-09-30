using System.Net;
using System.Text;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3B: the credential source of an authorized connection. Access-time checks before anything is sent; renewal of
/// the rotating pair that is single-flight, never reuses an old refresh token, and ends every ambiguous or
/// unstorable outcome in reauthorization; and the read client's single renewal after monday rejects a token.
/// </summary>
public sealed class MondayConnectionCredentialSourceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_credential_carries_the_delegated_identity_and_needs_no_renewal()
    {
        var harness = new OAuthHarness();
        var (connection, access, _) = await harness.ConnectAsync();
        var tokenCalls = harness.Server.TokenRequests.Count();

        var (result, authorization) = await ReadAsync(harness, connection);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ConnectorIdentity(harness.Tenant, connection, ConnectorIdentityKind.DelegatedUser, "58201774"), result.Identity);
        Assert.Equal(access, authorization);
        Assert.Equal(tokenCalls, harness.Server.TokenRequests.Count());
    }

    [Fact]
    public async Task Within_the_margin_the_pair_is_renewed_the_new_pair_kept_first_and_the_old_version_destroyed()
    {
        var harness = new OAuthHarness();
        var (connection, _, refresh) = await harness.ConnectAsync();
        var oldVersion = harness.Row(connection).MaterialVersion;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var (newAccess, newRefresh) = ScriptRefresh(harness, "2");

        var (result, authorization) = await ReadAsync(harness, connection);

        Assert.True(result.IsSuccess);
        Assert.Equal(newAccess, authorization);
        var renewal = TestTokens.Body(harness.Server.TokenRequests.Last());
        Assert.Equal("refresh_token", renewal.GetProperty("grant_type").GetString());
        Assert.Equal(refresh, renewal.GetProperty("refresh_token").GetString());
        Assert.Equal(OAuthHarness.ClientSecretValue, renewal.GetProperty("client_secret").GetString());

        var row = harness.Row(connection);
        Assert.Equal(2L, row.Generation);
        Assert.Null(row.Claim);
        Assert.Null(harness.Secrets.Peek(TokensName(harness, connection, oldVersion)));
        Assert.Contains(newRefresh, Encoding.UTF8.GetString(harness.Secrets.Peek(TokensName(harness, connection, row.MaterialVersion))!), StringComparison.Ordinal);

        // The new pair was stored before the row pointed at it.
        var operations = harness.Secrets.Operations.ToList();
        Assert.True(
            operations.IndexOf("set " + TokensName(harness, connection, row.MaterialVersion)) < operations.IndexOf("delete " + TokensName(harness, connection, oldVersion)));
    }

    [Fact]
    public async Task Every_renewal_uses_the_latest_refresh_token_and_never_an_older_one()
    {
        var harness = new OAuthHarness();
        var (connection, _, first) = await harness.ConnectAsync();
        var used = new List<string>();
        var issued = new List<string> { first };

        for (var round = 2; round <= 4; round++)
        {
            harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
            issued.Add(ScriptRefresh(harness, round.ToString(System.Globalization.CultureInfo.InvariantCulture)).Refresh);
            Assert.True((await ReadAsync(harness, connection)).Result.IsSuccess);
            used.Add(TestTokens.Body(harness.Server.TokenRequests.Last()).GetProperty("refresh_token").GetString()!);
        }

        Assert.Equal(issued.Take(3), used);
        Assert.Equal(4L, harness.Row(connection).Generation);
    }

    [Fact]
    public async Task Concurrent_callers_renew_once_and_keep_using_the_still_valid_token_meanwhile()
    {
        var harness = new OAuthHarness();
        var (connection, access, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(58);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAccess = TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "2");
        harness.Server.ThenToken(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return FakeMondayAuthServer.Json(HttpStatusCode.OK, TestTokens.Response(newAccess, "refresh-2"));
        });

        var winner = Task.Run(() => harness.Credentials.AcquireAsync(harness.Context(connection), Cancel).AsTask(), Cancel);
        await WaitUntilAsync(() => harness.Row(connection).Claim is not null);

        var others = await Task.WhenAll(Enumerable.Range(0, 9).Select(_ => harness.Credentials.AcquireAsync(harness.Context(connection), Cancel).AsTask()));
        release.SetResult();
        var renewed = await winner;

        Assert.All(others, other => Assert.NotNull(other.Credential));
        Assert.NotNull(renewed.Credential);
        Assert.Single(harness.Server.TokenRequests, request => request.Body.Contains("\"grant_type\":\"refresh_token\"", StringComparison.Ordinal));
        Assert.Equal(2L, harness.Row(connection).Generation);
        Assert.NotEqual(access, newAccess);
    }

    [Fact]
    public async Task A_caller_whose_token_has_expired_waits_for_another_callers_renewal_instead_of_renewing_too()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(61);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAccess = TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "2");
        harness.Server.ThenToken(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return FakeMondayAuthServer.Json(HttpStatusCode.OK, TestTokens.Response(newAccess, "refresh-2"));
        });

        var winner = Task.Run(() => harness.Credentials.AcquireAsync(harness.Context(connection), Cancel).AsTask(), Cancel);
        await WaitUntilAsync(() => harness.Row(connection).Claim is not null);

        // The waiting caller's first pause lets the renewal finish.
        var waiter = new MondayConnectionCredentialSource(
            harness.Store, harness.Secrets, harness.Options, harness.Http, harness.Clock,
            new HookDelay(async () =>
            {
                release.TrySetResult();
                await winner;
            }));
        var waited = await waiter.AcquireAsync(harness.Context(connection), Cancel);

        Assert.NotNull(waited.Credential);
        Assert.Equal(harness.Row(connection).AccessExpiresAt, waited.Credential.ExpiresAt);
        Assert.Equal(2L, harness.Row(connection).Generation);
        Assert.Single(harness.Server.TokenRequests, request => request.Body.Contains("\"grant_type\":\"refresh_token\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throttled_refresh_changes_nothing_and_is_reported_as_transient_with_mondays_wait()
    {
        var harness = new OAuthHarness();
        var (connection, _, refresh) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var version = harness.Row(connection).MaterialVersion;
        harness.Server.ThenToken((_, _) =>
        {
            var response = FakeMondayAuthServer.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });

        var throttled = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorFailureKind.RateLimited, throttled.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), throttled.Failure.RetryAfter);
        var row = harness.Row(connection);
        Assert.Equal((ConnectorAuthorizationState.Active, 1L, version), (row.State, row.Generation, row.MaterialVersion));
        Assert.Null(row.Claim);

        // Provably unchanged, so the same refresh token is still the latest and may be presented again.
        ScriptRefresh(harness, "2");
        Assert.NotNull((await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Credential);
        Assert.Equal(refresh, TestTokens.Body(harness.Server.TokenRequests.Last()).GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task A_refresh_that_never_left_changes_nothing()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        harness.Server.ThenToken((_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "SENTINEL"));

        var unsent = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorFailureKind.NetworkTransient, unsent.Failure!.Kind);
        Assert.Equal((ConnectorAuthorizationState.Active, 1L), (harness.Row(connection).State, harness.Row(connection).Generation));
        Assert.Null(harness.Row(connection).Claim);
    }

    public static TheoryData<string, HttpStatusCode, string, int> EndingRefreshes => new()
    {
        // case, status, body, revocations of whatever the answer issued
        { "rejected grant", HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}", 0 },
        { "rejected client", HttpStatusCode.Unauthorized, "{}", 0 },
        { "forbidden", HttpStatusCode.Forbidden, "{}", 0 },
        { "server error", HttpStatusCode.InternalServerError, "{}", 0 },
        { "bad gateway", HttpStatusCode.BadGateway, "{}", 0 },
        { "gateway timeout", HttpStatusCode.GatewayTimeout, "{}", 0 },
        { "unavailable", HttpStatusCode.ServiceUnavailable, "{}", 0 },
        { "redirect", HttpStatusCode.Found, "{}", 0 },
        { "unreadable success", HttpStatusCode.OK, "not json", 0 },
        { "success without a refresh token", HttpStatusCode.OK, "{\"access_token\":\"" + TestTokens.Access(MondayHarness.Now.AddHours(2), "x") + "\",\"token_type\":\"Bearer\",\"scope\":\"boards:read me:read\"}", 1 },
        { "success with a write scope", HttpStatusCode.OK, TestTokens.Response(TestTokens.Access(MondayHarness.Now.AddHours(2), "x"), "refresh-x", "boards:read boards:write me:read"), 2 },
    };

    [Theory]
    [MemberData(nameof(EndingRefreshes))]
    public async Task Any_refresh_that_may_have_rotated_the_token_or_cannot_be_kept_requires_reauthorization_and_is_never_retried(
        string name,
        HttpStatusCode status,
        string body,
        int revocations)
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        harness.Server.ThenToken(status, body);
        var before = harness.Server.TokenRequests.Count();

        var ended = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.True(ConnectorErrors.ReauthorizationRequired == ended.Failure?.Error, name);
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, harness.Row(connection).State);
        Assert.Null(harness.Row(connection).Claim);
        Assert.Equal(revocations, harness.Server.RevokeRequests.Count());

        // Never again: no later caller presents the old refresh token.
        harness.Clock.UtcNow += TimeSpan.FromMinutes(1);
        Assert.Equal(ConnectorErrors.ReauthorizationRequired, (await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Failure!.Error);
        Assert.Equal(before + 1, harness.Server.TokenRequests.Count());
    }

    [Fact]
    public async Task A_refresh_whose_answer_times_out_requires_reauthorization()
    {
        var harness = new OAuthHarness(new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, httpTimeout: TimeSpan.FromSeconds(1)));
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        harness.Server.ThenToken(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable.");
        });

        var ended = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorErrors.ReauthorizationRequired, ended.Failure!.Error);
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, harness.Row(connection).State);
    }

    [Fact]
    public async Task A_new_pair_that_cannot_be_stored_is_revoked_and_the_connection_requires_reauthorization()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var (newAccess, newRefresh) = ScriptRefresh(harness, "2");
        harness.Secrets.SetOverride = _ => MondaySecretStoreStatus.Unavailable;

        var ended = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorErrors.ReauthorizationRequired, ended.Failure!.Error);
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, harness.Row(connection).State);
        Assert.Equal(
            [(newRefresh, "refresh_token"), (newAccess, "access_token")],
            harness.Server.RevokeRequests.Select(request => TestTokens.Body(request)).Select(body => (body.GetProperty("token").GetString(), body.GetProperty("token_type_hint").GetString())));
    }

    [Fact]
    public async Task A_completion_that_did_not_commit_is_tried_once_more_and_otherwise_the_pair_is_kept_for_adoption()
    {
        // Once more succeeds.
        var retried = new OAuthHarness();
        var (first, _, _) = await retried.ConnectAsync();
        retried.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var (access, _) = ScriptRefresh(retried, "2");
        var failures = 0;
        retried.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.CompleteRefreshAsync) && failures++ == 0;
        var renewed = await retried.Credentials.AcquireAsync(retried.Context(first), Cancel);
        Assert.NotNull(renewed.Credential);
        Assert.Equal(2L, retried.Row(first).Generation);
        Assert.Empty(retried.Server.RevokeRequests);

        // Fails twice: the pair stays stored under the claim, unrevoked, for whoever finds the claim abandoned.
        var kept = new OAuthHarness();
        var (second, _, _) = await kept.ConnectAsync();
        kept.Clock.UtcNow += TimeSpan.FromMinutes(56);
        ScriptRefresh(kept, "2");
        kept.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.CompleteRefreshAsync);
        Assert.Equal(ConnectorErrors.CredentialUnavailable, (await kept.Credentials.AcquireAsync(kept.Context(second), Cancel)).Failure!.Error);
        var claim = kept.Row(second).Claim!.Value;
        Assert.NotNull(kept.Secrets.Peek(TokensName(kept, second, claim)));
        Assert.Empty(kept.Server.RevokeRequests);
        Assert.Equal((ConnectorAuthorizationState.Active, 1L), (kept.Row(second).State, kept.Row(second).Generation));
    }

    [Fact]
    public async Task An_abandoned_renewal_whose_new_pair_was_stored_is_adopted_without_calling_monday()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var oldVersion = harness.Row(connection).MaterialVersion;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var (newAccess, _) = ScriptRefresh(harness, "2");

        // The renewal stores its pair, then its completion never lands, as if the process stopped there.
        var reads = 0;
        harness.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.CompleteRefreshAsync)
            || (operation == nameof(IConnectorAuthorizationStore.ReadAsync) && ++reads > 1);
        Assert.Null((await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Credential);
        harness.Store.FailBefore = _ => false;
        var claim = harness.Row(connection).Claim!.Value;
        var tokenCalls = harness.Server.TokenRequests.Count();

        harness.Database.Now += TimeSpan.FromMinutes(3);
        var (result, authorization) = await ReadAsync(harness, connection);

        Assert.True(result.IsSuccess);
        Assert.Equal(newAccess, authorization);
        Assert.Equal(tokenCalls, harness.Server.TokenRequests.Count());
        Assert.Equal((2L, claim), (harness.Row(connection).Generation, harness.Row(connection).MaterialVersion));
        Assert.Null(harness.Secrets.Peek(TokensName(harness, connection, oldVersion)));
    }

    [Fact]
    public async Task An_abandoned_renewal_without_a_stored_pair_requires_reauthorization_without_calling_monday()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        await harness.Store.ClaimRefreshAsync(connection, 1, Guid.CreateVersion7(), Cancel);
        harness.Database.Now += TimeSpan.FromMinutes(3);
        var tokenCalls = harness.Server.TokenRequests.Count();

        var ended = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorErrors.ReauthorizationRequired, ended.Failure!.Error);
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, harness.Row(connection).State);
        Assert.Equal(tokenCalls, harness.Server.TokenRequests.Count());
    }

    [Fact]
    public async Task A_live_renewal_is_never_taken_over_before_its_timeout()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(61);
        await harness.Store.ClaimRefreshAsync(connection, 1, Guid.CreateVersion7(), Cancel);
        harness.Database.Now += TimeSpan.FromSeconds(100);
        var tokenCalls = harness.Server.TokenRequests.Count();

        var waited = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorErrors.CredentialUnavailable, waited.Failure!.Error);
        // Eight reads at most, a one-second pause between them.
        Assert.Equal(7, harness.Delay.Waits.Count);
        Assert.All(harness.Delay.Waits, wait => Assert.Equal(TimeSpan.FromSeconds(1), wait));
        Assert.Equal((ConnectorAuthorizationState.Active, 1L), (harness.Row(connection).State, harness.Row(connection).Generation));
        Assert.NotNull(harness.Row(connection).Claim);
        Assert.Equal(tokenCalls, harness.Server.TokenRequests.Count());
    }

    [Fact]
    public async Task A_renewal_that_lost_its_row_to_a_disconnect_destroys_its_new_pair()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var oldVersion = harness.Row(connection).MaterialVersion;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var newAccess = TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "2");
        harness.Server.ThenToken((_, _) =>
        {
            // The user disconnects while monday is answering.
            lock (harness.Database.Gate)
            {
                harness.Database.Authorizations[connection].State = ConnectorAuthorizationState.Revoked;
                harness.Database.Connections[connection].Enabled = false;
            }

            return Task.FromResult(FakeMondayAuthServer.Json(HttpStatusCode.OK, TestTokens.Response(newAccess, "refresh-2")));
        });

        var ended = await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel);

        Assert.Equal(ConnectorErrors.CredentialRevoked, ended.Failure!.Error);
        Assert.Equal(["refresh-2", newAccess], harness.Server.RevokeRequests.Select(request => TestTokens.Body(request).GetProperty("token").GetString()));
        Assert.Equal(
            [TokensName(harness, connection, oldVersion)],
            harness.Secrets.Names.Where(name => name.Contains(connection.ToString("N"), StringComparison.Ordinal)));
    }

    public static TheoryData<string, string> Unusable => new()
    {
        { "disabled", "connector.connection.disabled" },
        { "revoked", "connector.credential.revoked" },
        { "reauthorization required", "connector.credential.reauthorization_required" },
        { "delegating user inactive", "connector.credential.revoked" },
        { "unknown connection", "connector.credential.unavailable" },
        { "another tenant's context", "connector.credential.mismatch" },
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public async Task Access_is_refused_before_anything_is_sent_when_the_connection_may_not_be_used(string name, string code)
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var requests = harness.Server.Requests.Count;
        var context = harness.Context(connection);
        switch (name)
        {
            case "disabled":
                harness.Database.Connections[connection].Enabled = false;
                break;
            case "revoked":
                harness.Row(connection).State = ConnectorAuthorizationState.Revoked;
                break;
            case "reauthorization required":
                harness.Row(connection).State = ConnectorAuthorizationState.ReauthorizationRequired;
                break;
            case "delegating user inactive":
                harness.SetUserActive(harness.User, active: false);
                break;
            case "unknown connection":
                context = harness.Context(Guid.CreateVersion7());
                break;
            default:
                context = new MondayRequestContext(TenantId.New(), connection, Core.Correlation.CorrelationId.New());
                break;
        }

        var result = await harness.Reader.ReadItemsByIdAsync(context, ["1111"], Cancel);

        Assert.True(code == result.Failure.Error.Code, name);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task The_source_itself_refuses_a_context_of_another_tenant_before_reading_any_secret()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var requests = harness.Server.Requests.Count;
        var secretReads = harness.Secrets.Operations.Count;

        var refused = await harness.Credentials.AcquireAsync(new MondayRequestContext(TenantId.New(), connection, Core.Correlation.CorrelationId.New()), Cancel);

        Assert.Null(refused.Credential);
        Assert.Equal(ConnectorErrors.CredentialMismatch, refused.Failure!.Error);
        Assert.Equal(requests, harness.Server.Requests.Count);
        Assert.Equal(secretReads, harness.Secrets.Operations.Count);
    }

    [Fact]
    public async Task A_revocation_monday_does_not_confirm_is_recorded_as_unconfirmed()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Server.Revoke = _ => FakeMondayAuthServer.Json(HttpStatusCode.OK, """{"success":false}""");

        var disconnected = await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel);

        Assert.False(disconnected.Value.ProviderRevocationConfirmed);
        Assert.False(harness.Row(connection).RevocationConfirmed);
        Assert.Equal(ConnectorAuthorizationState.Revoked, harness.Row(connection).State);
    }

    [Fact]
    public async Task A_stored_pair_bound_to_another_connection_or_tenant_is_refused()
    {
        var harness = new OAuthHarness();
        var (first, _, _) = await harness.ConnectAsync("1");
        var (second, _, _) = await harness.ConnectAsync("2");
        var firstName = TokensName(harness, first, harness.Row(first).MaterialVersion);
        var secondName = TokensName(harness, second, harness.Row(second).MaterialVersion);

        // Connection 1's pair copied under connection 2's name, as a swapped or tampered value would be.
        harness.Secrets.Put(secondName, harness.Secrets.Peek(firstName)!);
        var requests = harness.Server.Requests.Count;

        var refused = await harness.Credentials.AcquireAsync(harness.Context(second), Cancel);

        Assert.Equal(ConnectorErrors.CredentialMismatch, refused.Failure!.Error);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("connection")]
    [InlineData("version")]
    [InlineData("subject")]
    [InlineData("account")]
    public async Task A_stored_pair_is_used_only_for_exactly_its_own_tenant_connection_version_and_identity(string field)
    {
        var harness = new OAuthHarness();
        var (first, _, _) = await harness.ConnectAsync("1");
        var (second, _, _) = await harness.ConnectAsync("2");
        var name = TokensName(harness, second, harness.Row(second).MaterialVersion);

        // Every other bound field stays right, so only the one changed field can make the value unacceptable.
        var stored = System.Text.Json.Nodes.JsonNode.Parse(harness.Secrets.Peek(name)!)!.AsObject();
        stored[field] = field switch
        {
            "tenant" => TenantId.New().Value.ToString("D"),
            "connection" => first.ToString("D"),
            "version" => harness.Row(first).MaterialVersion.ToString("D"),
            "subject" => "11111111",
            _ => "1234",
        };
        harness.Secrets.Put(name, Encoding.UTF8.GetBytes(stored.ToJsonString()));
        var requests = harness.Server.Requests.Count;

        var refused = await harness.Credentials.AcquireAsync(harness.Context(second), Cancel);

        Assert.Null(refused.Credential);
        Assert.Equal(ConnectorErrors.CredentialMismatch, refused.Failure!.Error);
        Assert.Equal(requests, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task A_missing_current_pair_requires_reauthorization_and_an_unreadable_store_changes_nothing()
    {
        var missing = new OAuthHarness();
        var (first, _, _) = await missing.ConnectAsync();
        missing.Secrets.Remove(TokensName(missing, first, missing.Row(first).MaterialVersion));
        Assert.Equal(ConnectorErrors.ReauthorizationRequired, (await missing.Credentials.AcquireAsync(missing.Context(first), Cancel)).Failure!.Error);
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, missing.Row(first).State);

        var unreadable = new OAuthHarness();
        var (second, _, _) = await unreadable.ConnectAsync();
        unreadable.Secrets.GetOverride = _ => MondaySecretStoreStatus.Unavailable;
        Assert.Equal(ConnectorErrors.CredentialUnavailable, (await unreadable.Credentials.AcquireAsync(unreadable.Context(second), Cancel)).Failure!.Error);
        Assert.Equal((ConnectorAuthorizationState.Active, 1L), (unreadable.Row(second).State, unreadable.Row(second).Generation));

        // Near expiry, an unreadable client secret releases the claim instead of stranding it.
        unreadable.Secrets.GetOverride = name => name == MondaySecretName.ClientSecret ? MondaySecretStoreStatus.Unavailable : null;
        unreadable.Clock.UtcNow += TimeSpan.FromMinutes(56);
        Assert.Equal(ConnectorErrors.CredentialUnavailable, (await unreadable.Credentials.AcquireAsync(unreadable.Context(second), Cancel)).Failure!.Error);
        Assert.Null(unreadable.Row(second).Claim);
    }

    [Fact]
    public async Task After_monday_rejects_a_token_the_client_renews_once_and_retries_with_the_new_one()
    {
        var harness = new OAuthHarness();
        var (connection, access, _) = await harness.ConnectAsync();
        harness.Server.ThenApi(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Unauthorized\"}]}");
        var (newAccess, _) = ScriptRefresh(harness, "2");
        harness.Server.ThenApi(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Reader.ReadItemsByIdAsync(harness.Context(connection), ["1111"], Cancel);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
        var reads = harness.Server.ApiRequests.Skip(1).ToList();
        Assert.Equal([access, newAccess], reads.Select(request => request.Headers["Authorization"]));
        Assert.Equal(2L, harness.Row(connection).Generation);
    }

    [Fact]
    public async Task A_second_rejection_ends_the_call_after_exactly_one_renewal()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Server.ThenApi(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Unauthorized\"}]}");
        ScriptRefresh(harness, "2");
        harness.Server.ThenApi(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Unauthorized\"}]}");
        var tokenCalls = harness.Server.TokenRequests.Count();

        var result = await harness.Reader.ReadItemsByIdAsync(harness.Context(connection), ["1111"], Cancel);

        Assert.Equal(ConnectorErrors.AuthenticationFailed, result.Failure.Error);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(tokenCalls + 1, harness.Server.TokenRequests.Count());
    }

    [Fact]
    public async Task A_renewal_another_caller_already_made_is_reused_without_calling_monday()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var stale = (await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Credential!;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        ScriptRefresh(harness, "2");
        Assert.NotNull((await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Credential);
        var tokenCalls = harness.Server.TokenRequests.Count();

        var renewed = await harness.Credentials.RenewAfterRejectionAsync(harness.Context(connection), stale, Cancel);

        Assert.Equal(harness.Row(connection).AccessExpiresAt, renewed.Credential!.ExpiresAt);
        Assert.NotEqual(stale.ExpiresAt, renewed.Credential.ExpiresAt);
        Assert.Equal(tokenCalls, harness.Server.TokenRequests.Count());
    }

    [Fact]
    public async Task A_rejected_credential_of_unknown_generation_is_renewed_exactly_once()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var identity = new ConnectorIdentity(harness.Tenant, connection, ConnectorIdentityKind.DelegatedUser, "58201774");
        var unknown = new MondayAccessCredential("some-token-not-real", identity, harness.Clock.UtcNow.AddMinutes(30));
        ScriptRefresh(harness, "2");
        var tokenCalls = harness.Server.TokenRequests.Count();

        var renewed = await harness.Credentials.RenewAfterRejectionAsync(harness.Context(connection), unknown, Cancel);

        Assert.NotNull(renewed.Credential);
        Assert.Equal(tokenCalls + 1, harness.Server.TokenRequests.Count());
        Assert.Equal(2L, harness.Row(connection).Generation);
    }

    [Fact]
    public async Task A_rejection_whose_renewal_fails_reports_the_renewals_reason()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Server.ThenApi(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Unauthorized\"}]}");
        harness.Server.ThenToken(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");

        var result = await harness.Reader.ReadItemsByIdAsync(harness.Context(connection), ["1111"], Cancel);

        Assert.Equal(ConnectorErrors.ReauthorizationRequired, result.Failure.Error);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(ConnectorAuthorizationState.ReauthorizationRequired, harness.Row(connection).State);
    }

    [Fact]
    public async Task Disconnecting_makes_the_connection_unusable_before_monday_is_asked_to_revoke_anything()
    {
        var harness = new OAuthHarness();
        var (connection, access, refresh) = await harness.ConnectAsync();
        var version = harness.Row(connection).MaterialVersion;
        var statesSeen = new List<(ConnectorAuthorizationState State, bool Enabled)>();
        harness.Server.Revoke = _ =>
        {
            lock (harness.Database.Gate)
            {
                statesSeen.Add((harness.Database.Authorizations[connection].State, harness.Database.Connections[connection].Enabled));
            }

            return FakeMondayAuthServer.Json(HttpStatusCode.OK, """{"success":true}""");
        };

        var disconnected = await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel);

        Assert.Equal(new MondayDisconnection(connection, WasAlreadyDisconnected: false, ProviderRevocationConfirmed: true), disconnected.Value);
        Assert.Equal([(ConnectorAuthorizationState.Revoked, false), (ConnectorAuthorizationState.Revoked, false)], statesSeen);
        Assert.Equal(
            [(refresh, "refresh_token"), (access, "access_token")],
            harness.Server.RevokeRequests.Select(request => TestTokens.Body(request)).Select(body => (body.GetProperty("token").GetString(), body.GetProperty("token_type_hint").GetString())));
        Assert.All(harness.Server.RevokeRequests, request =>
        {
            Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);
            Assert.Equal(OAuthHarness.ClientSecretValue, TestTokens.Body(request).GetProperty("client_secret").GetString());
        });
        Assert.Null(harness.Secrets.Peek(TokensName(harness, connection, version)));
        Assert.True(harness.Row(connection).RevocationConfirmed);
        Assert.Equal(ConnectorErrors.CredentialRevoked, (await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Failure!.Error);
    }

    [Fact]
    public async Task A_monday_outage_during_disconnect_still_leaves_the_connection_unusable_and_the_revocation_unconfirmed()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var version = harness.Row(connection).MaterialVersion;
        harness.Server.Revoke = _ => FakeMondayAuthServer.Json(HttpStatusCode.ServiceUnavailable, "{}");

        var disconnected = await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel);

        Assert.False(disconnected.Value.ProviderRevocationConfirmed);
        Assert.Equal(ConnectorAuthorizationState.Revoked, harness.Row(connection).State);
        Assert.False(harness.Row(connection).RevocationConfirmed);
        Assert.Null(harness.Secrets.Peek(TokensName(harness, connection, version)));
        Assert.Equal(ConnectorErrors.CredentialRevoked, (await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Failure!.Error);
    }

    [Fact]
    public async Task Only_the_authorizing_user_disconnects_and_a_second_disconnect_changes_nothing()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        var other = OAuthHarness.PrincipalOf(harness.AddUser(harness.Tenant));

        Assert.Equal(ConnectorAuthorizationErrors.NotPermitted, (await harness.Authorizer.DisconnectAsync(other, connection, Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.ConnectionNotFound, (await harness.Authorizer.DisconnectAsync(harness.Principal, Guid.CreateVersion7(), Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.InvalidRequest, (await harness.Authorizer.DisconnectAsync(harness.Principal, Guid.Empty, Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationErrors.UserNotEligible, (await harness.Authorizer.DisconnectAsync(Core.Identity.Principal.Anonymous, connection, Cancel)).Error);
        Assert.Equal(ConnectorAuthorizationState.Active, harness.Row(connection).State);
        Assert.Empty(harness.Server.RevokeRequests);

        Assert.True((await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel)).IsSuccess);
        var again = await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel);
        Assert.True(again.Value.WasAlreadyDisconnected);
        Assert.Equal(2, harness.Server.RevokeRequests.Count());
    }

    [Fact]
    public async Task A_pair_a_renewal_in_flight_stored_is_revoked_and_destroyed_by_the_disconnect_too()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(56);
        var (pendingAccess, pendingRefresh) = ScriptRefresh(harness, "2");
        var reads = 0;
        harness.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.CompleteRefreshAsync)
            || (operation == nameof(IConnectorAuthorizationStore.ReadAsync) && ++reads > 1);
        Assert.Null((await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Credential);
        harness.Store.FailBefore = _ => false;
        var pending = harness.Row(connection).Claim!.Value;

        var disconnected = await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel);

        Assert.True(disconnected.Value.ProviderRevocationConfirmed);
        Assert.Contains(pendingRefresh, harness.Server.RevokeRequests.Select(request => TestTokens.Body(request).GetProperty("token").GetString()));
        Assert.Contains(pendingAccess, harness.Server.RevokeRequests.Select(request => TestTokens.Body(request).GetProperty("token").GetString()));
        Assert.Null(harness.Secrets.Peek(TokensName(harness, connection, pending)));
        Assert.DoesNotContain(harness.Secrets.Names, name => name.StartsWith("wce-monday-tokens-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_restores_a_disconnected_connection()
    {
        var harness = new OAuthHarness();
        var (connection, _, _) = await harness.ConnectAsync();
        Assert.True((await harness.Authorizer.DisconnectAsync(harness.Principal, connection, Cancel)).IsSuccess);

        Assert.Equal(ConnectorAuthorizationErrors.NotReauthorizable, (await harness.Authorizer.StartAsync(harness.Principal, connection, Cancel)).Error);
        Assert.Equal(ConnectorErrors.CredentialRevoked, (await harness.Credentials.AcquireAsync(harness.Context(connection), Cancel)).Failure!.Error);
        Assert.False((await harness.Store.ReauthorizeAsync(
            new ConnectorAuthorizationGrant(connection, "monday", harness.User, "58201774", "9001", Guid.CreateVersion7(), harness.Clock.UtcNow.AddHours(1), "boards:read me:read"),
            Cancel)).Value);
    }

    private static (string Access, string Refresh) ScriptRefresh(OAuthHarness harness, string marker)
    {
        var access = TestTokens.Access(harness.Clock.UtcNow.AddHours(1), "access-" + marker);
        var refresh = "refresh-token-not-real-" + marker;
        harness.Server.ThenToken(HttpStatusCode.OK, TestTokens.Response(access, refresh));
        return (access, refresh);
    }

    private static async Task<(ConnectorCallResult<MondayItemBatch> Result, string? Authorization)> ReadAsync(OAuthHarness harness, Guid connection)
    {
        harness.Server.ThenApi(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));
        var result = await harness.Reader.ReadItemsByIdAsync(harness.Context(connection), ["1111"], Cancel);
        return (result, harness.Server.ApiRequests.Last().Headers.GetValueOrDefault("Authorization"));
    }

    private static string TokensName(OAuthHarness harness, Guid connection, Guid version) =>
        $"wce-monday-tokens-{harness.Tenant.Value:N}-{connection:N}-{version:N}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var poll = 0; poll < 500 && !condition(); poll++)
        {
            await Task.Delay(10, Cancel);
        }

        Assert.True(condition(), "The condition never held.");
    }

    private sealed class HookDelay(Func<Task> onDelay) : IMondayDelay
    {
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => await onDelay();
    }
}
