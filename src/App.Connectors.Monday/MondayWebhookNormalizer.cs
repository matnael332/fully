using System.Globalization;
using System.Text.Json;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Connectors.Monday;

/// <summary>The fixed, content-free refusals of the monday webhook normalizer (Gate 3D). None echoes the body.</summary>
public static class MondayWebhookNormalizationErrors
{
    /// <summary>The body is not in a documented shape: bad JSON, a missing or mistyped member, or an invalid id or time.</summary>
    public static Error Malformed { get; } =
        Error.Validation("monday.webhook.malformed", "The monday webhook body is not in a documented shape.");

    /// <summary>The event type or shape is not supported in Gate 3.</summary>
    public static Error UnsupportedEvent { get; } =
        Error.Validation("monday.webhook.unsupported_event", "The monday webhook event is not supported in Gate 3.");

    /// <summary>The column type has no canonical mapping.</summary>
    public static Error UnsupportedColumn { get; } =
        Error.Validation("monday.webhook.unsupported_column", "The monday column type has no canonical mapping.");

    /// <summary>The value is not a valid canonical value; it is refused, never cut.</summary>
    public static Error UnsupportedValue { get; } =
        Error.Validation("monday.webhook.unsupported_value", "The monday value is not a valid canonical value.");
}

/// <summary>
/// The production monday <see cref="ISourceEventNormalizer"/> (Gate 3D): maps a durable webhook receipt onto the
/// canonical object-changed event as a change signal whose values stay unconfirmed until an authoritative reread.
/// </summary>
/// <remarks>
/// <para>
/// A webhook's token proves the sender, not each value in its body, so no value of a webhook may become Snapshot state
/// (decision D3D-1): every event this normalizer produces declares <see cref="SourceValueConfirmation.Unconfirmed"/>,
/// and processing holds its object for reconciliation. The body is parsed again as hostile input: strict JSON without
/// comments, trailing commas or duplicate properties, nested at most 64 levels. It is classified by its documented
/// shape only, never by the name of the event it was registered for.
/// </para>
/// <para>
/// One shape is mapped: a column change (<c>update_column_value</c>) of a status column (<c>color</c>), without a
/// subitem's parent fields. The object is the monday item, by its id; the field is the column, by its id, as
/// <see cref="FactDataType.TaskStatus"/>; the values are the status labels, as untrusted hints; the actor is the monday
/// user id, left unresolved; <c>changedAt</c> is the source update time and <c>triggerTime</c> the occurrence time;
/// there is no revision. Ids are read from the JSON token's own text, never as floating-point numbers; numbers and
/// times are read culture-invariantly by exact formats. Names, titles, group ids, label indexes and styles, and
/// <c>originalTriggerUuid</c> are never read as identity, order or value.
/// </para>
/// <para>
/// Everything else is refused with a fixed, content-free code (<see cref="MondayWebhookNormalizationErrors"/>): never
/// guessed, never cut, never an exception. The normalizer is pure and deterministic; it calls nothing, reads no
/// credential, writes nothing and does not log.
/// </para>
/// </remarks>
public sealed class MondayWebhookNormalizer : ISourceEventNormalizer
{
    /// <summary>The canonical object type of a monday item. A subitem is an item with its own id.</summary>
    public const string ObjectType = "item";

    private const string ColumnChange = "update_column_value";
    private const string StatusColumn = "color";
    private const int MaxIdLength = 20;
    private const int MaxSecondsLength = 12;
    private const int MaxFractionDigits = 7;

    // 9999-12-31T23:59:59Z, the last second a timestamp can hold.
    private const long MaxEpochSeconds = 253_402_300_799;

    private static readonly JsonDocumentOptions Parsing = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    // ISO 8601 in UTC as monday sends it, with zero to seven decimals and nothing else.
    private static readonly string[] TriggerTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
    ];

    /// <inheritdoc />
    public string SourceSystem => MondayWebhooks.SourceSystem;

    /// <inheritdoc />
    public bool Supports(string schemaVersion) =>
        string.Equals(schemaVersion, MondayWebhooks.SchemaVersion, StringComparison.Ordinal);

    /// <inheritdoc />
    public Result<NormalizedSourceEvent> Normalize(SourcePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(payload.SourceSystem, SourceSystem, StringComparison.Ordinal) || !Supports(payload.SchemaVersion))
        {
            return Refuse(MondayWebhookNormalizationErrors.Malformed);
        }

        try
        {
            using var document = JsonDocument.Parse(payload.Payload, Parsing);
            return Map(document.RootElement, payload);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not JSON, too deep, a repeated property, or text that cannot be decoded.
            return Refuse(MondayWebhookNormalizationErrors.Malformed);
        }
    }

    private static Result<NormalizedSourceEvent> Map(JsonElement root, SourcePayload payload)
    {
        if (root.ValueKind != JsonValueKind.Object
            || root.TryGetProperty("challenge", out _)
            || !root.TryGetProperty("event", out var delivery)
            || delivery.ValueKind != JsonValueKind.Object
            || !delivery.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !TriggerMatchesReceipt(delivery, payload.SourceEventId))
        {
            return Refuse(MondayWebhookNormalizationErrors.Malformed);
        }

        // Only a column change without a subitem's parent fields has a documented shape that maps. A subitem's column
        // change is not documented, so its parent fields make the shape unsupported rather than guessed.
        if (!type.ValueEquals(ColumnChange)
            || delivery.TryGetProperty("parentItemId", out _)
            || delivery.TryGetProperty("parentItemBoardId", out _)
            || delivery.TryGetProperty("itemId", out _))
        {
            return Refuse(MondayWebhookNormalizationErrors.UnsupportedEvent);
        }

        if (ProviderId(delivery, "pulseId") is not { } itemId
            || ProviderId(delivery, "boardId") is not { } boardId
            || !delivery.TryGetProperty("columnId", out var column)
            || column.ValueKind != JsonValueKind.String
            || column.GetString() is not { } columnId
            || !EventText.IsToken(columnId)
            || !delivery.TryGetProperty("columnType", out var columnType)
            || columnType.ValueKind != JsonValueKind.String
            || !delivery.TryGetProperty("value", out var value)
            || !TryReadActor(delivery, out var actor)
            || !TryReadChangedAt(delivery, out var changedAt)
            || !TryReadTriggerTime(delivery, out var triggerTime))
        {
            return Refuse(MondayWebhookNormalizationErrors.Malformed);
        }

        if (!columnType.ValueEquals(StatusColumn))
        {
            return Refuse(MondayWebhookNormalizationErrors.UnsupportedColumn);
        }

        var after = StatusLabel(value);
        var before = delivery.TryGetProperty("previousValue", out var previous) ? StatusLabel(previous) : Label.NotStated;
        if (after.Kind == LabelKind.Malformed || before.Kind == LabelKind.Malformed)
        {
            return Refuse(MondayWebhookNormalizationErrors.Malformed);
        }

        if (after.Kind == LabelKind.Unsupported || before.Kind == LabelKind.Unsupported)
        {
            return Refuse(MondayWebhookNormalizationErrors.UnsupportedValue);
        }

        return Result<NormalizedSourceEvent>.Success(new NormalizedSourceEvent(
            CanonicalEventSchemas.ObjectChangedType,
            CanonicalEventSchemas.ObjectChangedVersion1,
            new ObjectReference(MondayWebhooks.SourceSystem, payload.ConnectionReference, ObjectType, itemId),
            sourceRevision: null,
            occurredAt: triggerTime,
            sourceUpdatedAt: changedAt,
            new CanonicalEventContent(
                [new FieldChange(columnId, FactDataType.TaskStatus, before.Text, after.Text)],
                actor,
                "board/" + boardId + "/item/" + itemId),
            SourceValueConfirmation.Unconfirmed));
    }

    // The receipt's provider event id is the one 3C stored from this body's triggerUuid: the two must agree exactly,
    // and neither is ever regenerated.
    private static bool TriggerMatchesReceipt(JsonElement delivery, string? sourceEventId)
    {
        if (!delivery.TryGetProperty("triggerUuid", out var trigger) || trigger.ValueKind == JsonValueKind.Null)
        {
            return sourceEventId is null;
        }

        return trigger.ValueKind == JsonValueKind.String && sourceEventId is not null && trigger.ValueEquals(sourceEventId);
    }

    // A monday id as the JSON integer token's own text: 1 to 20 digits, no sign, fraction, exponent or leading zero.
    private static string? ProviderId(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var text = value.GetRawText();
        return text.Length is > 0 and <= MaxIdLength && text[0] != '0' && text.All(char.IsAsciiDigit) ? text : null;
    }

    // The provider actor: none when absent or null, otherwise a monday user id that is never resolved here.
    private static bool TryReadActor(JsonElement delivery, out EventActor? actor)
    {
        actor = null;
        if (!delivery.TryGetProperty("userId", out var user) || user.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (ProviderId(delivery, "userId") is not { } userId)
        {
            return false;
        }

        actor = EventActor.Unresolved(userId);
        return true;
    }

    // The source update time: epoch seconds with at most seven decimals, read from the token's text, never rounded.
    private static bool TryReadChangedAt(JsonElement delivery, out DateTimeOffset? changedAt)
    {
        changedAt = null;
        if (!delivery.TryGetProperty("changedAt", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var text = value.GetRawText();
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];
        if (whole.Length is 0 or > MaxSecondsLength
            || !whole.All(char.IsAsciiDigit)
            || (point >= 0 && (fraction.Length is 0 or > MaxFractionDigits || !fraction.All(char.IsAsciiDigit))))
        {
            return false;
        }

        var seconds = long.Parse(whole, NumberStyles.None, CultureInfo.InvariantCulture);
        if (seconds > MaxEpochSeconds)
        {
            return false;
        }

        var ticks = fraction.Length == 0
            ? 0
            : long.Parse(fraction.PadRight(MaxFractionDigits, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        changedAt = DateTimeOffset.UnixEpoch.AddTicks((seconds * TimeSpan.TicksPerSecond) + ticks);
        return true;
    }

    // The occurrence time: ISO 8601 in UTC, exactly as monday sends it.
    private static bool TryReadTriggerTime(JsonElement delivery, out DateTimeOffset? triggerTime)
    {
        triggerTime = null;
        if (!delivery.TryGetProperty("triggerTime", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(
                value.GetString(),
                TriggerTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        triggerTime = parsed;
        return true;
    }

    // A status value: null (cleared; for a previous value, not stated), or {"label": {"text": …}} whose text is a valid
    // canonical value. Anything else is malformed; text that is not a valid value is unsupported and never cut.
    private static Label StatusLabel(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Label.NotStated;
        }

        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("label", out var label)
            || label.ValueKind != JsonValueKind.Object
            || !label.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            return new Label(LabelKind.Malformed, null);
        }

        var labelText = text.GetString();
        return EventText.IsValue(labelText) ? new Label(LabelKind.Text, labelText) : new Label(LabelKind.Unsupported, null);
    }

    private static Result<NormalizedSourceEvent> Refuse(Error error) => Result<NormalizedSourceEvent>.Failure(error);

    private enum LabelKind
    {
        NotStated,
        Text,
        Malformed,
        Unsupported,
    }

    private readonly record struct Label(LabelKind Kind, string? Text)
    {
        public static Label NotStated { get; } = new(LabelKind.NotStated, null);
    }
}
