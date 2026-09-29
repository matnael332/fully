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
/// belongs to the requesting tenant and connection; the client checks that as well. Where the secret is stored, and how
/// it is refreshed, belong to the OAuth 2.1 connection lifecycle (sub-gate 3B), which implements this port.
/// </para>
/// </remarks>
public interface IMondayCredentialSource
{
    /// <summary>Acquires the credential for one attempt.</summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The credential, or the reason none may be used.</returns>
    ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken);
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

    /// <summary>Identifies the outcome; a credential is never rendered.</summary>
    public override string ToString() => Credential is not null
        ? "MondayCredentialResult(acquired)"
        : $"MondayCredentialResult({Failure!.Error.Code})";

    private static MondayCredentialResult Refused(ConnectorFailureKind kind, Error error) =>
        new(credential: null, new ConnectorFailure(kind, error));
}
