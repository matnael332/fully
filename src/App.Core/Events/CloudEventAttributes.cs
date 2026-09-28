using System.Globalization;

namespace WorkContinuity.Core.Events;

/// <summary>
/// The mapping of a canonical event onto CloudEvents 1.0 context attributes and product extension
/// attributes.
/// </summary>
/// <remarks>
/// <para>
/// This is what "CloudEvents-compatible envelope plus product-specific extensions" (PRD §11) means in
/// code. The required context attributes are <c>id</c>, <c>source</c>, <c>specversion</c> and
/// <c>type</c>; <c>subject</c> and <c>time</c> are optional context attributes. Every product field that
/// CloudEvents does not define becomes an extension whose name follows the CloudEvents naming rule:
/// lowercase ASCII letters and digits only, at most 20 characters.
/// </para>
/// <para>
/// Only <see cref="CanonicalEventMetadata"/> is mapped. The attributes never carry work content, so
/// they are safe to use as transport metadata; the content travels as event data and expires with it.
/// </para>
/// </remarks>
public static class CloudEventAttributes
{
    /// <summary>The CloudEvents specification version this mapping follows.</summary>
    public const string SpecVersionValue = "1.0";

    /// <summary>Gets every context attribute name used by the mapping.</summary>
    public static IReadOnlyList<string> ContextNames { get; } =
    [
        Context.Id,
        Context.Source,
        Context.SpecVersion,
        Context.Type,
        Context.Subject,
        Context.Time,
    ];

    /// <summary>Gets every product extension attribute name.</summary>
    public static IReadOnlyList<string> ExtensionNames { get; } =
    [
        Extension.TenantId,
        Extension.SchemaVersion,
        Extension.SourceEventId,
        Extension.SourceRevision,
        Extension.SourceUpdatedTime,
        Extension.ReceivedTime,
        Extension.ProcessedTime,
        Extension.CorrelationId,
        Extension.CausationId,
    ];

    /// <summary>Maps a validated event's metadata onto CloudEvents attributes.</summary>
    /// <remarks>
    /// Optional attributes are omitted when the event does not have them; no value is invented.
    /// Instants use the RFC 3339 UTC form CloudEvents requires.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> For(CanonicalEvent canonicalEvent)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        var metadata = canonicalEvent.Metadata;

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Context.Id] = metadata.EventId.ToString(),
            [Context.Source] = SourceFor(metadata.Source),
            [Context.SpecVersion] = SpecVersionValue,
            [Context.Type] = metadata.Type,
            [Context.Subject] = SubjectFor(metadata.WorkObject),
            [Extension.TenantId] = metadata.TenantId.ToString(),
            [Extension.SchemaVersion] = metadata.SchemaVersion,
            [Extension.ReceivedTime] = Timestamp(metadata.ReceivedAtUtc),
            [Extension.CorrelationId] = metadata.CorrelationId.Value,
        };

        AddInstant(attributes, Context.Time, metadata.OccurredAtUtc);
        AddInstant(attributes, Extension.SourceUpdatedTime, metadata.SourceUpdatedAtUtc);
        AddInstant(attributes, Extension.ProcessedTime, metadata.ProcessedAtUtc);

        if (metadata.SourceEventId is not null)
        {
            attributes[Extension.SourceEventId] = metadata.SourceEventId;
        }

        if (metadata.SourceRevision is { } revision)
        {
            attributes[Extension.SourceRevision] = revision.ToString(CultureInfo.InvariantCulture);
        }

        if (metadata.CausationId is { } causationId)
        {
            attributes[Extension.CausationId] = causationId.ToString("D");
        }

        return attributes.AsReadOnly();
    }

    /// <summary>
    /// Renders a source as the CloudEvents <c>source</c> URI-reference:
    /// <c>/{kind}/{system}/{escaped connection}</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The source kind is not a defined kind.</exception>
    public static string SourceFor(EventSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return $"/{KindToken(source.Kind)}/{source.SourceSystem}/{Uri.EscapeDataString(source.ConnectionReference)}";
    }

    /// <summary>
    /// Renders a work object as the CloudEvents <c>subject</c>:
    /// <c>{system}/{escaped connection}/{type}/{escaped id}</c>.
    /// </summary>
    public static string SubjectFor(ObjectReference workObject)
    {
        ArgumentNullException.ThrowIfNull(workObject);
        return $"{workObject.SourceSystem}/{Uri.EscapeDataString(workObject.ConnectionReference)}/"
            + $"{workObject.ObjectType}/{Uri.EscapeDataString(workObject.SourceObjectId)}";
    }

    private static string KindToken(EventSourceKind kind) => kind switch
    {
        EventSourceKind.SaasConnector => "saas-connector",
        EventSourceKind.LocalAgent => "local-agent",
        EventSourceKind.Employee => "employee",
        EventSourceKind.IdentityProvider => "identity-provider",
        EventSourceKind.ActionExecutor => "action-executor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "The source kind is not a defined kind."),
    };

    private static void AddInstant(Dictionary<string, string> attributes, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
        {
            attributes[name] = Timestamp(instant);
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>CloudEvents 1.0 context attribute names.</summary>
    public static class Context
    {
        /// <summary>The event id. Required.</summary>
        public const string Id = "id";

        /// <summary>The context in which the event happened. Required.</summary>
        public const string Source = "source";

        /// <summary>The CloudEvents specification version. Required.</summary>
        public const string SpecVersion = "specversion";

        /// <summary>The event type. Required.</summary>
        public const string Type = "type";

        /// <summary>The subject of the event within its source. Optional.</summary>
        public const string Subject = "subject";

        /// <summary>When the occurrence happened. Optional.</summary>
        public const string Time = "time";
    }

    /// <summary>Product extension attribute names.</summary>
    public static class Extension
    {
        /// <summary>The owning tenant.</summary>
        public const string TenantId = "tenantid";

        /// <summary>The schema version of the event type.</summary>
        public const string SchemaVersion = "schemaversion";

        /// <summary>The provider's event id.</summary>
        public const string SourceEventId = "sourceeventid";

        /// <summary>The provider's revision or sequence for the object.</summary>
        public const string SourceRevision = "sourcerevision";

        /// <summary>The authoritative source's update time.</summary>
        public const string SourceUpdatedTime = "sourceupdatedtime";

        /// <summary>When the payload was durably received.</summary>
        public const string ReceivedTime = "receivedtime";

        /// <summary>When the event was processed.</summary>
        public const string ProcessedTime = "processedtime";

        /// <summary>The correlation id.</summary>
        public const string CorrelationId = "correlationid";

        /// <summary>The causation id.</summary>
        public const string CausationId = "causationid";
    }
}
