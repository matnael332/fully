using System.Reflection;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Email is contact data, not a security identifier: it is mutable, reassignable and controlled by
/// the identity provider. These tests assert that no Core type can be keyed on it.
/// </summary>
public sealed class EmailIsNotASecurityIdentifierTests
{
    private static readonly string[] ForbiddenFragments = ["EMAIL", "MAIL", "UPN", "USERPRINCIPALNAME"];

    private static IEnumerable<Type> CoreTypes() => typeof(UserId).Assembly
        .GetTypes()
        .Where(type => type.IsPublic);

    [Fact]
    public void No_core_type_exposes_an_email_member()
    {
        var offenders = new List<string>();

        foreach (var type in CoreTypes())
        {
            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (IsForbidden(member.Name))
                {
                    offenders.Add($"{type.FullName}.{member.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_core_constructor_or_method_accepts_an_email_parameter()
    {
        var offenders = new List<string>();

        foreach (var type in CoreTypes())
        {
            var parameters = type.GetConstructors().Cast<MethodBase>()
                .Concat(type.GetMethods())
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => (Method: method.Name, Parameter: parameter.Name ?? string.Empty)));

            foreach (var (method, parameter) in parameters)
            {
                if (IsForbidden(parameter))
                {
                    offenders.Add($"{type.FullName}.{method}({parameter})");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_identity_types_are_keyed_on_a_canonical_id_and_a_provider_subject()
    {
        // Identity is established by the canonical UserId plus the provider's immutable subject.
        var identity = new ExternalIdentity(IdentityProviderId.EntraId, "provider-object-id");
        var principal = Principal.Authenticated(UserId.New(), identity);

        Assert.False(principal.UserId.IsEmpty);
        Assert.Equal("provider-object-id", principal.ExternalIdentity!.Subject);
    }

    [Fact]
    public void An_email_shaped_value_is_not_a_valid_provider_key()
    {
        // A provider key is an allowlisted slug, so an address cannot be smuggled in as one.
        Assert.False(IdentityProviderId.TryParse("person@example.com", out _));
    }

    [Fact]
    public void Two_users_sharing_a_provider_subject_string_remain_distinct_identities()
    {
        var subject = "collision-subject";
        var first = Principal.Authenticated(
            UserId.New(), new ExternalIdentity(IdentityProviderId.EntraId, subject));
        var second = Principal.Authenticated(
            UserId.New(), new ExternalIdentity(IdentityProviderId.EntraId, subject));

        // The canonical id is the identity key, so a duplicated provider string cannot merge users.
        Assert.NotEqual(first.UserId, second.UserId);
        Assert.NotEqual(first, second);
    }

    private static bool IsForbidden(string name)
    {
        var upper = name.ToUpperInvariant();

        return ForbiddenFragments.Any(fragment => upper.Contains(fragment, StringComparison.Ordinal));
    }
}

/// <summary>
/// Authentication answers "who"; authorization answers "may they". These tests assert the two stay
/// separate types so being authenticated can never imply access.
/// </summary>
public sealed class AuthenticationIsNotAuthorizationTests
{
    [Fact]
    public void A_principal_carries_no_tenant_and_no_roles()
    {
        // Instance members only: the static Anonymous factory is not state a principal carries.
        var propertyNames = typeof(Principal)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(["UserId", "ExternalIdentity", "IsAuthenticated"], propertyNames);
    }

    [Fact]
    public void Authorization_input_lives_on_membership_not_on_the_principal()
    {
        var membership = new TenantMembership(
            TenantId.New(), UserId.New(), [PlatformRole.Employee]);

        Assert.True(membership.HasRole(PlatformRole.Employee));
        Assert.DoesNotContain(
            typeof(Principal).GetMethods(),
            method => method.Name.Contains("Role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_authenticated_principal_alone_resolves_no_tenant()
    {
        var principal = Principal.Authenticated(
            UserId.New(),
            new ExternalIdentity(IdentityProviderId.EntraId, "subject"));

        // Nothing on a principal can produce a tenant context: that requires a separate,
        // server-side resolution step.
        Assert.True(principal.IsAuthenticated);
        Assert.DoesNotContain(
            typeof(Principal).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.PropertyType == typeof(ITenantContext)
                || property.PropertyType == typeof(TenantId));
    }
}
