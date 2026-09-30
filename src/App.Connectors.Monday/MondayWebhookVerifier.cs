using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The production monday <see cref="ISourceVerifier"/> (Gate 3C): a webhook delivery is verified by its JWT and then
/// confirmed against the connection's 3B authorization.
/// </summary>
/// <remarks>
/// <para>
/// Before any tenant is set, <see cref="VerifyAsync"/> accepts only a webhook delivery, under the connector's source and
/// schema version, whose token is valid for the audience of the connection it names; that audience binds the proof to
/// the connection. After ingress sets the tenant from the connection, <see cref="ConfirmAsync"/> verifies the token
/// again and reads the connection's authorization in that tenant through <see cref="IConnectorAuthorizationStore"/>. It
/// confirms only when the authorization belongs to the trusted tenant, this connection and this source; is active, with
/// its connection enabled and its authorizing user active, as every 3B access requires; and records exactly the monday
/// account the token names. A valid signature alone never suffices: a token of one monday account is never accepted
/// into a connection authorized for another.
/// </para>
/// <para>
/// The token's <c>userId</c> is never read as an identity and its <c>shortLivedToken</c> is never read at all. The
/// verifier writes nothing and does not log.
/// </para>
/// </remarks>
public sealed class MondayWebhookVerifier : ISourceVerifier
{
    private readonly MondayWebhookAuthenticator _authenticator;
    private readonly IConnectorAuthorizationStore _store;
    private readonly ITenantContextAccessor _tenantContextAccessor;

    /// <summary>Creates the verifier.</summary>
    /// <param name="authenticator">Checks the token with the Signing Secret.</param>
    /// <param name="store">The tenant-scoped store of connection authorizations.</param>
    /// <param name="tenantContextAccessor">The trusted tenant context that ingress sets from the connection.</param>
    public MondayWebhookVerifier(
        MondayWebhookAuthenticator authenticator,
        IConnectorAuthorizationStore store,
        ITenantContextAccessor tenantContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tenantContextAccessor);

        _authenticator = authenticator;
        _store = store;
        _tenantContextAccessor = tenantContextAccessor;
    }

    /// <inheritdoc />
    public string SourceSystem => MondayWebhooks.SourceSystem;

    /// <inheritdoc />
    public async ValueTask<bool> VerifyAsync(IngressSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!IsWebhookDelivery(submission))
        {
            return false;
        }

        var (status, _) = await _authenticator
            .VerifyAsync(submission.ConnectionId, submission.Proof, cancellationToken)
            .ConfigureAwait(false);
        return status == MondayWebhookAuthenticationStatus.Verified;
    }

    /// <inheritdoc />
    public async ValueTask<SourceConfirmation> ConfirmAsync(IngressSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!IsWebhookDelivery(submission) || !_tenantContextAccessor.Current.TryGetTenantId(out var tenant))
        {
            return SourceConfirmation.Refused;
        }

        var (status, claims) = await _authenticator
            .VerifyAsync(submission.ConnectionId, submission.Proof, cancellationToken)
            .ConfigureAwait(false);
        if (status == MondayWebhookAuthenticationStatus.Unavailable)
        {
            return SourceConfirmation.Unavailable;
        }

        if (status != MondayWebhookAuthenticationStatus.Verified || claims is null)
        {
            return SourceConfirmation.Refused;
        }

        var read = await _store.ReadAsync(submission.ConnectionId, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return SourceConfirmation.Unavailable;
        }

        return read.Value is { } authorization
            && authorization.Tenant == tenant
            && authorization.ConnectionId == submission.ConnectionId
            && string.Equals(authorization.SourceSystem, MondayWebhooks.SourceSystem, StringComparison.Ordinal)
            && authorization.State == ConnectorAuthorizationState.Active
            && authorization.ConnectionEnabled
            && authorization.CanonicalUserActive
            && string.Equals(authorization.ProviderAccount, claims.AccountId, StringComparison.Ordinal)
                ? SourceConfirmation.Confirmed
                : SourceConfirmation.Refused;
    }

    private static bool IsWebhookDelivery(IngressSubmission submission) =>
        string.Equals(submission.SourceSystem, MondayWebhooks.SourceSystem, StringComparison.Ordinal)
        && string.Equals(submission.SchemaVersion, MondayWebhooks.SchemaVersion, StringComparison.Ordinal);
}
