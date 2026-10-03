using System.Globalization;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.InitialSync;

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

    /// <summary>
    /// A status reread (Gate 3E). Strict: an unrequested or repeated item, an unknown state, a board that is neither null
    /// nor an object with an id, an unrequested or repeated column, or a <c>StatusValue</c> fragment with only some of its
    /// fields, makes the whole answer unsupported, so no value can be bound to anything but what was asked.
    /// </summary>
    internal static ParseOutcome<MondayItemStatusBatch> ItemStatuses(
        JsonElement data,
        IReadOnlyCollection<string> requestedIds,
        IReadOnlyCollection<string> requestedColumns)
    {
        if (!data.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > requestedIds.Count)
        {
            return ParseOutcome<MondayItemStatusBatch>.Unsupported;
        }

        var wantedItems = new HashSet<string>(requestedIds, StringComparer.Ordinal);
        var wantedColumns = new HashSet<string>(requestedColumns, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<MondayItemStatus>(items.GetArrayLength());
        foreach (var element in items.EnumerateArray())
        {
            if (ItemStatus(element, wantedColumns) is not { } item || !wantedItems.Contains(item.Id) || !seen.Add(item.Id))
            {
                return ParseOutcome<MondayItemStatusBatch>.Unsupported;
            }

            parsed.Add(item);
        }

        return ParseOutcome<MondayItemStatusBatch>.Parsed(new MondayItemStatusBatch(parsed));
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
    /// An initial-sync board validation (Gate 3F). Strict: anything but one board object with an id, a documented state, a
    /// type and a hierarchy that are each a string or null, and a list of distinct columns each with an id and a type, is
    /// unsupported. A board monday did not return is not found.
    /// </summary>
    internal static ParseOutcome<MondaySyncBoard> SyncBoard(JsonElement data)
    {
        if (!data.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Array)
        {
            return ParseOutcome<MondaySyncBoard>.Unsupported;
        }

        switch (boards.GetArrayLength())
        {
            case 0:
                return ParseOutcome<MondaySyncBoard>.NotFound;
            case > 1:
                return ParseOutcome<MondaySyncBoard>.Unsupported;
        }

        var board = boards[0];
        if (board.ValueKind != JsonValueKind.Object
            || Identifier(board, "id") is not { } id
            || !TryState(board, out var state)
            || !TryNullableIdentifier(board, "type", out var type)
            || !TryNullableIdentifier(board, "hierarchy_type", out var hierarchyType)
            || !board.TryGetProperty("columns", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return ParseOutcome<MondaySyncBoard>.Unsupported;
        }

        var columns = new List<MondaySyncColumn>(list.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || Identifier(entry, "id") is not { } columnId
                || Identifier(entry, "type") is not { } columnType
                || !seen.Add(columnId))
            {
                return ParseOutcome<MondaySyncBoard>.Unsupported;
            }

            columns.Add(new MondaySyncColumn(columnId, columnType));
        }

        return ParseOutcome<MondaySyncBoard>.Parsed(new MondaySyncBoard(id, state, type, hierarchyType, columns));
    }

    /// <summary>
    /// The first page of an initial-sync status listing (Gate 3F): one board object with its id and its items page. A board
    /// monday did not return is not found.
    /// </summary>
    internal static ParseOutcome<MondayStatusPage> SyncStatusPage(JsonElement data, int limit, IReadOnlyCollection<string> requestedColumns)
    {
        if (!data.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Array)
        {
            return ParseOutcome<MondayStatusPage>.Unsupported;
        }

        switch (boards.GetArrayLength())
        {
            case 0:
                return ParseOutcome<MondayStatusPage>.NotFound;
            case > 1:
                return ParseOutcome<MondayStatusPage>.Unsupported;
        }

        var board = boards[0];
        return board.ValueKind == JsonValueKind.Object
            && Identifier(board, "id") is { } boardId
            && board.TryGetProperty("items_page", out var page)
                ? StatusPage(page, boardId, limit, requestedColumns)
                : ParseOutcome<MondayStatusPage>.Unsupported;
    }

    /// <summary>The next page of an initial-sync status listing (Gate 3F).</summary>
    internal static ParseOutcome<MondayStatusPage> NextSyncStatusPage(JsonElement data, int limit, IReadOnlyCollection<string> requestedColumns) =>
        data.TryGetProperty("next_items_page", out var page)
            ? StatusPage(page, boardId: null, limit, requestedColumns)
            : ParseOutcome<MondayStatusPage>.Unsupported;

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

    // A page of an initial-sync listing: at most the limit of distinct items, each parsed as strictly as a 3E reread, and a
    // cursor that is null or a continuation, never interpreted.
    private static ParseOutcome<MondayStatusPage> StatusPage(JsonElement page, string? boardId, int limit, IReadOnlyCollection<string> requestedColumns)
    {
        if (page.ValueKind != JsonValueKind.Object
            || !page.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > limit
            || !page.TryGetProperty("cursor", out var cursorElement))
        {
            return ParseOutcome<MondayStatusPage>.Unsupported;
        }

        string? cursor;
        switch (cursorElement.ValueKind)
        {
            case JsonValueKind.Null:
                cursor = null;
                break;
            case JsonValueKind.String when ScopeContinuation.IsValid(cursorElement.GetString()):
                cursor = cursorElement.GetString();
                break;
            default:
                return ParseOutcome<MondayStatusPage>.Unsupported;
        }

        var wantedColumns = new HashSet<string>(requestedColumns, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<MondayItemStatus>(items.GetArrayLength());
        foreach (var element in items.EnumerateArray())
        {
            if (ItemStatus(element, wantedColumns) is not { } item || !seen.Add(item.Id))
            {
                return ParseOutcome<MondayStatusPage>.Unsupported;
            }

            parsed.Add(item);
        }

        return ParseOutcome<MondayStatusPage>.Parsed(new MondayStatusPage(boardId, cursor, parsed));
    }

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

    private static MondayItemStatus? ItemStatus(JsonElement element, HashSet<string> wantedColumns)
    {
        if (element.ValueKind != JsonValueKind.Object
            || Identifier(element, "id") is not { } id
            || !TryState(element, out var state)
            || !element.TryGetProperty("board", out var board)
            || !element.TryGetProperty("column_values", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? boardId = null;
        string? rawBoardId = null;
        switch (board.ValueKind)
        {
            case JsonValueKind.Null:
                break;
            case JsonValueKind.Object when Identifier(board, "id") is { } nested:
                boardId = nested;
                rawBoardId = board.GetProperty("id").GetRawText();
                break;
            default:
                return null;
        }

        var columns = new List<MondayStatusColumn>(list.GetArrayLength());
        var seenColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateArray())
        {
            if (StatusColumn(entry) is not { } column || !wantedColumns.Contains(column.ColumnId) || !seenColumns.Add(column.ColumnId))
            {
                return null;
            }

            columns.Add(column);
        }

        return new MondayItemStatus(
            id, state, boardId, columns, element.GetProperty("id").GetRawText(), element.GetProperty("state").GetRawText(), rawBoardId);
    }

    // A column of a status reread: its id and type, and the StatusValue fields when the fragment matched. A column the
    // fragment did not match has none of them; one with only some of them is not a shape monday documents.
    private static MondayStatusColumn? StatusColumn(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || Identifier(entry, "id") is not { } columnId
            || Identifier(entry, "type") is not { } columnType)
        {
            return null;
        }

        var hasLabel = entry.TryGetProperty("label", out var label);
        var hasIndex = entry.TryGetProperty("index", out var index);
        var hasUpdated = entry.TryGetProperty("updated_at", out var updated);
        MondayStatusFields? status = null;
        if (hasLabel || hasIndex || hasUpdated)
        {
            if (!(hasLabel && hasIndex && hasUpdated)
                || label.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
                || index.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number)
                || updated.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return null;
            }

            long? indexValue = null;
            if (index.ValueKind == JsonValueKind.Number)
            {
                if (!index.TryGetInt64(out var parsedIndex) || parsedIndex < 0)
                {
                    return null;
                }

                indexValue = parsedIndex;
            }

            status = new MondayStatusFields(
                label.ValueKind == JsonValueKind.String ? label.GetString() : null,
                indexValue,
                updated.ValueKind == JsonValueKind.String ? updated.GetString() : null,
                label.GetRawText(),
                index.GetRawText(),
                updated.GetRawText());
        }

        return new MondayStatusColumn(
            columnId, columnType, status, entry.GetProperty("id").GetRawText(), entry.GetProperty("type").GetRawText());
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

    private static bool TryNullableIdentifier(JsonElement parent, string name, out string? value)
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
            case JsonValueKind.String when EventText.IsIdentifier(property.GetString()):
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
