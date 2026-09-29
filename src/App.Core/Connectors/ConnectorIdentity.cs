using WorkContinuity.Core.Events;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Connectors;

/// <summary>Whose authority a provider call uses.</summary>
public enum ConnectorIdentityKind
{
    /// <summary>Unspecified. Never valid: a call always names its identity.</summary>
    Unspecified = 0,

    /// <summary>An explicit Service Identity of the tenant, for a system or service workflow.</summary>
    ServiceIdentity = 1,

    /// <summary>A named user who delegated their access to the connection.</summary>
    DelegatedUser = 2,
}

/// <summary>
/// The identity under whose authority a connector calls its provider, bound to one tenant and one connection.
/// </summary>
/// <remarks>
/// <para>
/// Premium architecture §20 requires every provider call to record whose authority it uses, and Premium decision D2
/// makes that either an explicit Service Identity or a named user's own delegation, never an invented or impersonated
/// user. The identity is established when the connection is authorized and travels with the connection's credential;
/// there is no default and no anonymous identity, so a call without one cannot be made.
/// </para>
/// <para>
/// The subject is the provider's stable identifier for the principal, never an email address (invariant 47). The
/// identity carries no credential, and its string form names neither the subject nor anything secret.
/// </para>
/// </remarks>
public sealed record ConnectorIdentity
{
    /// <summary>Creates an identity.</summary>
    /// <param name="tenant">The tenant the connection belongs to.</param>
    /// <param name="connectionId">The connection the identity acts through.</param>
    /// <param name="kind">Whether the identity is a Service Identity or a delegated user.</param>
    /// <param name="subject">The provider's stable identifier for the principal.</param>
    /// <exception cref="ArgumentException">A component is missing or malformed.</exception>
    public ConnectorIdentity(TenantId tenant, Guid connectionId, ConnectorIdentityKind kind, string subject)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("A connector identity requires a tenant.", nameof(tenant));
        }

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("A connector identity requires a connection.", nameof(connectionId));
        }

        if (kind == ConnectorIdentityKind.Unspecified || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("A connector identity requires a defined kind.", nameof(kind));
        }

        if (!EventText.IsIdentifier(subject) || subject.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A connector identity's subject must be the provider's stable identifier, never an email address.",
                nameof(subject));
        }

        Tenant = tenant;
        ConnectionId = connectionId;
        Kind = kind;
        Subject = subject;
    }

    /// <summary>Gets the tenant the connection belongs to.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the connection the identity acts through.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets whether the identity is a Service Identity or a delegated user.</summary>
    public ConnectorIdentityKind Kind { get; }

    /// <summary>Gets the provider's stable identifier for the principal.</summary>
    public string Subject { get; }

    /// <summary>Whether this identity acts for the given tenant and connection.</summary>
    public bool Serves(TenantId tenant, Guid connectionId) => Tenant == tenant && ConnectionId == connectionId;

    /// <summary>Identifies the identity without its subject.</summary>
    public override string ToString() => $"ConnectorIdentity({Kind}, connection {ConnectionId:D})";
}
