using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Tenant context must be resolved server-side. These tests assert both that the type system
/// refuses an untrusted tenant and that no production source file actually derives one from
/// request data.
/// </summary>
public sealed class TenantTrustTests
{
    private static readonly string[] RequestInputApis =
    [
        "Request.Query",
        "Request.Headers",
        "Request.Form",
        "Request.Cookies",
        "Request.Body",
        "Request.RouteValues",
        "RouteValues",
        "FromQuery",
        "FromHeader",
        "FromRoute",
        "FromBody",
    ];

    [Fact]
    public void A_tenant_cannot_be_resolved_without_naming_a_trusted_source()
    {
        Assert.Throws<ArgumentException>(
            () => TenantContext.Resolved(TenantId.New(), TenantResolutionSource.None));
    }

    [Fact]
    public void There_is_no_resolution_source_meaning_the_caller_asserted_it()
    {
        var clientish = Enum.GetNames<TenantResolutionSource>()
            .Where(name =>
                name.Contains("Client", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Request", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Header", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Query", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Caller", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(clientish);
    }

    [Fact]
    public void An_unresolved_context_yields_no_tenant_even_by_default()
    {
        // A caller that forgets to check IsResolved still cannot read a usable tenant, because
        // TryGetTenantId reports false and hands back an empty id rather than a silent default.
        Assert.False(TenantContext.Unresolved.TryGetTenantId(out var tenantId));
        Assert.True(tenantId.IsEmpty);
    }

    [Fact]
    public void No_production_source_derives_a_tenant_from_request_input()
    {
        var offenders = new List<string>();

        foreach (var file in RepositoryLayout.ProductionSourceFiles)
        {
            var lines = File.ReadAllLines(file.FullName);

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                if (!line.Contains("tenant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var api in RequestInputApis)
                {
                    if (line.Contains(api, StringComparison.Ordinal))
                    {
                        offenders.Add(
                            $"{RepositoryLayout.RelativePath(file)}:{index + 1} mentions a tenant "
                            + $"alongside request input '{api}'");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_ingress_assigns_a_tenant_context_and_only_as_a_trusted_service()
    {
        // Gate 1C shipped no caller of SetResolved. Gate 2B adds exactly one: the ingress, which takes
        // the tenant from the connection it resolved server-side and names the trusted-service source.
        // The files under Core/Tenancy declare the abstraction itself and are excluded. The Gate that
        // introduces validated SSO updates this list deliberately, rather than a tenant assignment
        // slipping in unnoticed.
        const string ingress = "src/App.Infrastructure/Ingress/EventIngressService.cs";

        var assigning = RepositoryLayout.ProductionSourceFiles
            .Where(file => !RepositoryLayout.RelativePath(file)
                .StartsWith("src/App.Core/Tenancy/", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file.FullName)
                .Contains("SetResolved", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.Equal([ingress], assigning);

        var text = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, ingress));
        Assert.Equal(1, Occurrences(text, "SetResolved("));
        Assert.Equal(1, Occurrences(text, "TenantResolutionSource."));
        Assert.Contains("TenantResolutionSource.TrustedServiceIdentity", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_production_source_parses_a_tenant_id_from_a_string()
    {
        var offenders = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName)
                .Contains("TenantId.TryParse", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.Empty(offenders);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
