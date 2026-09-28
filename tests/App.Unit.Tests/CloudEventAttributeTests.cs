using WorkContinuity.Core.Events;

namespace WorkContinuity.Unit.Tests;

public sealed class CloudEventAttributeTests
{
    [Fact]
    public void The_required_context_attributes_are_always_present()
    {
        var created = new EventBuilder().Valid();

        var attributes = CloudEventAttributes.For(created);

        Assert.Equal(created.Metadata.EventId.ToString(), attributes[CloudEventAttributes.Context.Id]);
        Assert.Equal("1.0", attributes[CloudEventAttributes.Context.SpecVersion]);
        Assert.Equal(CanonicalEventSchemas.ObjectChangedType, attributes[CloudEventAttributes.Context.Type]);
        Assert.Equal("/saas-connector/synthetic/connection-1", attributes[CloudEventAttributes.Context.Source]);
        Assert.True(Uri.TryCreate(attributes[CloudEventAttributes.Context.Source], UriKind.Relative, out _));
        Assert.Equal("synthetic/connection-1/task/task-42", attributes[CloudEventAttributes.Context.Subject]);
    }

    [Fact]
    public void Times_use_the_rfc_3339_utc_form_and_time_is_the_occurrence()
    {
        var attributes = CloudEventAttributes.For(new EventBuilder().Valid());

        Assert.Equal("2026-09-28T04:08:08.0000000Z", attributes[CloudEventAttributes.Context.Time]);
        Assert.Equal("2026-09-28T04:09:08.0000000Z", attributes[CloudEventAttributes.Extension.SourceUpdatedTime]);
        Assert.Equal("2026-09-28T04:10:08.0000000Z", attributes[CloudEventAttributes.Extension.ReceivedTime]);
        Assert.Equal("2026-09-28T04:10:09.0000000Z", attributes[CloudEventAttributes.Extension.ProcessedTime]);
    }

    [Fact]
    public void Product_extensions_carry_the_tenant_version_ids_and_revision()
    {
        var builder = new EventBuilder();
        var attributes = CloudEventAttributes.For(builder.Valid());

        Assert.Equal(builder.TenantId.ToString(), attributes[CloudEventAttributes.Extension.TenantId]);
        Assert.Equal(CanonicalEventSchemas.ObjectChangedVersion1, attributes[CloudEventAttributes.Extension.SchemaVersion]);
        Assert.Equal("evt-1001", attributes[CloudEventAttributes.Extension.SourceEventId]);
        Assert.Equal("7", attributes[CloudEventAttributes.Extension.SourceRevision]);
        Assert.Equal(builder.CorrelationId.Value, attributes[CloudEventAttributes.Extension.CorrelationId]);
        Assert.Equal(builder.CausationId!.Value.ToString("D"), attributes[CloudEventAttributes.Extension.CausationId]);
    }

    [Fact]
    public void Absent_optional_values_are_omitted_rather_than_invented()
    {
        var created = new EventBuilder
        {
            OccurredAt = null,
            SourceUpdatedAt = null,
            ProcessedAt = null,
            SourceEventId = null,
            SourceRevision = null,
            CausationId = null,
        }.Valid();

        var attributes = CloudEventAttributes.For(created);

        string[] omitted =
        [
            CloudEventAttributes.Context.Time,
            CloudEventAttributes.Extension.SourceUpdatedTime,
            CloudEventAttributes.Extension.ProcessedTime,
            CloudEventAttributes.Extension.SourceEventId,
            CloudEventAttributes.Extension.SourceRevision,
            CloudEventAttributes.Extension.CausationId,
        ];
        foreach (var name in omitted)
        {
            Assert.DoesNotContain(name, attributes.Keys);
        }

        Assert.Equal("2026-09-28T04:10:08.0000000Z", attributes[CloudEventAttributes.Extension.ReceivedTime]);
    }

    [Fact]
    public void Attribute_names_follow_the_cloudevents_naming_rule()
    {
        var names = CloudEventAttributes.ContextNames.Concat(CloudEventAttributes.ExtensionNames).ToArray();

        foreach (var name in names)
        {
            Assert.InRange(name.Length, 1, 20);
            Assert.All(name, character => Assert.True(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character)));
        }

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_attribute_produced_is_a_declared_name()
    {
        var declared = CloudEventAttributes.ContextNames
            .Concat(CloudEventAttributes.ExtensionNames)
            .ToHashSet(StringComparer.Ordinal);

        var attributes = CloudEventAttributes.For(new EventBuilder().Valid());

        Assert.All(attributes.Keys, name => Assert.Contains(name, declared));
    }

    [Fact]
    public void Source_and_subject_escape_provider_identifiers()
    {
        var created = new EventBuilder
        {
            Source = new EventSource(EventSourceKind.SaasConnector, "synthetic", "team a/b"),
            WorkObject = new ObjectReference("synthetic", "team a/b", "task", "id/with space"),
        }.Valid();

        var attributes = CloudEventAttributes.For(created);

        Assert.Equal("/saas-connector/synthetic/team%20a%2Fb", attributes[CloudEventAttributes.Context.Source]);
        Assert.Equal("synthetic/team%20a%2Fb/task/id%2Fwith%20space", attributes[CloudEventAttributes.Context.Subject]);
        Assert.True(Uri.TryCreate(attributes[CloudEventAttributes.Context.Source], UriKind.Relative, out _));
    }

    [Fact]
    public void Attributes_never_carry_work_content()
    {
        const string marker = "CONTENT-MARKER";
        var created = new EventBuilder
        {
            Changes = [new FieldChange("status", FactDataType.TaskStatus, marker + "-before", marker + "-after")],
            Actor = EventActor.Unresolved(marker + "-actor"),
            SourceReference = "https://example.invalid/" + marker,
        }.Valid();

        var attributes = CloudEventAttributes.For(created);

        Assert.All(attributes.Values, value => Assert.DoesNotContain(marker, value, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_defined_source_kind_has_a_source_token_and_none_has_none()
    {
        var defined = Enum.GetValues<EventSourceKind>().Where(kind => kind != EventSourceKind.None);
        var sources = defined
            .Select(kind => CloudEventAttributes.SourceFor(new EventSource(kind, "synthetic", "connection-1")))
            .ToArray();

        Assert.All(sources, source => Assert.StartsWith("/", source, StringComparison.Ordinal));
        Assert.Equal(sources.Length, sources.Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CloudEventAttributes.SourceFor(new EventSource(EventSourceKind.None, "synthetic", "connection-1")));
    }
}
