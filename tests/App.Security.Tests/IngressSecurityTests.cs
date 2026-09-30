using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Ingress;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2B ingress properties that must hold by construction: a delivery cannot name its tenant, the
/// tenant-less database path is one internal method calling one approved function, and every source without
/// a verifier fails closed. Gate 3C adds the production monday verifier as the only one, the verifier
/// contract's post-tenant confirmation, and a connection check that reveals a status only.
/// </summary>
public sealed class IngressSecurityTests
{
    private const string ResolverFile = "src/App.Infrastructure/Ingress/IngressConnectionResolver.cs";
    private const string IngressFile = "src/App.Infrastructure/Ingress/EventIngressService.cs";
    private const string MondayVerifierFile = "src/App.Connectors.Monday/MondayWebhookVerifier.cs";
    private const string WebRegistrationFile = "src/App.Web/Connectors/MondayWebhookRegistration.cs";
    private const string MigrationFile =
        "src/App.Infrastructure/Persistence/Migrations/20260928101033_DurableIngressConnections.cs";
    private const string RelayMigrationFile =
        "src/App.Infrastructure/Persistence/Migrations/20260928114056_OutboxRelayTenantDiscovery.cs";
    private const string RetentionMigrationFile =
        "src/App.Infrastructure/Persistence/Migrations/20260928171310_RawContentRetention.cs";
    private const string PurgeAgeMigrationFile =
        "src/App.Infrastructure/Persistence/Migrations/20260928235219_RawContentPurgeAge.cs";

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
    public void Security_definer_and_database_functions_exist_only_in_the_approved_migrations()
    {
        // Gate 2 owner decision 1 allows narrowly scoped functions for three operations. Gate 2B added the
        // connection resolver, Gate 2C the outbox discovery function and Gate 2F the retention function, which
        // completes the three; the Gate 2F follow-up migration redefines that same function, without a cutoff, at
        // the 28-day purge age by the database's clock, and its Down restores the original. It also adds the
        // receipt-time trigger's function, which is no definer and reads nothing: it only stamps the inserted row's
        // own receipt time with the database's clock. Any other must be added to this list deliberately, with its own
        // pinned tests.
        var securityDefiner = new Regex(@"\bSECURITY\s+DEFINER\b", RegexOptions.IgnoreCase);
        var createsRoutine = new Regex(
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)\b",
            RegexOptions.IgnoreCase);

        var definerFiles = RepositoryLayout.ProductionSourceFiles
            .Where(file => securityDefiner.IsMatch(File.ReadAllText(file.FullName)))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([MigrationFile, RelayMigrationFile, RetentionMigrationFile, PurgeAgeMigrationFile], definerFiles);

        var routines = RepositoryLayout.ProductionSourceFiles
            .SelectMany(file => createsRoutine.Matches(File.ReadAllText(file.FullName))
                .Select(_ => RepositoryLayout.RelativePath(file)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [MigrationFile, RelayMigrationFile, RetentionMigrationFile, PurgeAgeMigrationFile, PurgeAgeMigrationFile, PurgeAgeMigrationFile],
            routines);
    }

    [Fact]
    public void The_monday_webhook_verifier_is_the_only_production_source_verifier_and_the_only_proof_reader()
    {
        // Gate 2 registered no source verifier, so every real source failed closed. Gate 3C adds the monday webhook
        // verifier, registered by the Web host's webhook composition; any other must be added here deliberately.
        var verifierMentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("ISourceVerifier", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([MondayVerifierFile, "src/App.Core/Ingress/IEventIngress.cs", IngressFile, WebRegistrationFile], verifierMentions);

        var implementations = new[] { typeof(IngressRegistration).Assembly, typeof(MondayWebhookVerifier).Assembly, typeof(ISourceVerifier).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(ISourceVerifier).IsAssignableFrom(type) && type.IsClass)
            .ToArray();
        Assert.Equal([typeof(MondayWebhookVerifier)], implementations);

        // Only a verifier may read the proof, and the monday verifier neither stores nor logs it.
        var proofReaders = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains(".Proof", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal([MondayVerifierFile], proofReaders);
        Assert.DoesNotContain("ILogger", Read(MondayVerifierFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_verifier_contract_is_verify_before_the_tenant_then_a_mandatory_confirmation_after_it()
    {
        // Gate 3C's provider-neutral extension: exactly these members, all abstract, so no verifier can omit the
        // confirmation, and a confirmation of any value but Confirmed stores nothing.
        var members = typeof(ISourceVerifier).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member is not MethodInfo { IsSpecialName: true })
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["ConfirmAsync", "SourceSystem", "VerifyAsync"], members);
        Assert.All(typeof(ISourceVerifier).GetMethods(), method => Assert.True(method.IsAbstract, method.Name));

        var confirm = typeof(ISourceVerifier).GetMethod(nameof(ISourceVerifier.ConfirmAsync))!;
        Assert.Equal(typeof(ValueTask<SourceConfirmation>), confirm.ReturnType);
        Assert.Equal([typeof(IngressSubmission), typeof(CancellationToken)], confirm.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            ["Unspecified", "Confirmed", "Refused", "Unavailable"],
            Enum.GetValues<SourceConfirmation>().Select(value => value.ToString()));
        Assert.Equal(0, (int)SourceConfirmation.Unspecified);
    }

    [Fact]
    public void Ingress_verifies_before_setting_the_tenant_and_confirms_after_it_but_before_any_write()
    {
        var ingress = Read(IngressFile);

        var verify = ingress.IndexOf("await IsAuthenticAsync(verifier, submission", StringComparison.Ordinal);
        var setTenant = ingress.IndexOf("tenantContextAccessor.SetResolved(", StringComparison.Ordinal);
        var confirm = ingress.IndexOf("await ConfirmAsync(verifier, submission", StringComparison.Ordinal);
        var write = ingress.IndexOf("await database.ExecuteAsync(", StringComparison.Ordinal);

        Assert.True(verify > 0 && verify < setTenant && setTenant < confirm && confirm < write, $"{verify} {setTenant} {confirm} {write}");
        Assert.Equal(1, Occurrences(ingress, "tenantContextAccessor.SetResolved("));
        Assert.Equal(1, Occurrences(ingress, "await ConfirmAsync(verifier, submission"));
        Assert.Equal(1, Occurrences(ingress, "if (confirmation != SourceConfirmation.Confirmed)"));
    }

    [Fact]
    public void The_connection_check_takes_a_connection_and_a_source_and_reveals_a_status_only()
    {
        var method = Assert.Single(typeof(IIngressConnectionCheck).GetMethods());
        Assert.Equal(nameof(IIngressConnectionCheck.CheckAsync), method.Name);
        Assert.Equal(typeof(Task<IngressConnectionStatus>), method.ReturnType);
        Assert.Equal([typeof(Guid), typeof(string), typeof(CancellationToken)], method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            ["Unspecified", "Active", "Unknown", "Unavailable"],
            Enum.GetValues<IngressConnectionStatus>().Select(value => value.ToString()));

        var check = typeof(IngressRegistration).Assembly.GetType("WorkContinuity.Infrastructure.Ingress.IngressConnectionCheck", throwOnError: true)!;
        Assert.False(check.IsPublic);
        Assert.True(check.IsSealed);

        // It goes through the approved resolver and nothing else: no SQL, no database context, no tenant.
        var source = Read("src/App.Infrastructure/Ingress/IngressConnectionCheck.cs");
        Assert.Contains("connectionResolver.ResolveAsync(connectionId, cancellationToken)", source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "SELECT", "resolve_ingress_connection(", "DbContext", "SetResolved", "ITenantContextAccessor" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
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
