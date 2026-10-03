using System.Reflection;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Processing;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2D processing properties that must hold by construction: a normalizer can neither read nor state the tenant,
/// the source or the provenance; the pipeline alone names the source kind; the ledger has no member that could hold
/// content; and the pipeline never assigns a tenant. Gate 3D adds the monday webhook normalizer as the only production
/// normalizer, and the rule that values a normalizer does not declare confirmed are held before anything is ordered.
/// </summary>
public sealed class ProcessingSecurityTests
{
    private const string ProcessorFile = "src/App.Infrastructure/Processing/EventProcessor.cs";
    private const string NormalizerFile = "src/App.Connectors.Monday/MondayWebhookNormalizer.cs";

    // The members of canonical content and of a raw receipt. A connection reference is an identifier, not content.
    private static readonly string[] ContentWords = ["Payload", "Value", "Before", "After", "Actor", "SourceReference", "Fingerprint", "Content", "Body"];

    [Fact]
    public void The_monday_webhook_normalizer_is_the_only_production_normalizer()
    {
        // Gate 2 had none, so every real source was quarantined until its connector Gate. Gate 3D adds the monday
        // webhook normalizer; any other must be added here deliberately. No host registers it yet: running processing
        // anywhere is an owner boundary, pinned by the transport security tests.
        var mentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("ISourceEventNormalizer", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([NormalizerFile, "src/App.Core/Processing/SourceNormalization.cs", ProcessorFile], mentions);
        Assert.DoesNotContain(
            typeof(EventProcessingRegistration).Assembly.GetTypes(),
            type => typeof(ISourceEventNormalizer).IsAssignableFrom(type));

        var implementations = new[] { typeof(EventProcessingRegistration).Assembly, typeof(MondayWebhookNormalizer).Assembly, typeof(ISourceEventNormalizer).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && typeof(ISourceEventNormalizer).IsAssignableFrom(type))
            .ToArray();
        Assert.Equal([typeof(MondayWebhookNormalizer)], implementations);
    }

    [Fact]
    public void Values_a_normalizer_does_not_declare_confirmed_are_held_before_any_ordering_state_is_touched()
    {
        // The declaration is a required constructor argument, so no normalizer can leave it out.
        var constructor = Assert.Single(typeof(NormalizedSourceEvent).GetConstructors());
        var last = constructor.GetParameters()[^1];
        Assert.Equal(typeof(SourceValueConfirmation), last.ParameterType);
        Assert.False(last.HasDefaultValue);
        Assert.Equal(
            ["Unspecified", "Confirmed", "Unconfirmed"],
            Enum.GetValues<SourceValueConfirmation>().Select(value => value.ToString()));
        Assert.Equal(0, (int)SourceValueConfirmation.Unspecified);

        // Only an event that declares its values confirmed is ordered: the check sits after authority and impossible
        // order, which are judged for every event, and before the ordering state is read or written.
        var processor = Read(ProcessorFile);
        var authority = processor.IndexOf("SourceOfTruthResolver.ResolveChanges(canonical)", StringComparison.Ordinal);
        var impossible = processor.IndexOf("OrderingPolicy.ImpossibilityOf(incoming", StringComparison.Ordinal);
        var confirmation = processor.IndexOf("if (parts.ValueConfirmation != SourceValueConfirmation.Confirmed)", StringComparison.Ordinal);
        var ordering = processor.IndexOf("await LockOrderingStatesAsync(dbContext", StringComparison.Ordinal);
        Assert.True(authority > 0 && authority < impossible && impossible < confirmation && confirmation < ordering, $"{authority} {impossible} {confirmation} {ordering}");
        Assert.Equal(1, processor.Split("parts.ValueConfirmation").Length - 1);
        Assert.Contains("ProcessingErrors.UnconfirmedSourceValues.Code, Withheld(canonical, incoming)", processor, StringComparison.Ordinal);
        Assert.Equal("processing.source.unconfirmed", ProcessingErrors.UnconfirmedSourceValues.Code);
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

        // A normalized event carries the object's home and the source's own statements, never trusted parts. The source
        // scope (Gate 3E) is such a statement: the source's own resource id for its fields, never the trusted event source.
        var normalized = typeof(NormalizedSourceEvent).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(
            ["Content", "OccurredAt", "SchemaVersion", "SourceRevision", "SourceScope", "SourceUpdatedAt", "Type", "ValueConfirmation", "WorkObject"],
            normalized.Order(StringComparer.Ordinal));
        string[] sourceStatements = ["SourceRevision", "SourceScope", "SourceUpdatedAt"];
        Assert.DoesNotContain(normalized, name => trusted.Any(word => name.StartsWith(word, StringComparison.Ordinal)) && !sourceStatements.Contains(name));
        Assert.Equal(typeof(string), typeof(NormalizedSourceEvent).GetProperty(nameof(NormalizedSourceEvent.SourceScope))!.PropertyType);
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
        Assert.DoesNotContain("retention_expired_tenants", processor, StringComparison.Ordinal);

        // It reads the tenant from the scope the transport restored, and refuses a message of another tenant.
        Assert.Contains("_tenantContextAccessor.Current.TryGetTenantId(out var tenant) || tenant != envelope.TenantId", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void Processing_errors_are_safe_tokens_with_fixed_messages()
    {
        var errors = typeof(ProcessingErrors).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => (Core.Results.Error)property.GetValue(null)!)
            .ToList();

        // Gate 3D added exactly one: the reason an unconfirmed change is held for reconciliation. Gate 3F added exactly one:
        // a receipt held for its connection's initial sync.
        Assert.Equal(15, errors.Count);
        Assert.Contains(ProcessingErrors.UnconfirmedSourceValues, errors);
        Assert.Contains(ProcessingErrors.HeldForInitialSync, errors);
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
