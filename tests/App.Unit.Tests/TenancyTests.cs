using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

public sealed class TenantIdTests
{
    [Fact]
    public void New_produces_a_non_empty_identifier()
    {
        var tenantId = TenantId.New();

        Assert.False(tenantId.IsEmpty);
        Assert.NotEqual(Guid.Empty, tenantId.Value);
    }

    [Fact]
    public void New_produces_distinct_identifiers()
    {
        Assert.NotEqual(TenantId.New(), TenantId.New());
    }

    [Fact]
    public void Default_is_empty()
    {
        Assert.True(default(TenantId).IsEmpty);
    }

    [Fact]
    public void From_rejects_an_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => TenantId.From(Guid.Empty));
    }

    [Fact]
    public void Equal_values_are_equal_and_share_a_hash_code()
    {
        var value = Guid.CreateVersion7();

        var left = TenantId.From(value);
        var right = TenantId.From(value);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Different_values_are_not_equal()
    {
        var left = TenantId.From(Guid.CreateVersion7());
        var right = TenantId.From(Guid.CreateVersion7());

        Assert.NotEqual(left, right);
        Assert.True(left != right);
    }

    [Fact]
    public void TryParse_round_trips_ToString()
    {
        var original = TenantId.New();

        Assert.True(TenantId.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryParse_rejects_invalid_input(string? candidate)
    {
        Assert.False(TenantId.TryParse(candidate, out var tenantId));
        Assert.True(tenantId.IsEmpty);
    }
}

public sealed class TenantContextTests
{
    [Fact]
    public void Unresolved_context_exposes_no_tenant()
    {
        var context = TenantContext.Unresolved;

        Assert.False(context.IsResolved);
        Assert.Equal(TenantResolutionSource.None, context.Source);
        Assert.False(context.TryGetTenantId(out var tenantId));
        Assert.True(tenantId.IsEmpty);
    }

    [Fact]
    public void Resolved_context_exposes_its_tenant_and_source()
    {
        var expected = TenantId.New();

        var context = TenantContext.Resolved(expected, TenantResolutionSource.AuthenticatedPrincipal);

        Assert.True(context.IsResolved);
        Assert.Equal(TenantResolutionSource.AuthenticatedPrincipal, context.Source);
        Assert.True(context.TryGetTenantId(out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Resolution_requires_a_trusted_source()
    {
        Assert.Throws<ArgumentException>(
            () => TenantContext.Resolved(TenantId.New(), TenantResolutionSource.None));
    }

    [Fact]
    public void Resolution_requires_a_non_empty_tenant()
    {
        Assert.Throws<ArgumentException>(
            () => TenantContext.Resolved(default, TenantResolutionSource.AuthenticatedPrincipal));
    }

    [Fact]
    public void The_resolution_source_enum_has_no_client_supplied_option()
    {
        // A value meaning "the caller told us which tenant they are" must not exist, so no code
        // can resolve a tenant from request data and still name a legitimate source.
        var names = Enum.GetNames<TenantResolutionSource>();

        Assert.Equal(
            ["None", "AuthenticatedPrincipal", "TrustedServiceIdentity"],
            names);
    }
}

public sealed class TenantContextAccessorTests
{
    [Fact]
    public async Task Current_defaults_to_unresolved()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new TenantContextAccessor();

            Assert.False(accessor.Current.IsResolved);
        });
    }

    [Fact]
    public async Task SetResolved_makes_the_tenant_visible_in_scope()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new TenantContextAccessor();
            var expected = TenantId.New();

            accessor.SetResolved(
                TenantContext.Resolved(expected, TenantResolutionSource.AuthenticatedPrincipal));

            Assert.True(accessor.Current.TryGetTenantId(out var actual));
            Assert.Equal(expected, actual);
        });
    }

    [Fact]
    public async Task A_resolved_tenant_context_is_immutable()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new TenantContextAccessor();
            accessor.SetResolved(
                TenantContext.Resolved(TenantId.New(), TenantResolutionSource.AuthenticatedPrincipal));

            Assert.Throws<InvalidOperationException>(() => accessor.SetResolved(
                TenantContext.Resolved(TenantId.New(), TenantResolutionSource.AuthenticatedPrincipal)));
        });
    }

    [Fact]
    public async Task An_unresolved_context_cannot_be_assigned()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new TenantContextAccessor();

            Assert.Throws<ArgumentException>(() => accessor.SetResolved(TenantContext.Unresolved));
        });
    }

    [Fact]
    public async Task One_scope_cannot_observe_another_scopes_tenant()
    {
        var accessor = new TenantContextAccessor();

        await Scope.RunAsync(() => accessor.SetResolved(
            TenantContext.Resolved(TenantId.New(), TenantResolutionSource.AuthenticatedPrincipal)));

        // The sibling scope above has ended; its tenant must not leak into this one.
        await Scope.RunAsync(() => Assert.False(accessor.Current.IsResolved));
    }
}
