using System.Globalization;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// Reads the <c>data</c> of a successful response into the typed provider data. Strict: a shape it does not expect is
/// an unsupported response, never a guess.
/// </summary>
/// <remarks>
/// Identifiers must be well-formed provider identifiers, the state one monday documents, and times parseable
/// timestamps. A response may not return more items than were asked for, and a per-item reread may not return an item
/// that was not asked for, so no data about another object can slip into a result. Content — names, column text and
/// raw values — is carried exactly as received.
/// </remarks>
internal static class MondayResponseParser
{
    internal static ParseOutcome<MondayItemBatch> ItemsById(JsonElement data, IReadOnlyCollection<string> requestedIds)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return ParseOutcome<MondayItemBatch>.Unsupported;
        }

        var parsed = Items(items, requestedIds.Count);
        if (parsed is null)
        {
            return ParseOutcome<MondayItemBatch>.Unsupported;
        }

        var requested = new HashSet<string>(requestedIds, StringComparer.Ordinal);
        foreach (var item in parsed)
        {
            if (!requested.Contains(item.Id))
            {
                return ParseOutcome<MondayItemBatch>.Unsupported;
            }
        }

        return ParseOutcome<MondayItemBatch>.Parsed(new MondayItemBatch(parsed));
    }

    internal static ParseOutcome<MondayItemsPage> BoardItemsPage(JsonElement data, int limit)
    {
        if (!data.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Array)
        {
            return ParseOutcome<MondayItemsPage>.Unsupported;
        }

        // A board that does not exist or is not visible to the identity is simply absent from the list.
        switch (boards.GetArrayLength())
        {
            case 0:
                return ParseOutcome<MondayItemsPage>.NotFound;
            case > 1:
                return ParseOutcome<MondayItemsPage>.Unsupported;
        }

        var board = boards[0];
        return board.ValueKind == JsonValueKind.Object && board.TryGetProperty("items_page", out var page)
            ? Page(page, limit)
            : ParseOutcome<MondayItemsPage>.Unsupported;
    }

    internal static ParseOutcome<MondayItemsPage> NextItemsPage(JsonElement data, int limit) =>
        data.TryGetProperty("next_items_page", out var page)
            ? Page(page, limit)
            : ParseOutcome<MondayItemsPage>.Unsupported;

    /// <summary>
    /// The delegating user and account of <c>me { id account { id } }</c>: both must be monday's numeric ids, so no
    /// email address or display name can become an identity.
    /// </summary>
    internal static ParseOutcome<MondayAuthorizingIdentity> AuthorizingIdentity(JsonElement data)
    {
        if (!data.TryGetProperty("me", out var me)
            || me.ValueKind != JsonValueKind.Object
            || NumericId(me, "id") is not { } userId
            || !me.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object
            || NumericId(account, "id") is not { } accountId)
        {
            return ParseOutcome<MondayAuthorizingIdentity>.Unsupported;
        }

        return ParseOutcome<MondayAuthorizingIdentity>.Parsed(new MondayAuthorizingIdentity(userId, accountId));
    }

    private static string? NumericId(JsonElement parent, string name) =>
        Identifier(parent, name) is { Length: <= 20 } id && id.All(char.IsAsciiDigit) ? id : null;

    private static ParseOutcome<MondayItemsPage> Page(JsonElement page, int limit)
    {
        if (page.ValueKind != JsonValueKind.Object
            || !page.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || !page.TryGetProperty("cursor", out var cursorElement))
        {
            return ParseOutcome<MondayItemsPage>.Unsupported;
        }

        string? cursor;
        switch (cursorElement.ValueKind)
        {
            case JsonValueKind.Null:
                cursor = null;
                break;
            case JsonValueKind.String when EventText.IsReference(cursorElement.GetString()):
                cursor = cursorElement.GetString();
                break;
            default:
                return ParseOutcome<MondayItemsPage>.Unsupported;
        }

        var parsed = Items(items, limit);
        return parsed is null
            ? ParseOutcome<MondayItemsPage>.Unsupported
            : ParseOutcome<MondayItemsPage>.Parsed(new MondayItemsPage(cursor, parsed));
    }

    private static List<MondayItem>? Items(JsonElement items, int maximum)
    {
        if (items.GetArrayLength() > maximum)
        {
            return null;
        }

        var parsed = new List<MondayItem>(items.GetArrayLength());
        foreach (var element in items.EnumerateArray())
        {
            var item = Item(element);
            if (item is null)
            {
                return null;
            }

            parsed.Add(item);
        }

        return parsed;
    }

    private static MondayItem? Item(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || Identifier(element, "id") is not { } id
            || !TryString(element, "name", out var name)
            || !TryState(element, out var state)
            || !TryTime(element, "created_at", out var createdAt)
            || !TryTime(element, "updated_at", out var updatedAt)
            || !TryNestedId(element, "board", out var boardId)
            || !TryNestedId(element, "group", out var groupId)
            || !TryColumnValues(element, out var columnValues))
        {
            return null;
        }

        return new MondayItem(id, name, state, createdAt, updatedAt, boardId, groupId, columnValues);
    }

    private static bool TryColumnValues(JsonElement item, out IReadOnlyList<MondayColumnValue> columnValues)
    {
        columnValues = [];
        if (!item.TryGetProperty("column_values", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<MondayColumnValue>(list.GetArrayLength());
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || Identifier(entry, "id") is not { } columnId
                || Identifier(entry, "type") is not { } columnType
                || !TryNullableString(entry, "text", out var text)
                || !entry.TryGetProperty("value", out var value))
            {
                return false;
            }

            parsed.Add(new MondayColumnValue(
                columnId,
                columnType,
                text,
                value.ValueKind == JsonValueKind.Null ? null : value.GetRawText()));
        }

        columnValues = parsed;
        return true;
    }

    private static string? Identifier(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
        && EventText.IsIdentifier(property.GetString())
            ? property.GetString()
            : null;

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static bool TryNullableString(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = property.GetString();
                return true;
            default:
                return false;
        }
    }

    private static bool TryState(JsonElement item, out MondayItemState state)
    {
        state = MondayItemState.Unspecified;
        if (!item.TryGetProperty("state", out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        state = property.GetString() switch
        {
            "active" => MondayItemState.Active,
            "archived" => MondayItemState.Archived,
            "deleted" => MondayItemState.Deleted,
            _ => MondayItemState.Unspecified,
        };

        return state != MondayItemState.Unspecified;
    }

    private static bool TryTime(JsonElement parent, string name, out DateTimeOffset? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String when DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryNestedId(JsonElement parent, string name, out string? id)
    {
        id = null;
        if (!parent.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Object when Identifier(property, "id") is { } nested:
                id = nested;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>What a parser made of a successful response's data.</summary>
internal readonly record struct ParseOutcome<TValue>(TValue? Value, ConnectorFailureKind FailureKind)
{
    internal static ParseOutcome<TValue> Unsupported => new(default, ConnectorFailureKind.UnsupportedResponse);

    internal static ParseOutcome<TValue> NotFound => new(default, ConnectorFailureKind.ResourceNotFound);

    internal bool IsParsed => FailureKind == ConnectorFailureKind.Unspecified;

    internal static ParseOutcome<TValue> Parsed(TValue value) => new(value, ConnectorFailureKind.Unspecified);
}
