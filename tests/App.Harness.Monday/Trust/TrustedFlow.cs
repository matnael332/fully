using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Configuration;

namespace WorkContinuity.Harness.Monday.Trust;

/// <summary>
/// The trusted chain the harness supplies (R3B-1): the configured tenant and the canonical user who owns the delegation.
/// </summary>
/// <remarks>
/// <para>
/// Both come from <see cref="HarnessOptions"/>, which reads them once from server-side configuration. Nothing else in
/// the harness can construct this type, so no request, console argument or provider value can become the tenant or the
/// user.
/// </para>
/// <para>
/// The operator's identity provider is the harness's own, named for what it is (owner decision B-12): an owner-operated
/// DEV/TEST harness whose console, loopback listener and browser binding stand for the owner acting as the configured
/// canonical user. It is never the production trust model, which Gate 7's Entra sign-in provides, and it is not the
/// local demo provider, because a real delegation is not a demonstration account.
/// </para>
/// </remarks>
public sealed class HarnessTrust
{
    /// <summary>The key of the harness's own DEV/TEST identity provider (owner decision B-12). An identifier, not a credential.</summary>
    public const string OperatorProviderKey = "wce-dev-harness";

    /// <summary>The subject of the operator's local identity. An identifier, not a credential.</summary>
    public const string OperatorSubject = "wce-harness-operator";

    internal HarnessTrust(HarnessOptions options)
    {
        Tenant = options.Tenant;
        Operator = Principal.Authenticated(
            options.CanonicalUser,
            new ExternalIdentity(IdentityProviderId.From(OperatorProviderKey), OperatorSubject));
    }

    /// <summary>Gets the trusted tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the operator: the configured canonical user, authenticated by the harness's own configuration.</summary>
    public Principal Operator { get; }

    /// <summary>Names neither the tenant nor the user.</summary>
    public override string ToString() => "HarnessTrust(redacted)";
}

/// <summary>
/// Runs one operator action or one cleanup tick as the trusted tenant: the harness's only tenant assignment.
/// </summary>
/// <remarks>
/// <para>
/// Every run gets an execution flow of its own and a dependency scope of its own (owner decision D3G-3). The flow is
/// started with the caller's execution context suppressed, so nothing the caller carries flows in, and the tenant
/// assigned here is visible to that run alone and is gone when it ends: no run observes another run's tenant, and
/// the relay, the consumer and webhook requests, which resolve their own tenants, never see this one.
/// </para>
/// <para>
/// The tenant is always the configured one. There is no overload that takes a tenant, so a caller cannot choose which
/// tenant to act as. This is the one reviewed call site of <c>SetResolved</c> in the harness, and it names the
/// trusted-service source, as the production callers do.
/// </para>
/// </remarks>
public sealed class TrustedFlow
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly HarnessTrust _trust;

    /// <summary>Creates the flow.</summary>
    /// <param name="scopes">Creates each run's dependency scope.</param>
    /// <param name="tenantContext">The flow-scoped tenant accessor the composed services read.</param>
    /// <param name="trust">The configured tenant and canonical user.</param>
    public TrustedFlow(IServiceScopeFactory scopes, ITenantContextAccessor tenantContext, HarnessTrust trust)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(trust);

        _scopes = scopes;
        _tenantContext = tenantContext;
        _trust = trust;
    }

    /// <summary>Runs work in its own flow and scope, as the trusted tenant.</summary>
    /// <typeparam name="TResult">What the work returns.</typeparam>
    /// <param name="work">The work, given the run's services and the trusted chain.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the work returned.</returns>
    public Task<TResult> RunAsync<TResult>(
        Func<IServiceProvider, HarnessTrust, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        // A new, empty flow: whatever the caller's flow carries stays behind.
        return DetachedFlow.Run(() => RunScopedAsync(work, cancellationToken));
    }

    private async Task<TResult> RunScopedAsync<TResult>(
        Func<IServiceProvider, HarnessTrust, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        _tenantContext.SetResolved(TenantContext.Resolved(_trust.Tenant, TenantResolutionSource.TrustedServiceIdentity));
        return await work(scope.ServiceProvider, _trust, cancellationToken).ConfigureAwait(false);
    }
}
