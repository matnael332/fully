using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// Supplies the credential for one attempt of one monday call, checking the connection's state at that moment.
/// </summary>
/// <remarks>
/// <para>
/// The client asks for the credential before every attempt, retries included, and never caches it across attempts.
/// Disabling a connection or revoking its token therefore takes effect at the next attempt, at access time rather than
/// only at ingestion (invariant 58, Premium architecture §20).
/// </para>
/// <para>
/// An implementation must fail closed: when the connection is disabled, when its credential was revoked, or when the
/// state or the secret cannot be read, it returns a failure and never a credential. It returns only a credential that
/// belongs to the requesting tenant and connection; the client checks that as well. The OAuth 2.1 connection lifecycle
/// (sub-gate 3B) implements this port in <see cref="MondayConnectionCredentialSource"/>, which keeps the secret in
/// <see cref="IMondaySecretStore"/> and renews it single-flight.
/// </para>
/// </remarks>
public interface IMondayCredentialSource
{
    /// <summary>Acquires the credential for one attempt.</summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The credential, or the reason none may be used.</returns>
    ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Renews the credential after monday rejected it (401), as monday documents. The client asks at most once per call.
    /// An implementation that cannot renew returns <see cref="MondayCredentialResult.NotRenewed"/>, and the same
    /// access-time checks as <see cref="AcquireAsync"/> apply to whatever it returns.
    /// </summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="rejected">The credential monday rejected.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>A renewed credential for the next attempt, or the reason there is none.</returns>
    ValueTask<MondayCredentialResult> RenewAfterRejectionAsync(
        MondayRequestContext context,
        MondayAccessCredential rejected,
        CancellationToken cancellationToken);
}

/// <summary>The outcome of acquiring a credential: the credential, or the fail-closed reason there is none.</summary>
public sealed class MondayCredentialResult
{
    private MondayCredentialResult(MondayAccessCredential? credential, ConnectorFailure? failure)
    {
        Credential = credential;
        Failure = failure;
    }

    /// <summary>Gets the credential, when one was acquired.</summary>
    public MondayAccessCredential? Credential { get; }

    /// <summary>Gets the reason no credential may be used, when there is none.</summary>
    public ConnectorFailure? Failure { get; }

    /// <summary>A usable credential.</summary>
    /// <exception cref="ArgumentNullException">The credential is null.</exception>
    public static MondayCredentialResult Acquired(MondayAccessCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new MondayCredentialResult(credential, failure: null);
    }

    /// <summary>The connection is disabled.</summary>
    public static MondayCredentialResult ConnectionDisabled() =>
        Refused(ConnectorFailureKind.ConnectionDisabled, ConnectorErrors.ConnectionDisabled);

    /// <summary>The connection's credential was revoked.</summary>
    public static MondayCredentialResult Revoked() =>
        Refused(ConnectorFailureKind.AuthenticationFailed, ConnectorErrors.CredentialRevoked);

    /// <summary>The connection's state or secret could not be read, or no credential exists.</summary>
    public static MondayCredentialResult Unavailable() =>
        Refused(ConnectorFailureKind.CredentialUnavailable, ConnectorErrors.CredentialUnavailable);

    /// <summary>The rejected credential could not be renewed; the provider's rejection stands.</summary>
    public static MondayCredentialResult NotRenewed() =>
        Refused(ConnectorFailureKind.AuthenticationFailed, ConnectorErrors.AuthenticationFailed);

    /// <summary>The connection's credential can no longer be trusted or renewed; its user must authorize it again.</summary>
    public static MondayCredentialResult ReauthorizationRequired() =>
        Refused(ConnectorFailureKind.AuthenticationFailed, ConnectorErrors.ReauthorizationRequired);

    /// <summary>The stored credential or its state belongs to another tenant, connection or identity.</summary>
    public static MondayCredentialResult Mismatch() =>
        Refused(ConnectorFailureKind.CredentialUnavailable, ConnectorErrors.CredentialMismatch);

    /// <summary>A renewal failed transiently and changed nothing; a later call may succeed.</summary>
    internal static MondayCredentialResult Deferred(ConnectorFailure failure) =>
        failure.IsTransient
            ? new MondayCredentialResult(credential: null, failure)
            : throw new ArgumentException("A deferred renewal reports a transient failure.", nameof(failure));

    /// <summary>Identifies the outcome; a credential is never rendered.</summary>
    public override string ToString() => Credential is not null
        ? "MondayCredentialResult(acquired)"
        : $"MondayCredentialResult({Failure!.Error.Code})";

    private static MondayCredentialResult Refused(ConnectorFailureKind kind, Error error) =>
        new(credential: null, new ConnectorFailure(kind, error));
}
