namespace WorkContinuity.Core.Tenancy;

/// <summary>
/// Provides ambient access to the tenant context of the current request or operation scope.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>
    /// Gets the current tenant context. Never <see langword="null"/>; defaults to
    /// <see cref="TenantContext.Unresolved"/>.
    /// </summary>
    ITenantContext Current { get; }

    /// <summary>
    /// Assigns the tenant resolved for the current scope. May be called at most once per scope:
    /// once resolved, the tenant context is immutable.
    /// </summary>
    /// <exception cref="ArgumentException">The supplied context is not resolved.</exception>
    /// <exception cref="InvalidOperationException">The scope already has a resolved tenant.</exception>
    void SetResolved(ITenantContext tenantContext);
}

/// <summary>
/// Flow-scoped <see cref="ITenantContextAccessor"/>. Values assigned inside a scope are visible to
/// nested work and are discarded when the scope ends, so one request can never observe another's
/// tenant.
/// </summary>
public sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<ITenantContext?> Scope = new();

    /// <inheritdoc />
    public ITenantContext Current => Scope.Value ?? TenantContext.Unresolved;

    /// <inheritdoc />
    public void SetResolved(ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (!tenantContext.IsResolved)
        {
            throw new ArgumentException(
                "Only a resolved tenant context may be assigned to a scope.",
                nameof(tenantContext));
        }

        if (Scope.Value is { IsResolved: true })
        {
            throw new InvalidOperationException(
                "The tenant context for this scope is already resolved and cannot be reassigned.");
        }

        Scope.Value = tenantContext;
    }
}
