using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Processing;

/// <summary>One field change of a synthetic event.</summary>
internal sealed record SyntheticChange(string Field, string DataType, string? Before, string? After);

/// <summary>
/// Builds the synthetic source's payloads (Gate 2 owner decision 3: a deterministic synthetic source, in tests
/// only). The shape is this test source's own; nothing about it is monday-specific.
/// </summary>
internal sealed record SyntheticEvent
{
    public string ObjectType { get; init; } = "task";

    public string ObjectId { get; init; } = "1001";

    /// <summary>Gets the object's home system and connection, when it is not the delivering connection.</summary>
    public (string System, string Connection)? Home { get; init; }

    public long? Revision { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public string? Actor { get; init; }

    public string? Reference { get; init; }

    /// <summary>Gets a claim the payload makes about its own source, which no normalizer can turn into authority.</summary>
    public string? ClaimedSource { get; init; }

    public string SchemaVersion { get; init; } = CanonicalEventSchemas.ObjectChangedVersion1;

    public IReadOnlyList<SyntheticChange> Changes { get; init; } = [new("status", "TaskStatus", "working", "done")];

    /// <summary>Gets what the payload says about its values; null means confirmed, as every Gate 2 test assumes.</summary>
    public string? ValueConfirmation { get; init; }

    public string ToPayload()
    {
        var root = new JsonObject
        {
            ["kind"] = SyntheticNormalizer.Kind,
            ["schemaVersion"] = SchemaVersion,
            ["object"] = new JsonObject { ["type"] = ObjectType, ["id"] = ObjectId },
            ["changes"] = new JsonArray([.. Changes.Select(change => (JsonNode)new JsonObject
            {
                ["field"] = change.Field,
                ["dataType"] = change.DataType,
                ["before"] = change.Before,
                ["after"] = change.After,
            })]),
        };

        if (Home is { } home)
        {
            root["object"]!["homeSystem"] = home.System;
            root["object"]!["homeConnection"] = home.Connection;
        }

        if (Revision is { } revision)
        {
            root["revision"] = revision;
        }

        if (UpdatedAt is { } updatedAt)
        {
            root["updatedAt"] = updatedAt.ToString("O", CultureInfo.InvariantCulture);
        }

        if (OccurredAt is { } occurredAt)
        {
            root["occurredAt"] = occurredAt.ToString("O", CultureInfo.InvariantCulture);
        }

        if (Actor is not null)
        {
            root["actor"] = Actor;
        }

        if (Reference is not null)
        {
            root["reference"] = Reference;
        }

        if (ClaimedSource is not null)
        {
            root["claimedSource"] = ClaimedSource;
        }

        if (ValueConfirmation is not null)
        {
            root["valueConfirmation"] = ValueConfirmation;
        }

        return root.ToJsonString();
    }
}

/// <summary>
/// The deterministic synthetic normalizer: strict, pure, and able to state only what a source may state. It
/// never sees a tenant and cannot name the event's source; a payload's claim about its own source is read and
/// deliberately discarded.
/// </summary>
internal sealed class SyntheticNormalizer : ISourceEventNormalizer
{
    public const string Kind = "synthetic.object.changed";

    public static Error Refused { get; } = Error.Validation("synthetic.payload.invalid", "The synthetic payload is not well formed.");

    public string SourceSystem => SyntheticSource.System;

    public bool Supports(string schemaVersion) => string.Equals(schemaVersion, SyntheticSource.SchemaVersion, StringComparison.Ordinal);

    public Result<NormalizedSourceEvent> Normalize(SourcePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            var root = JsonNode.Parse(payload.Payload) as JsonObject;
            if (root is null || (string?)root["kind"] != Kind || root["object"] is not JsonObject target || root["changes"] is not JsonArray changes)
            {
                return Result<NormalizedSourceEvent>.Failure(Refused);
            }

            var home = (System: (string?)target["homeSystem"] ?? payload.SourceSystem, Connection: (string?)target["homeConnection"] ?? payload.ConnectionReference);
            var fieldChanges = new List<FieldChange>();
            foreach (var node in changes)
            {
                if (node is not JsonObject change || !Enum.TryParse<FactDataType>((string?)change["dataType"], ignoreCase: false, out var dataType))
                {
                    return Result<NormalizedSourceEvent>.Failure(Refused);
                }

                fieldChanges.Add(new FieldChange((string?)change["field"] ?? string.Empty, dataType, (string?)change["before"], (string?)change["after"]));
            }

            // Read, and deliberately discarded: a payload cannot make itself authoritative.
            _ = (string?)root["claimedSource"];

            return Result<NormalizedSourceEvent>.Success(new NormalizedSourceEvent(
                CanonicalEventSchemas.ObjectChangedType,
                (string?)root["schemaVersion"] ?? string.Empty,
                new ObjectReference(home.System, home.Connection, (string?)target["type"] ?? string.Empty, (string?)target["id"] ?? string.Empty),
                (long?)root["revision"],
                Instant(root["occurredAt"]),
                Instant(root["updatedAt"]),
                new CanonicalEventContent(
                    fieldChanges,
                    (string?)root["actor"] is { } actor ? EventActor.Unresolved(actor) : null,
                    (string?)root["reference"]),
                (string?)root["valueConfirmation"] is { } confirmation
                    ? Enum.Parse<SourceValueConfirmation>(confirmation)
                    : SourceValueConfirmation.Confirmed));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return Result<NormalizedSourceEvent>.Failure(Refused);
        }
    }

    private static DateTimeOffset? Instant(JsonNode? node) =>
        (string?)node is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
}

/// <summary>A normalizer that throws with a message quoting the payload, as a careless one might.</summary>
internal sealed class ThrowingNormalizer : ISourceEventNormalizer
{
    public string SourceSystem => SyntheticSource.System;

    public bool Supports(string schemaVersion) => true;

    public Result<NormalizedSourceEvent> Normalize(SourcePayload payload) =>
        throw new InvalidOperationException("Could not map " + payload.Payload);
}

/// <summary>
/// The synthetic normalizer, tripping a crash switch once it has produced its result, so the pipeline dies after
/// every decision is made and before anything commits.
/// </summary>
internal sealed class CrashingNormalizer(CancellationTokenSource crash) : ISourceEventNormalizer
{
    private readonly SyntheticNormalizer _inner = new();

    public string SourceSystem => _inner.SourceSystem;

    public bool Supports(string schemaVersion) => _inner.Supports(schemaVersion);

    public Result<NormalizedSourceEvent> Normalize(SourcePayload payload)
    {
        var result = _inner.Normalize(payload);
        crash.Cancel();
        return result;
    }
}
