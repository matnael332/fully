namespace WorkContinuity.Core.Tenancy;

/// <summary>
/// Describes how a tenant identity was established. Client-supplied request data is
/// deliberately absent: there is no value that represents "the caller told us".
/// </summary>
public enum TenantResolutionSource
{
    /// <summary>No tenant has been resolved. This is the only safe default.</summary>
    None = 0,

    /// <summary>Resolved server-side from a validated authenticated principal.</summary>
    AuthenticatedPrincipal = 1,

    /// <summary>Resolved server-side from a trusted internal service or worker identity.</summary>
    TrustedServiceIdentity = 2,
}

/// <summary>
/// Request- or operation-scoped tenant security context.
/// </summary>
public interface ITenantContext
{
    /// <summary>Gets a value indicating whether a tenant has been resolved server-side.</summary>
    bool IsResolved { get; }

    /// <summary>Gets the trusted mechanism that resolved the tenant.</summary>
    TenantResolutionSource Source { get; }

    /// <summary>
    /// Gets the resolved tenant. Returns <see langword="false"/> when no tenant has been
    /// resolved, forcing callers to handle the unresolved case rather than reading a default.
    /// </summary>
    bool TryGetTenantId(out TenantId tenantId);
}

/// <summary>
/// Immutable <see cref="ITenantContext"/> implementation.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private readonly TenantId _tenantId;

    private TenantContext() => Source = TenantResolutionSource.None;

    private TenantContext(TenantId tenantId, TenantResolutionSource source)
    {
        _tenantId = tenantId;
        Source = source;
    }

    /// <summary>Gets the shared unresolved context used until authentication resolves a tenant.</summary>
    public static ITenantContext Unresolved { get; } = new TenantContext();

    /// <inheritdoc />
    public bool IsResolved => Source != TenantResolutionSource.None;

    /// <inheritdoc />
    public TenantResolutionSource Source { get; }

    /// <inheritdoc />
    public bool TryGetTenantId(out TenantId tenantId)
    {
        tenantId = IsResolved ? _tenantId : default;
        return IsResolved;
    }

    /// <summary>
    /// Creates a resolved tenant context. Callers must name the trusted server-side mechanism
    /// that established the tenant.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The source is <see cref="TenantResolutionSource.None"/> or the tenant id is empty.
    /// </exception>
    public static ITenantContext Resolved(TenantId tenantId, TenantResolutionSource source)
    {
        if (source == TenantResolutionSource.None)
        {
            throw new ArgumentException(
                "A resolved tenant context requires a trusted resolution source.",
                nameof(source));
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException(
                "A resolved tenant context requires a non-empty tenant id.",
                nameof(tenantId));
        }

        return new TenantContext(tenantId, source);
    }
}
