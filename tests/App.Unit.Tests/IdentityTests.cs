using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

public sealed class UserIdTests
{
    [Fact]
    public void New_produces_a_non_empty_identifier()
    {
        Assert.False(UserId.New().IsEmpty);
    }

    [Fact]
    public void From_rejects_an_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => UserId.From(Guid.Empty));
    }

    [Fact]
    public void Equal_values_are_equal()
    {
        var value = Guid.CreateVersion7();

        Assert.Equal(UserId.From(value), UserId.From(value));
    }

    [Fact]
    public void TryParse_round_trips_ToString()
    {
        var original = UserId.New();

        Assert.True(UserId.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
    }
}

public sealed class IdentityProviderIdTests
{
    [Fact]
    public void Well_known_providers_are_available()
    {
        Assert.Equal("entra-id", IdentityProviderId.EntraId.Value);
        Assert.Equal("local-demo", IdentityProviderId.LocalDemo.Value);
    }

    [Fact]
    public void Default_is_empty()
    {
        Assert.True(default(IdentityProviderId).IsEmpty);
    }

    [Fact]
    public void A_custom_provider_key_is_accepted()
    {
        Assert.Equal("okta", IdentityProviderId.From("okta").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Entra-ID")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    public void Invalid_provider_keys_are_rejected(string? candidate)
    {
        Assert.False(IdentityProviderId.TryParse(candidate, out _));
        Assert.Throws<ArgumentException>(() => IdentityProviderId.From(candidate!));
    }
}

public sealed class ExternalIdentityTests
{
    [Fact]
    public void An_external_identity_keeps_its_provider_and_subject()
    {
        var identity = new ExternalIdentity(IdentityProviderId.EntraId, "8f14e45f-ea2b-4a1f-9f0e-1c2d3e4f5a6b");

        Assert.Equal(IdentityProviderId.EntraId, identity.Provider);
        Assert.Equal("8f14e45f-ea2b-4a1f-9f0e-1c2d3e4f5a6b", identity.Subject);
    }

    [Fact]
    public void A_provider_is_required()
    {
        Assert.Throws<ArgumentException>(() => new ExternalIdentity(default, "subject-value"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\rcontrol")]
    public void An_invalid_subject_is_rejected(string subject)
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalIdentity(IdentityProviderId.EntraId, subject));
    }

    [Fact]
    public void The_same_subject_at_different_providers_is_a_different_identity()
    {
        var entra = new ExternalIdentity(IdentityProviderId.EntraId, "shared-subject");
        var demo = new ExternalIdentity(IdentityProviderId.LocalDemo, "shared-subject");

        Assert.NotEqual(entra, demo);
    }
}

public sealed class TenantMembershipTests
{
    [Fact]
    public void A_membership_exposes_its_roles()
    {
        var membership = new TenantMembership(
            TenantId.New(),
            UserId.New(),
            [PlatformRole.Employee, PlatformRole.Manager]);

        Assert.True(membership.HasRole(PlatformRole.Employee));
        Assert.True(membership.HasRole(PlatformRole.Manager));
        Assert.False(membership.HasRole(PlatformRole.TenantAdmin));
        Assert.True(membership.IsActive);
    }

    [Fact]
    public void Duplicate_and_empty_roles_are_discarded()
    {
        var membership = new TenantMembership(
            TenantId.New(),
            UserId.New(),
            [PlatformRole.Employee, PlatformRole.Employee, PlatformRole.None]);

        Assert.Single(membership.Roles);
        Assert.True(membership.HasRole(PlatformRole.Employee));
    }

    [Fact]
    public void At_least_one_real_role_is_required()
    {
        Assert.Throws<ArgumentException>(() => new TenantMembership(
            TenantId.New(),
            UserId.New(),
            [PlatformRole.None]));
    }

    [Fact]
    public void A_tenant_and_a_user_are_required()
    {
        Assert.Throws<ArgumentException>(() => new TenantMembership(
            default, UserId.New(), [PlatformRole.Employee]));

        Assert.Throws<ArgumentException>(() => new TenantMembership(
            TenantId.New(), default, [PlatformRole.Employee]));
    }

    [Fact]
    public void Every_canonical_role_is_representable()
    {
        // Employee, Manager, Tenant Admin, Auditor-Security, Internal Support, Service Identity.
        Assert.Equal(
            ["None", "Employee", "Manager", "TenantAdmin", "SecurityAuditor", "InternalSupport", "ServiceIdentity"],
            Enum.GetNames<PlatformRole>());
    }

    [Fact]
    public void An_inactive_membership_is_reported_as_inactive()
    {
        var membership = new TenantMembership(
            TenantId.New(), UserId.New(), [PlatformRole.Employee], isActive: false);

        Assert.False(membership.IsActive);
    }
}

public sealed class PrincipalTests
{
    [Fact]
    public void Anonymous_is_not_authenticated()
    {
        Assert.False(Principal.Anonymous.IsAuthenticated);
        Assert.True(Principal.Anonymous.UserId.IsEmpty);
        Assert.Null(Principal.Anonymous.ExternalIdentity);
    }

    [Fact]
    public void An_authenticated_principal_carries_its_identity()
    {
        var userId = UserId.New();
        var external = new ExternalIdentity(IdentityProviderId.EntraId, "subject-123");

        var principal = Principal.Authenticated(userId, external);

        Assert.True(principal.IsAuthenticated);
        Assert.Equal(userId, principal.UserId);
        Assert.Equal(external, principal.ExternalIdentity);
    }

    [Fact]
    public void Authentication_requires_a_canonical_user_id()
    {
        var external = new ExternalIdentity(IdentityProviderId.EntraId, "subject-123");

        Assert.Throws<ArgumentException>(() => Principal.Authenticated(default, external));
    }

    [Fact]
    public void Authentication_requires_an_external_identity()
    {
        Assert.Throws<ArgumentNullException>(() => Principal.Authenticated(UserId.New(), null!));
    }

    [Fact]
    public void A_principal_carries_no_authorization_decision()
    {
        // Authentication answers "who"; authorization is evaluated separately against membership
        // and resource permission. A principal must therefore expose no roles or permissions.
        var memberNames = typeof(Principal)
            .GetMembers()
            .Select(member => member.Name.ToUpperInvariant())
            .ToList();

        Assert.DoesNotContain(memberNames, name => name.Contains("ROLE", StringComparison.Ordinal));
        Assert.DoesNotContain(memberNames, name => name.Contains("PERMISSION", StringComparison.Ordinal));
        Assert.DoesNotContain(memberNames, name => name.Contains("AUTHORIZ", StringComparison.Ordinal));
        Assert.DoesNotContain(memberNames, name => name.Contains("TENANT", StringComparison.Ordinal));
    }
}
