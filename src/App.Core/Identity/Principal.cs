namespace WorkContinuity.Core.Identity;

/// <summary>
/// The outcome of authentication: who the caller is, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// This type intentionally exposes no roles, permissions, tenant or authorization decision.
/// Authentication and authorization are separate concerns: a principal answers "who", while
/// <see cref="TenantMembership"/> and resource-level checks answer "may they".
/// </para>
/// <para>
/// There is also no email member. Email is contact data, not a security identifier, so it cannot
/// appear on the type that represents an authenticated caller.
/// </para>
/// </remarks>
public sealed record Principal
{
    private Principal()
    {
    }

    private Principal(UserId userId, ExternalIdentity externalIdentity)
    {
        UserId = userId;
        ExternalIdentity = externalIdentity;
        IsAuthenticated = true;
    }

    /// <summary>Gets the anonymous principal used before authentication has taken place.</summary>
    public static Principal Anonymous { get; } = new();

    /// <summary>Gets the canonical user identifier. Empty when anonymous.</summary>
    public UserId UserId { get; }

    /// <summary>Gets the external identity this principal authenticated with. Null when anonymous.</summary>
    public ExternalIdentity? ExternalIdentity { get; }

    /// <summary>Gets a value indicating whether authentication succeeded.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>Creates an authenticated principal.</summary>
    /// <exception cref="ArgumentException">The user id is empty.</exception>
    public static Principal Authenticated(UserId userId, ExternalIdentity externalIdentity)
    {
        ArgumentNullException.ThrowIfNull(externalIdentity);

        if (userId.IsEmpty)
        {
            throw new ArgumentException(
                "An authenticated principal requires a canonical user id.",
                nameof(userId));
        }

        return new Principal(userId, externalIdentity);
    }
}
