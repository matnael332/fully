using WorkContinuity.Core.Events;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Unit.Tests;

public sealed class CanonicalEventTests
{
    [Fact]
    public void A_complete_event_keeps_every_architecture_field()
    {
        var builder = new EventBuilder();

        var created = builder.Valid();
        var metadata = created.Metadata;

        Assert.Equal(builder.EventId, metadata.EventId);
        Assert.Equal(builder.TenantId, metadata.TenantId);
        Assert.Equal(builder.TenantId, created.TenantId);
        Assert.Equal(CanonicalEventSchemas.ObjectChangedType, metadata.Type);
        Assert.Equal(CanonicalEventSchemas.ObjectChangedVersion1, metadata.SchemaVersion);
        Assert.Equal(builder.Source, metadata.Source);
        Assert.Equal(builder.WorkObject, metadata.WorkObject);
        Assert.Equal("evt-1001", metadata.SourceEventId);
        Assert.Equal<long?>(7, metadata.SourceRevision);
        Assert.Equal(builder.OccurredAt, metadata.OccurredAtUtc);
        Assert.Equal(builder.SourceUpdatedAt, metadata.SourceUpdatedAtUtc);
        Assert.Equal(builder.ReceivedAt, metadata.ReceivedAtUtc);
        Assert.Equal(builder.ProcessedAt, metadata.ProcessedAtUtc);
        Assert.Equal(builder.CorrelationId, metadata.CorrelationId);
        Assert.Equal(builder.CausationId, metadata.CausationId);

        var change = Assert.Single(created.Content.Changes);
        Assert.Equal(new FieldChange("status", FactDataType.TaskStatus, "working", "done"), change);
        Assert.Equal(builder.Actor, created.Content.Actor);
        Assert.Equal(builder.SourceReference, created.Content.SourceReference);
    }

    [Fact]
    public void A_minimal_event_needs_none_of_the_optional_parts()
    {
        var result = new EventBuilder
        {
            SourceEventId = null,
            SourceRevision = null,
            OccurredAt = null,
            SourceUpdatedAt = null,
            ProcessedAt = null,
            CausationId = null,
            Actor = null,
            SourceReference = null,
        }.Create();

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void A_missing_source_time_is_never_filled_from_the_receipt_time()
    {
        var created = new EventBuilder { OccurredAt = null, SourceUpdatedAt = null }.Valid();

        Assert.Null(created.Metadata.OccurredAtUtc);
        Assert.Null(created.Metadata.SourceUpdatedAtUtc);
        Assert.Equal(EventBuilder.Received, created.Metadata.ReceivedAtUtc);
    }

    [Fact]
    public void Every_instant_is_normalised_to_utc_without_moving_it()
    {
        var local = new DateTimeOffset(2026, 9, 28, 7, 10, 8, TimeSpan.FromHours(3));

        var metadata = new EventBuilder
        {
            OccurredAt = local,
            SourceUpdatedAt = local,
            ReceivedAt = local,
            ProcessedAt = local,
        }.Valid().Metadata;

        DateTimeOffset?[] instants =
        [
            metadata.OccurredAtUtc,
            metadata.SourceUpdatedAtUtc,
            metadata.ReceivedAtUtc,
            metadata.ProcessedAtUtc,
        ];
        foreach (var instant in instants)
        {
            Assert.NotNull(instant);
            Assert.Equal(TimeSpan.Zero, instant.Value.Offset);
            Assert.Equal(local, instant.Value);
        }
    }

    [Fact]
    public void Every_malformed_part_is_refused_with_a_stable_code_and_its_quarantine_reason()
    {
        var cases = new (string Name, Action<EventBuilder> Break, Error Expected, QuarantineReason Reason)[]
        {
            ("empty event id", b => b.EventId = default, CanonicalEventErrors.MissingEventId, QuarantineReason.BadSchema),
            ("no tenant", b => b.TenantId = default, CanonicalEventErrors.MissingTenant, QuarantineReason.UnknownTenant),
            ("unknown type", b => b.Type = "workcontinuity.object.renamed", CanonicalEventErrors.UnknownType, QuarantineReason.BadSchema),
            ("empty type", b => b.Type = string.Empty, CanonicalEventErrors.UnknownType, QuarantineReason.BadSchema),
            ("unsupported schema version", b => b.SchemaVersion = "2", CanonicalEventErrors.UnknownSchemaVersion, QuarantineReason.UnknownSchemaVersion),
            ("source kind none", b => b.Source = new EventSource(EventSourceKind.None, "synthetic", "connection-1"), CanonicalEventErrors.InvalidSource, QuarantineReason.BadSchema),
            ("undefined source kind", b => b.Source = new EventSource((EventSourceKind)42, "synthetic", "connection-1"), CanonicalEventErrors.InvalidSource, QuarantineReason.BadSchema),
            ("uppercase source system", b => b.Source = new EventSource(EventSourceKind.SaasConnector, "Synthetic", "connection-1"), CanonicalEventErrors.InvalidSource, QuarantineReason.BadSchema),
            ("connection with a line break", b => b.Source = new EventSource(EventSourceKind.SaasConnector, "synthetic", "connection\n1"), CanonicalEventErrors.InvalidSource, QuarantineReason.BadSchema),
            ("connection too long", b => b.Source = new EventSource(EventSourceKind.SaasConnector, "synthetic", new string('c', 201)), CanonicalEventErrors.InvalidSource, QuarantineReason.BadSchema),
            ("object type with a space", b => b.WorkObject = new ObjectReference("synthetic", "connection-1", "work item", "task-42"), CanonicalEventErrors.InvalidObject, QuarantineReason.BadSchema),
            ("empty object id", b => b.WorkObject = EventBuilder.Task(string.Empty), CanonicalEventErrors.InvalidObject, QuarantineReason.BadSchema),
            ("object id with a bidi override", b => b.WorkObject = EventBuilder.Task("task‮42"), CanonicalEventErrors.InvalidObject, QuarantineReason.BadSchema),
            ("object id with surrounding whitespace", b => b.WorkObject = EventBuilder.Task(" task-42"), CanonicalEventErrors.InvalidObject, QuarantineReason.BadSchema),
            ("blank source event id", b => b.SourceEventId = "   ", CanonicalEventErrors.InvalidSourceEventId, QuarantineReason.BadSchema),
            ("source event id with a tab", b => b.SourceEventId = "evt\t1001", CanonicalEventErrors.InvalidSourceEventId, QuarantineReason.BadSchema),
            ("negative revision", b => b.SourceRevision = -1, CanonicalEventErrors.InvalidSourceRevision, QuarantineReason.BadSchema),
            ("unset receipt time", b => b.ReceivedAt = default, CanonicalEventErrors.InvalidTime, QuarantineReason.BadSchema),
            ("maximum occurrence time", b => b.OccurredAt = DateTimeOffset.MaxValue, CanonicalEventErrors.InvalidTime, QuarantineReason.BadSchema),
            ("minimum source update time", b => b.SourceUpdatedAt = DateTimeOffset.MinValue, CanonicalEventErrors.InvalidTime, QuarantineReason.BadSchema),
            ("unset processing time", b => b.ProcessedAt = default(DateTimeOffset), CanonicalEventErrors.InvalidTime, QuarantineReason.BadSchema),
            ("no correlation id", b => b.CorrelationId = default, CanonicalEventErrors.MissingCorrelation, QuarantineReason.BadSchema),
            ("empty causation id", b => b.CausationId = Guid.Empty, CanonicalEventErrors.InvalidCausation, QuarantineReason.BadSchema),
            ("no changes", b => b.Changes = [], CanonicalEventErrors.MissingChanges, QuarantineReason.BadSchema),
            ("change without a data type", b => b.Changes = [new FieldChange("status", FactDataType.None, null, "done")], CanonicalEventErrors.InvalidChange, QuarantineReason.BadSchema),
            ("change with an undefined data type", b => b.Changes = [new FieldChange("status", (FactDataType)99, null, "done")], CanonicalEventErrors.InvalidChange, QuarantineReason.BadSchema),
            ("uppercase field name", b => b.Changes = [new FieldChange("Status", FactDataType.TaskStatus, null, "done")], CanonicalEventErrors.InvalidChange, QuarantineReason.BadSchema),
            ("same field twice", b => b.Changes = [new FieldChange("status", FactDataType.TaskStatus, null, "working"), new FieldChange("status", FactDataType.TaskStatus, "working", "done")], CanonicalEventErrors.DuplicateField, QuarantineReason.BadSchema),
            ("actor id with a control character", b => b.Actor = EventActor.Unresolved("user\u0007"), CanonicalEventErrors.InvalidActor, QuarantineReason.BadSchema),
            ("empty actor id", b => b.Actor = EventActor.Unresolved(string.Empty), CanonicalEventErrors.InvalidActor, QuarantineReason.BadSchema),
            ("source reference too long", b => b.SourceReference = new string('r', 2049), CanonicalEventErrors.InvalidSourceReference, QuarantineReason.BadSchema),
        };

        var failures = new List<string>();
        foreach (var (name, breakIt, expected, reason) in cases)
        {
            var builder = new EventBuilder();
            breakIt(builder);

            var result = builder.Create();
            if (result.IsSuccess)
            {
                failures.Add($"{name}: accepted");
                continue;
            }

            if (result.Error != expected)
            {
                failures.Add($"{name}: {result.Error.Code} instead of {expected.Code}");
            }

            var actualReason = CanonicalEventErrors.QuarantineReasonFor(result.Error);
            if (actualReason != reason)
            {
                failures.Add($"{name}: quarantined as {actualReason} instead of {reason}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void A_validation_error_never_echoes_the_offending_value()
    {
        const string marker = "INJECTED-MARKER";

        var result = new EventBuilder { WorkObject = EventBuilder.Task(marker + "\n") }.Create();

        Assert.True(result.IsFailure);
        Assert.DoesNotContain(marker, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, result.Error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Identifiers_at_their_maximum_length_are_accepted()
    {
        var result = new EventBuilder
        {
            Source = new EventSource(EventSourceKind.SaasConnector, new string('s', 64), new string('c', 200)),
            WorkObject = new ObjectReference(new string('s', 64), new string('c', 200), new string('t', 64), new string('o', 200)),
            SourceEventId = new string('e', 200),
            SourceReference = new string('r', 2048),
            Actor = EventActor.Unresolved(new string('a', 200)),
        }.Create();

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Provider_identifiers_may_use_any_printable_text()
    {
        var result = new EventBuilder
        {
            Source = new EventSource(EventSourceKind.SaasConnector, "synthetic", "team a/b"),
            WorkObject = new ObjectReference("synthetic", "team a/b", "task", "Ticket #42 (ürgent)"),
            SourceEventId = "evt:42@2026",
            Actor = EventActor.Unresolved("Ada Lovelace"),
        }.Create();

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void The_change_list_is_copied_so_an_event_cannot_be_altered_after_creation()
    {
        var builder = new EventBuilder();
        var created = builder.Valid();

        builder.Changes.Add(new FieldChange("deadline", FactDataType.Deadline, null, "2026-10-01"));

        Assert.Single(created.Content.Changes);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<FieldChange>)created.Content.Changes).Add(
                new FieldChange("deadline", FactDataType.Deadline, null, "2026-10-01")));
    }

    [Fact]
    public void Several_fields_of_different_data_types_can_change_in_one_event()
    {
        var created = new EventBuilder
        {
            Changes =
            [
                new FieldChange("status", FactDataType.TaskStatus, "working", "done"),
                new FieldChange("deadline", FactDataType.Deadline, "2026-09-30", "2026-10-01"),
            ],
        }.Valid();

        Assert.Equal(2, created.Content.Changes.Count);
    }

    [Fact]
    public void A_cleared_field_and_an_unreported_previous_value_are_representable()
    {
        var created = new EventBuilder
        {
            Changes = [new FieldChange("deadline", FactDataType.Deadline, before: null, after: null)],
        }.Valid();

        var change = Assert.Single(created.Content.Changes);
        Assert.Null(change.Before);
        Assert.Null(change.After);
    }

    [Fact]
    public void An_unresolved_actor_is_preserved_and_does_not_invalidate_the_event()
    {
        var created = new EventBuilder { Actor = EventActor.Unresolved("source-user-99") }.Valid();

        var actor = created.Content.Actor;
        Assert.NotNull(actor);
        Assert.False(actor.IsResolved);
        Assert.Null(actor.CanonicalUserId);
        Assert.Equal("source-user-99", actor.SourceActorId);
    }

    [Fact]
    public void A_resolved_actor_requires_a_canonical_user()
    {
        Assert.Throws<ArgumentException>(() => EventActor.Resolved("user-7", default));

        var userId = UserId.New();
        var actor = EventActor.Resolved("user-7", userId);

        Assert.True(actor.IsResolved);
        Assert.Equal(userId, actor.CanonicalUserId);
    }

    [Fact]
    public void Missing_parts_are_programming_errors_not_quarantine_reasons()
    {
        var builder = new EventBuilder();

        Assert.Throws<ArgumentNullException>(() => CanonicalEvent.Create(null!, builder.Content()));
        Assert.Throws<ArgumentNullException>(() => CanonicalEvent.Create(builder.Metadata(), null!));
        Assert.Throws<ArgumentNullException>(() => new EventSource(EventSourceKind.SaasConnector, null!, "connection-1"));
        Assert.Throws<ArgumentNullException>(() => new ObjectReference("synthetic", "connection-1", "task", null!));
        Assert.Throws<ArgumentNullException>(() => new FieldChange(null!, FactDataType.TaskStatus, null, "done"));
        Assert.Throws<ArgumentNullException>(() => EventActor.Unresolved(null!));
        Assert.Throws<ArgumentNullException>(() => new CanonicalEventContent(null!, actor: null, sourceReference: null));
        Assert.Throws<ArgumentException>(() => new CanonicalEventContent([null!], actor: null, sourceReference: null));
    }

    [Fact]
    public void A_canonical_event_id_is_never_empty()
    {
        Assert.False(CanonicalEventId.New().IsEmpty);
        Assert.NotEqual(CanonicalEventId.New(), CanonicalEventId.New());
        Assert.True(default(CanonicalEventId).IsEmpty);
        Assert.Throws<ArgumentException>(() => CanonicalEventId.From(Guid.Empty));
    }
}

public sealed class CanonicalEventSchemaTests
{
    [Fact]
    public void Object_changed_version_1_is_the_only_supported_schema()
    {
        string[] types = [CanonicalEventSchemas.ObjectChangedType];
        string[] versions = [CanonicalEventSchemas.ObjectChangedVersion1];

        Assert.Equal(types, CanonicalEventSchemas.KnownTypes);
        Assert.Equal(versions, CanonicalEventSchemas.VersionsOf(CanonicalEventSchemas.ObjectChangedType));
        Assert.True(CanonicalEventSchemas.IsSupported(
            CanonicalEventSchemas.ObjectChangedType,
            CanonicalEventSchemas.ObjectChangedVersion1));
    }

    [Fact]
    public void Unknown_types_and_versions_are_not_supported()
    {
        Assert.False(CanonicalEventSchemas.IsKnownType("workcontinuity.object.deleted"));
        Assert.False(CanonicalEventSchemas.IsKnownType(null));
        Assert.False(CanonicalEventSchemas.IsSupported(CanonicalEventSchemas.ObjectChangedType, "2"));
        Assert.False(CanonicalEventSchemas.IsSupported(CanonicalEventSchemas.ObjectChangedType, null));
        Assert.False(CanonicalEventSchemas.IsSupported(null, CanonicalEventSchemas.ObjectChangedVersion1));
        Assert.Throws<ArgumentException>(() => CanonicalEventSchemas.VersionsOf("workcontinuity.object.deleted"));
    }
}

public sealed class QuarantineReasonTests
{
    [Fact]
    public void The_reasons_are_exactly_the_six_architecture_reasons()
    {
        string[] expected =
        [
            "None",
            "BadSchema",
            "UnknownTenant",
            "UnknownSchemaVersion",
            "ImpossibleOrdering",
            "AuthorityConflict",
            "InvalidAuthentication",
        ];

        Assert.Equal(expected, Enum.GetNames<QuarantineReason>());
    }

    [Fact]
    public void Every_validation_error_has_a_distinct_code_and_a_quarantine_reason()
    {
        foreach (var error in CanonicalEventErrors.All)
        {
            Assert.NotEqual(QuarantineReason.None, CanonicalEventErrors.QuarantineReasonFor(error));
        }

        var distinctCodes = CanonicalEventErrors.All.Select(error => error.Code).Distinct(StringComparer.Ordinal);
        Assert.Equal(CanonicalEventErrors.All.Count, distinctCodes.Count());
    }

    [Fact]
    public void An_error_from_elsewhere_has_no_quarantine_reason()
    {
        var foreign = Error.Validation("persistence.other", "Something else failed.");

        Assert.Throws<ArgumentException>(() => CanonicalEventErrors.QuarantineReasonFor(foreign));
    }
}
