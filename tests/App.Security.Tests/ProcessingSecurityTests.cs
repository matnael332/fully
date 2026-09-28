using System.Reflection;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Processing;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2D processing properties that must hold by construction: no production normalizer exists yet; a
/// normalizer can neither read nor state the tenant, the source or the provenance; the pipeline alone names the
/// source kind; the ledger has no member that could hold content; and the pipeline never assigns a tenant.
/// </summary>
public sealed class ProcessingSecurityTests
{
    private const string ProcessorFile = "src/App.Infrastructure/Processing/EventProcessor.cs";

    // The members of canonical content and of a raw receipt. A connection reference is an identifier, not content.
    private static readonly string[] ContentWords = ["Payload", "Value", "Before", "After", "Actor", "SourceReference", "Fingerprint", "Content", "Body"];

    [Fact]
    public void No_production_normalizer_exists_yet_so_every_real_source_is_quarantined_until_its_connector_gate()
    {
        var mentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("ISourceEventNormalizer", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["src/App.Core/Processing/SourceNormalization.cs", ProcessorFile], mentions);
        Assert.DoesNotContain(
            typeof(EventProcessingRegistration).Assembly.GetTypes(),
            type => typeof(ISourceEventNormalizer).IsAssignableFrom(type));
    }

    [Fact]
    public void A_normalizer_can_neither_read_nor_state_the_tenant_the_source_or_the_provenance()
    {
        string[] trusted = ["Tenant", "Source", "Kind", "Correlation", "Causation", "Received", "Processed", "EventId"];

        foreach (var type in new[] { typeof(SourcePayload), typeof(NormalizedSourceEvent) })
        {
            var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => (property.Name, Type: property.PropertyType))
                .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => (Name: parameter.Name ?? string.Empty, Type: parameter.ParameterType)))
                .ToList();

            Assert.NotEmpty(members);
            Assert.DoesNotContain(members, member => member.Type == typeof(TenantId) || member.Type == typeof(EventSource) || member.Type == typeof(EventSourceKind));
            Assert.DoesNotContain(members, member => member.Name.Contains("tenant", StringComparison.OrdinalIgnoreCase));
        }

        // A normalized event carries the object's home and the source's own statements, never trusted parts.
        var normalized = typeof(NormalizedSourceEvent).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(
            ["Content", "OccurredAt", "SchemaVersion", "SourceRevision", "SourceUpdatedAt", "Type", "WorkObject"],
            normalized.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(normalized, name => trusted.Any(word => name.StartsWith(word, StringComparison.Ordinal) && name != "SourceRevision" && name != "SourceUpdatedAt"));
    }

    [Fact]
    public void The_pipeline_alone_names_the_source_kind_and_it_is_always_the_saas_connector_of_the_receipt()
    {
        var processor = Read(ProcessorFile);
        var kinds = Enum.GetNames<EventSourceKind>()
            .Where(name => processor.Contains("EventSourceKind." + name, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([nameof(EventSourceKind.SaasConnector)], kinds);
        Assert.Contains("new EventSource(EventSourceKind.SaasConnector, receipt.SourceSystem, receipt.ConnectionReference)", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_entities_have_no_member_that_could_hold_raw_content()
    {
        foreach (var entity in new[] { typeof(EventLedgerRecord), typeof(EventLedgerFieldRecord), typeof(FieldOrderingStateRecord) })
        {
            var names = entity.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToList();
            Assert.NotEmpty(names);
            Assert.DoesNotContain(names, name => ContentWords.Any(word => name.Contains(word, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void The_pipeline_never_assigns_a_tenant_and_never_uses_a_tenant_less_function()
    {
        var processor = Read(ProcessorFile);

        Assert.DoesNotContain("SetResolved", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("outbox_pending_tenants", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("resolve_ingress_connection", processor, StringComparison.Ordinal);

        // It reads the tenant from the scope the transport restored, and refuses a message of another tenant.
        Assert.Contains("_tenantContextAccessor.Current.TryGetTenantId(out var tenant) || tenant != envelope.TenantId", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void Processing_errors_are_safe_tokens_with_fixed_messages()
    {
        var errors = typeof(ProcessingErrors).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => (Core.Results.Error)property.GetValue(null)!)
            .ToList();

        Assert.Equal(12, errors.Count);
        Assert.Equal(errors.Count, errors.Select(error => error.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(errors, error =>
        {
            Assert.True(EventText.IsToken(error.Code), error.Code);
            Assert.StartsWith("processing.", error.Code, StringComparison.Ordinal);
        });
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));
}
