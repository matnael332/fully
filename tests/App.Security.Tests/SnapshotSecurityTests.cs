using System.Reflection;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Processing;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2E Snapshot properties that must hold by construction. The Snapshot keeps the fact and its provenance and
/// nothing else of an event. Only the projection writes it, only the processor calls the projection, and only
/// with the handoff of an accepted result. No AI, inference or intent source kind exists that could state a fact.
/// </summary>
public sealed class SnapshotSecurityTests
{
    private const string ProcessorFile = "src/App.Infrastructure/Processing/EventProcessor.cs";

    [Fact]
    public void Snapshot_entities_hold_the_fact_and_its_provenance_and_nothing_else_of_an_event()
    {
        Assert.Equal(
            ["LastEventId", "ObjectConnection", "ObjectId", "ObjectSystem", "ObjectType", "ProcessedAtUtc", "TenantId", "Version"],
            PropertyNames(typeof(SnapshotObjectRecord)));

        // Exactly one member holds work content, the fact itself: no before value, actor, reference or payload.
        Assert.Equal(
            [
                "Authority", "ConnectionReference", "DataType", "Field", "FieldVersion", "LastEventId", "ObjectConnection", "ObjectId",
                "ObjectSystem", "ObjectType", "ObjectVersion", "ObservedAtUtc", "OccurredAtUtc", "ProcessedAtUtc", "SourceEventId", "SourceKind",
                "SourceRevision", "SourceSystem", "SourceUpdatedAtUtc", "TenantId", "Value",
            ],
            PropertyNames(typeof(SnapshotFieldRecord)));

        foreach (var entity in new[] { typeof(SnapshotObjectRecord), typeof(SnapshotFieldRecord) })
        {
            Assert.DoesNotContain(
                entity.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => property.SetMethod?.IsPublic == true);
        }
    }

    [Fact]
    public void Only_the_projection_writes_the_snapshot_and_only_the_processor_projects_an_accepted_handoff()
    {
        // Every production file that names a Snapshot entity or set, apart from the migrations, which describe the
        // schema and write no rows: the entities, the schema definition, the context and the projection.
        string[] snapshotNames = ["SnapshotFieldRecord", "SnapshotObjectRecord", "SnapshotFields", "SnapshotObjects"];
        var writers = RepositoryLayout.ProductionSourceFiles
            .Where(file => !RepositoryLayout.RelativePath(file).Contains("/Persistence/Migrations/", StringComparison.Ordinal))
            .Where(file => snapshotNames.Any(name => File.ReadAllText(file.FullName).Contains(name, StringComparison.Ordinal)))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "src/App.Infrastructure/Persistence/Entities/SnapshotFieldRecord.cs",
                "src/App.Infrastructure/Persistence/Entities/SnapshotObjectRecord.cs",
                "src/App.Infrastructure/Persistence/WorkContinuityDbContext.cs",
                "src/App.Infrastructure/Persistence/WorkContinuityModel.cs",
                "src/App.Infrastructure/Processing/SnapshotProjection.cs",
            ],
            writers);

        var callers = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("SnapshotProjection.ProjectAsync", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal([ProcessorFile], callers);

        // The processor projects only what an accepted result hands over, and the projection accepts nothing else.
        var processor = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, ProcessorFile));
        Assert.Contains("result.TryGetProjection(out var projection)", processor, StringComparison.Ordinal);
        Assert.Contains("SnapshotProjection.ProjectAsync(dbContext, projection!, cancellationToken)", processor, StringComparison.Ordinal);

        var projection = typeof(EventProcessingRegistration).Assembly
            .GetType("WorkContinuity.Infrastructure.Processing.SnapshotProjection", throwOnError: true)!;
        var project = projection.GetMethod("ProjectAsync", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(
            [typeof(WorkContinuityDbContext), typeof(ProjectionHandoff), typeof(CancellationToken)],
            project.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Empty(typeof(ProjectionHandoff).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void No_ai_inference_or_intent_source_kind_exists_to_state_a_snapshot_fact()
    {
        // The Snapshot's source kind is checked against exactly these names in the database, too.
        Assert.Equal(
            ["ActionExecutor", "Employee", "IdentityProvider", "LocalAgent", "None", "SaasConnector"],
            Enum.GetNames<EventSourceKind>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_snapshot_row_refuses_a_repeated_event_foreign_evidence_or_a_version_that_does_not_move_forward()
    {
        // Duplicates never get past the ledger, so these are the last line: each would otherwise count one
        // accepted event twice or attach another event's evidence to a fact.
        var tenant = TenantId.New();
        var first = Event(tenant, "1001", revision: 1);
        var second = Event(tenant, "1001", revision: 2);
        var elsewhere = Event(tenant, "2002", revision: 3);

        var snapshotObject = SnapshotObjectRecord.Create(tenant, first.Metadata.WorkObject, first.Metadata.EventId, Now);
        Assert.Throws<InvalidOperationException>(() => snapshotObject.Advance(first.Metadata.EventId, Now));
        snapshotObject.Advance(second.Metadata.EventId, Now);
        Assert.Equal(2L, snapshotObject.Version);

        var field = SnapshotFieldRecord.Create(first, first.Content.Changes[0], EvidenceOf(first), objectVersion: 1);
        Assert.Throws<InvalidOperationException>(() => field.Apply(first, first.Content.Changes[0], EvidenceOf(first), objectVersion: 2));
        Assert.Throws<ArgumentException>(() => field.Apply(second, second.Content.Changes[0], EvidenceOf(first), objectVersion: 2));
        Assert.Throws<ArgumentException>(() => field.Apply(second, second.Content.Changes[0], EvidenceOf(second), objectVersion: 1));
        Assert.Throws<ArgumentException>(() => field.Apply(elsewhere, elsewhere.Content.Changes[0], EvidenceOf(elsewhere), objectVersion: 2));
        Assert.Throws<ArgumentException>(() => field.Apply(second, new FieldChange("status", FactDataType.TaskStatus, null, "not-in-the-event"), EvidenceOf(second), objectVersion: 2));

        field.Apply(second, second.Content.Changes[0], EvidenceOf(second), objectVersion: 2);
        Assert.Equal((2L, 2L, second.Metadata.EventId.Value, 2L), (field.FieldVersion, field.ObjectVersion, field.LastEventId, field.SourceRevision));
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CanonicalEvent Event(TenantId tenant, string objectId, long revision) =>
        CanonicalEvent.Create(
            new CanonicalEventMetadata(
                CanonicalEventId.New(),
                tenant,
                CanonicalEventSchemas.ObjectChangedType,
                CanonicalEventSchemas.ObjectChangedVersion1,
                new EventSource(EventSourceKind.SaasConnector, "synthetic", "connection-1"),
                new ObjectReference("synthetic", "connection-1", "task", objectId),
                "evt-" + revision,
                revision,
                occurredAt: null,
                sourceUpdatedAt: null,
                Now,
                Now,
                CorrelationId.New(),
                Guid.CreateVersion7()),
            new CanonicalEventContent([new FieldChange("status", FactDataType.TaskStatus, null, "done-" + revision)], actor: null, sourceReference: null)).Value;

    private static OrderingEvidence EvidenceOf(CanonicalEvent canonicalEvent) =>
        new(canonicalEvent.Metadata.SourceRevision, canonicalEvent.Metadata.SourceUpdatedAtUtc, canonicalEvent.Metadata.OccurredAtUtc, canonicalEvent.Metadata.SourceEventId);

    private static string[] PropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
