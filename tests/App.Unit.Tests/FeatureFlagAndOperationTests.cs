using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Features;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Operations;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Unit.Tests;

public sealed class FeatureFlagTests
{
    [Fact]
    public void Learned_auto_execution_is_off_by_default()
    {
        IFeatureFlagProvider provider = new InMemoryFeatureFlagProvider();

        Assert.False(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning));
        Assert.False(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning, TenantId.New()));
    }

    [Fact]
    public void The_canonical_flag_key_is_stable()
    {
        Assert.Equal("AUTO_EXECUTION_FROM_LEARNING", FeatureFlags.AutoExecutionFromLearning);
        Assert.Contains(FeatureFlags.AutoExecutionFromLearning, FeatureFlags.All);
    }

    [Fact]
    public void Every_canonical_flag_is_off_by_default()
    {
        IFeatureFlagProvider provider = new InMemoryFeatureFlagProvider();

        Assert.All(FeatureFlags.All, flag => Assert.False(provider.IsEnabled(flag)));
    }

    [Fact]
    public void An_unknown_flag_is_off()
    {
        IFeatureFlagProvider provider = new InMemoryFeatureFlagProvider();

        Assert.False(provider.IsEnabled("NEVER_CONFIGURED_FLAG"));
    }

    [Fact]
    public void A_global_flag_can_be_enabled()
    {
        var provider = new InMemoryFeatureFlagProvider();

        provider.SetGlobal(FeatureFlags.AutoExecutionFromLearning, enabled: true);

        Assert.True(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning));
    }

    [Fact]
    public void A_tenant_override_beats_the_global_value()
    {
        var provider = new InMemoryFeatureFlagProvider();
        var enabledTenant = TenantId.New();
        var otherTenant = TenantId.New();

        provider.SetGlobal(FeatureFlags.AutoExecutionFromLearning, enabled: false);
        provider.SetForTenant(FeatureFlags.AutoExecutionFromLearning, enabledTenant, enabled: true);

        Assert.True(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning, enabledTenant));
        Assert.False(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning, otherTenant));
        Assert.False(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning));
    }

    [Fact]
    public void A_tenant_without_an_override_falls_back_to_the_global_value()
    {
        var provider = new InMemoryFeatureFlagProvider();

        provider.SetGlobal(FeatureFlags.AutoExecutionFromLearning, enabled: true);

        Assert.True(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning, TenantId.New()));
    }

    [Fact]
    public void A_tenant_override_can_disable_a_globally_enabled_flag()
    {
        var provider = new InMemoryFeatureFlagProvider();
        var tenant = TenantId.New();

        provider.SetGlobal(FeatureFlags.AutoExecutionFromLearning, enabled: true);
        provider.SetForTenant(FeatureFlags.AutoExecutionFromLearning, tenant, enabled: false);

        Assert.False(provider.IsEnabled(FeatureFlags.AutoExecutionFromLearning, tenant));
    }

    [Fact]
    public void A_tenant_scoped_flag_requires_a_tenant()
    {
        var provider = new InMemoryFeatureFlagProvider();

        Assert.Throws<ArgumentException>(
            () => provider.SetForTenant(FeatureFlags.AutoExecutionFromLearning, default, true));
    }

    [Fact]
    public void A_flag_key_is_required()
    {
        var provider = new InMemoryFeatureFlagProvider();

        Assert.Throws<ArgumentException>(() => provider.IsEnabled("   "));
        Assert.Throws<ArgumentNullException>(() => provider.IsEnabled(null!));
    }
}

public sealed class OperationContextTests
{
    [Fact]
    public void An_operation_context_composes_the_validated_primitives()
    {
        var tenantId = TenantId.New();
        var tenant = TenantContext.Resolved(tenantId, TenantResolutionSource.AuthenticatedPrincipal);
        var principal = Principal.Authenticated(
            UserId.New(),
            new ExternalIdentity(IdentityProviderId.EntraId, "subject-1"));
        var correlation = CorrelationContext.Generate();
        var instant = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var operation = OperationContext.Start(tenant, principal, correlation, new FixedClock(instant));

        Assert.True(operation.Tenant.TryGetTenantId(out var actualTenant));
        Assert.Equal(tenantId, actualTenant);
        Assert.Equal(principal, operation.Principal);
        Assert.Equal(correlation.CorrelationId, operation.CorrelationId);
        Assert.Equal(instant, operation.StartedAtUtc);
    }

    [Fact]
    public void An_operation_context_may_be_unresolved_and_anonymous()
    {
        var operation = OperationContext.Start(
            TenantContext.Unresolved,
            Principal.Anonymous,
            CorrelationContext.Generate(),
            new SystemClock());

        Assert.False(operation.Tenant.IsResolved);
        Assert.False(operation.Principal.IsAuthenticated);
        Assert.False(operation.CorrelationId.IsEmpty);
    }

    [Fact]
    public void The_start_time_comes_from_the_clock_abstraction()
    {
        var instant = new DateTimeOffset(2030, 12, 31, 23, 59, 58, TimeSpan.Zero);

        var operation = OperationContext.Start(
            TenantContext.Unresolved,
            Principal.Anonymous,
            CorrelationContext.Generate(),
            new FixedClock(instant));

        Assert.Equal(instant, operation.StartedAtUtc);
        Assert.Equal(TimeSpan.Zero, operation.StartedAtUtc.Offset);
    }

    [Fact]
    public void All_collaborators_are_required()
    {
        var correlation = CorrelationContext.Generate();
        IClock clock = new SystemClock();

        Assert.Throws<ArgumentNullException>(
            () => OperationContext.Start(null!, Principal.Anonymous, correlation, clock));
        Assert.Throws<ArgumentNullException>(
            () => OperationContext.Start(TenantContext.Unresolved, null!, correlation, clock));
        Assert.Throws<ArgumentNullException>(
            () => OperationContext.Start(TenantContext.Unresolved, Principal.Anonymous, null!, clock));
        Assert.Throws<ArgumentNullException>(
            () => OperationContext.Start(TenantContext.Unresolved, Principal.Anonymous, correlation, null!));
    }

    [Fact]
    public void A_correlation_id_is_required()
    {
        Assert.Throws<ArgumentException>(() => OperationContext.Start(
            TenantContext.Unresolved,
            Principal.Anonymous,
            new EmptyCorrelationContext(),
            new SystemClock()));
    }

    [Fact]
    public void The_context_stays_small_and_does_not_become_a_service_locator()
    {
        var properties = typeof(IOperationContext).GetProperties();

        Assert.Equal(4, properties.Length);
        Assert.DoesNotContain(typeof(IOperationContext).GetMethods(), method => !method.IsSpecialName);
    }

    private sealed class FixedClock(DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow { get; } = instant;
    }

    private sealed class EmptyCorrelationContext : ICorrelationContext
    {
        public CorrelationId CorrelationId => default;

        public bool WasPropagated => false;
    }
}
