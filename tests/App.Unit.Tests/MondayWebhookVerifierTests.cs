using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3C production monday source verifier: before any tenant is set it verifies a webhook delivery's token for the
/// connection the delivery names; after ingress sets the tenant it confirms the token's monday account against the
/// connection's 3B authorization in that tenant. A valid signature alone never suffices.
/// </summary>
public sealed class MondayWebhookVerifierTests
{
    private readonly VerifierHarness _harness = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_valid_token_for_the_named_connection_verifies_and_is_confirmed_for_its_recorded_account()
    {
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));
        var verifier = _harness.Verifier();

        Assert.Equal("monday", verifier.SourceSystem);
        Assert.True(await verifier.VerifyAsync(delivery, Token));
        Assert.Equal(SourceConfirmation.Confirmed, await verifier.ConfirmAsync(delivery, Token));
    }

    [Fact]
    public async Task A_token_of_monday_account_a_is_never_confirmed_for_a_connection_authorized_for_account_b()
    {
        // Signed with the right secret, for the right connection's audience, but naming another monday account.
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection, WebhookTokens.OtherAccount));
        var verifier = _harness.Verifier();

        Assert.True(await verifier.VerifyAsync(delivery, Token));
        Assert.Equal(SourceConfirmation.Refused, await verifier.ConfirmAsync(delivery, Token));

        // And the other way round: a connection of the same tenant authorized for account B refuses account A's token.
        var accountB = _harness.AddConnection(WebhookTokens.OtherAccount);
        var intoB = _harness.Delivery(WebhookTokens.Valid(accountB, WebhookTokens.Account), accountB);
        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(intoB, Token));
        Assert.Equal(
            SourceConfirmation.Confirmed,
            await _harness.Verifier().ConfirmAsync(_harness.Delivery(WebhookTokens.Valid(accountB, WebhookTokens.OtherAccount), accountB), Token));
    }

    [Fact]
    public async Task The_monday_user_in_the_token_is_not_an_identity_and_decides_nothing()
    {
        // The user who triggered the event is provider actor information; it need not be the delegating user.
        foreach (var user in new[] { "58201774", "4242", "\"someone@example.test\"", "null" })
        {
            var delivery = _harness.Delivery(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, userId: user)));

            Assert.Equal(SourceConfirmation.Confirmed, await _harness.Verifier().ConfirmAsync(delivery, Token));
        }
    }

    [Theory]
    [InlineData(ConnectorAuthorizationState.ReauthorizationRequired)]
    [InlineData(ConnectorAuthorizationState.Revoked)]
    public async Task An_authorization_that_is_not_active_is_not_confirmed(ConnectorAuthorizationState state)
    {
        _harness.Authorization.State = state;
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));

        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));
    }

    [Fact]
    public async Task A_disabled_connection_or_an_inactive_authorizing_user_is_not_confirmed()
    {
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));

        _harness.Database.Connections[_harness.Connection].Enabled = false;
        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));

        _harness.Database.Connections[_harness.Connection].Enabled = true;
        _harness.Database.Users[_harness.User] = (_harness.Tenant, false);
        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));
    }

    [Fact]
    public async Task A_connection_without_an_authorization_in_the_trusted_tenant_is_not_confirmed()
    {
        var elsewhere = Guid.CreateVersion7();
        var delivery = _harness.Delivery(WebhookTokens.Valid(elsewhere), elsewhere);

        Assert.True(await _harness.Verifier().VerifyAsync(delivery, Token));
        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));

        // The same connection read under another tenant finds nothing: the store is bound to the trusted tenant.
        var other = TenantContext.Resolved(TenantId.New(), TenantResolutionSource.TrustedServiceIdentity);
        var ownDelivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));
        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier(other).ConfirmAsync(ownDelivery, Token));
    }

    [Fact]
    public async Task Without_a_resolved_tenant_nothing_is_confirmed_and_nothing_is_read()
    {
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));

        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier(TenantContext.Unresolved).ConfirmAsync(delivery, Token));
        Assert.Empty(_harness.Store.Calls);
    }

    [Fact]
    public async Task An_unreadable_store_or_signing_secret_makes_the_confirmation_unavailable_not_refused()
    {
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));

        _harness.Store.FailBefore = operation => operation == nameof(IConnectorAuthorizationStore.ReadAsync);
        Assert.Equal(SourceConfirmation.Unavailable, await _harness.Verifier().ConfirmAsync(delivery, Token));
        _harness.Store.FailBefore = _ => false;

        _harness.Webhooks.Secrets.GetOverride = _ => MondaySecretStoreStatus.Unavailable;
        Assert.Equal(SourceConfirmation.Unavailable, await _harness.Verifier().ConfirmAsync(delivery, Token));
        Assert.False(await _harness.Verifier().VerifyAsync(delivery, Token));
    }

    [Fact]
    public async Task An_invalid_token_is_neither_verified_nor_confirmed_and_the_store_is_not_read()
    {
        var connection = _harness.Connection;
        foreach (var token in new[]
                 {
                     null,
                     string.Empty,
                     "not a token",
                     WebhookTokens.Mint(WebhookTokens.Claims(connection), WebhookTokens.OtherSecret),
                     WebhookTokens.Valid(Guid.CreateVersion7()),
                     WebhookTokens.WithClaims(WebhookTokens.Valid(connection), WebhookTokens.Claims(connection, WebhookTokens.OtherAccount)),
                 })
        {
            var delivery = _harness.Delivery(token);

            Assert.False(await _harness.Verifier().VerifyAsync(delivery, Token));
            Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));
        }

        Assert.Empty(_harness.Store.Calls);
    }

    [Fact]
    public async Task Only_a_monday_webhook_delivery_is_verified_or_confirmed()
    {
        var token = WebhookTokens.Valid(_harness.Connection);

        foreach (var delivery in new[]
                 {
                     _harness.Delivery(token, sourceSystem: "synthetic"),
                     _harness.Delivery(token, schemaVersion: "monday.webhook.v2"),
                     _harness.Delivery(token, schemaVersion: "1"),
                 })
        {
            Assert.False(await _harness.Verifier().VerifyAsync(delivery, Token));
            Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => _harness.Verifier().VerifyAsync(null!, Token).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => _harness.Verifier().ConfirmAsync(null!, Token).AsTask());
    }

    [Fact]
    public async Task Confirming_only_reads_the_connections_own_authorization()
    {
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));

        Assert.Equal(SourceConfirmation.Confirmed, await _harness.Verifier().ConfirmAsync(delivery, Token));

        Assert.Equal([nameof(IConnectorAuthorizationStore.ReadAsync)], _harness.Store.Calls);
        Assert.DoesNotContain(_harness.Webhooks.Secrets.Operations, operation => !operation.StartsWith("get ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_that_expires_between_verification_and_confirmation_is_not_confirmed()
    {
        var delivery = _harness.Delivery(WebhookTokens.Valid(_harness.Connection));

        Assert.True(await _harness.Verifier().VerifyAsync(delivery, Token));

        _harness.Webhooks.Clock.UtcNow = WebhookTokens.Now.AddMinutes(6);
        Assert.Equal(SourceConfirmation.Refused, await _harness.Verifier().ConfirmAsync(delivery, Token));
    }
}
