using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Ingress;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2B ingress properties that must hold by construction: a delivery cannot name its tenant, the
/// tenant-less database path is one internal method calling one approved function, and no production
/// source verifier exists yet, so every real source fails closed.
/// </summary>
public sealed class IngressSecurityTests
{
    private const string ResolverFile = "src/App.Infrastructure/Ingress/IngressConnectionResolver.cs";
    private const string IngressFile = "src/App.Infrastructure/Ingress/EventIngressService.cs";
    private const string MigrationFile =
        "src/App.Infrastructure/Persistence/Migrations/20260928101033_DurableIngressConnections.cs";

    [Fact]
    public void A_delivery_has_no_way_to_name_its_tenant()
    {
        var members = typeof(IngressSubmission)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, Type: property.PropertyType))
            .Concat(typeof(IngressSubmission)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => (Name: parameter.Name ?? string.Empty, Type: parameter.ParameterType)))
            .ToList();

        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, member =>
            member.Type == typeof(TenantId) || typeof(ITenantContext).IsAssignableFrom(member.Type));
        Assert.DoesNotContain(members, member =>
            member.Name.Contains("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_delivery_and_its_outcome_never_print_the_payload_or_the_proof()
    {
        var submission = new IngressSubmission(
            Guid.CreateVersion7(),
            "synthetic",
            "evt-1",
            "1",
            """{"note":"payload-marker"}""",
            CorrelationId.New(),
            "proof-marker");

        Assert.DoesNotContain("marker", submission.ToString(), StringComparison.Ordinal);

        // The outcome holds identifiers, codes and reasons only: no text member that could carry content.
        Assert.DoesNotContain(
            typeof(IngressOutcome).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.PropertyType == typeof(string));

        var outcome = IngressOutcome.Rejected(IngressErrors.SourceNotVerified, submission.CorrelationId);
        Assert.DoesNotContain("marker", outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_tenant_less_resolver_is_one_internal_method_taking_only_a_connection_id()
    {
        var resolver = typeof(IngressRegistration).Assembly.GetType(
            "WorkContinuity.Infrastructure.Ingress.IngressConnectionResolver",
            throwOnError: true)!;

        Assert.False(resolver.IsPublic);
        Assert.True(resolver.IsSealed);

        var methods = resolver
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();
        Assert.Equal(["ResolveAsync"], methods);

        var parameters = resolver.GetMethod("ResolveAsync")!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.Equal([typeof(Guid), typeof(CancellationToken)], parameters);

        // The ingress service is internal too: the public surface is IEventIngress and its registration.
        Assert.False(typeof(IngressRegistration).Assembly
            .GetType("WorkContinuity.Infrastructure.Ingress.EventIngressService", throwOnError: true)!
            .IsPublic);
    }

    [Fact]
    public void Only_the_resolver_calls_the_tenant_less_function_and_only_its_migration_defines_it()
    {
        var mentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName)
                .Contains("resolve_ingress_connection(", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([ResolverFile, MigrationFile], mentions);

        // The resolver's statement is fixed, parameterised and schema-qualified.
        var resolver = Read(ResolverFile);
        Assert.Equal(1, Occurrences(resolver, "resolve_ingress_connection("));
        Assert.Contains(
            "SELECT tenant_id, source_system FROM public.resolve_ingress_connection(@connection_id)",
            resolver,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Security_definer_and_database_functions_exist_only_in_the_approved_migration()
    {
        // Gate 2 owner decision 1 allows narrowly scoped functions for three operations. Gate 2B adds the
        // first; each later one must be added to this list deliberately, with its own pinned tests.
        var securityDefiner = new Regex(@"\bSECURITY\s+DEFINER\b", RegexOptions.IgnoreCase);
        var createsRoutine = new Regex(
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)\b",
            RegexOptions.IgnoreCase);

        var definerFiles = RepositoryLayout.ProductionSourceFiles
            .Where(file => securityDefiner.IsMatch(File.ReadAllText(file.FullName)))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal([MigrationFile], definerFiles);

        var routines = RepositoryLayout.ProductionSourceFiles
            .SelectMany(file => createsRoutine.Matches(File.ReadAllText(file.FullName))
                .Select(_ => RepositoryLayout.RelativePath(file)))
            .ToArray();
        Assert.Equal([MigrationFile], routines);
    }

    [Fact]
    public void No_production_source_verifier_exists_yet_and_no_production_code_reads_the_proof()
    {
        // Gate 2 registers no source verifier, so every real source fails closed. The connector Gate
        // that adds one updates this list deliberately.
        var verifierMentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("ISourceVerifier", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["src/App.Core/Ingress/IEventIngress.cs", IngressFile], verifierMentions);

        // Only a verifier may read the proof; production code never persists or logs it.
        var proofReaders = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains(".Proof", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Empty(proofReaders);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));

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
