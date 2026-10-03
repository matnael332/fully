using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Integration.Tests.Connectors;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>One board of the fake account.</summary>
internal sealed class FakeBoard(string id)
{
    internal string Id { get; } = id;

    internal string State { get; set; } = "active";

    internal string? Type { get; set; } = "board";

    internal string? Hierarchy { get; set; } = "classic";

    /// <summary>The columns by id and monday type, in board order.</summary>
    internal List<(string Id, string Type)> Columns { get; } = [];
}

/// <summary>One item of the fake account: its board, its state and its status values by column.</summary>
internal sealed class FakeItem(string id, string board)
{
    internal string Id { get; } = id;

    internal string Board { get; set; } = board;

    internal string State { get; set; } = "active";

    /// <summary>The board monday names in the item's answer, when it differs from the board that lists it.</summary>
    internal string? AnsweredBoard { get; set; }

    /// <summary>The status values by column: the label, null when the status is empty, and the value's own update time.</summary>
    internal Dictionary<string, (string? Label, DateTimeOffset? UpdatedAt)> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>Columns monday answers with exactly this JSON instead, such as a rollup whose fragment does not match.</summary>
    internal Dictionary<string, string> RawColumns { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A deterministic, stateful fake of one monday account, answering exactly the read documents the connector sends: the
/// board validation and the status pages of initial sync, and the status reread of reconciliation. Items are listed by
/// numeric id; a listing is recomputed from the account's state on every page, as a live account changes under a scan;
/// cursors are opaque, and a test can expire them. A test can change the account, or answer for it, before any call,
/// as a user editing monday or an outage would.
/// </summary>
internal sealed class FakeMondayWorkspace
{
    internal const string BoardTitle = "Board title SENTINEL";
    internal const string ItemName = "Item name SENTINEL";

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Board, int Offset, bool Expired)> _cursors = new(StringComparer.Ordinal);

    internal Dictionary<string, FakeBoard> Boards { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, FakeItem> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>The operations answered so far, in order: board, page, next, reread.</summary>
    internal List<string> Answered { get; } = [];

    /// <summary>The board ids every board validation and first page asked for.</summary>
    internal List<string> BoardsAsked { get; } = [];

    /// <summary>Every cursor issued.</summary>
    internal List<string> IssuedCursors { get; } = [];

    /// <summary>
    /// Runs before each answer with the operation and how many of it were answered before; it may change the account, wait
    /// for work the test interleaves, or return the answer itself.
    /// </summary>
    internal Func<string, int, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }

    /// <summary>
    /// Runs after each answer was computed and before it is returned, with the operation and how many of it were answered
    /// before: what a user changes here happens after monday read the account for that call.
    /// </summary>
    internal Func<string, int, CancellationToken, Task>? AfterAnswer { get; set; }

    /// <summary>Whether an expired cursor answers with no next page at all instead of an error.</summary>
    internal bool ExpiredCursorAnswersNull { get; set; }

    /// <summary>The most items a page holds, when smaller than the limit asked for: monday may answer fewer.</summary>
    internal int? PageSize { get; set; }

    internal FakeBoard AddBoard(string id, params (string Id, string Type)[] columns)
    {
        var board = new FakeBoard(id);
        board.Columns.AddRange(columns.Length == 0 ? [("name", "name"), ("status", "status")] : columns);
        lock (_gate)
        {
            Boards[id] = board;
        }

        return board;
    }

    internal FakeItem AddItem(string id, string board, string? label, DateTimeOffset? updatedAt, string column = "status")
    {
        var item = new FakeItem(id, board);
        item.Statuses[column] = (label, updatedAt);
        lock (_gate)
        {
            Items[id] = item;
        }

        return item;
    }

    internal void SetStatus(string item, string? label, DateTimeOffset? updatedAt, string column = "status")
    {
        lock (_gate)
        {
            Items[item].Statuses[column] = (label, updatedAt);
        }
    }

    internal void ExpireCursors()
    {
        lock (_gate)
        {
            foreach (var cursor in _cursors.Keys.ToList())
            {
                _cursors[cursor] = _cursors[cursor] with { Expired = true };
            }
        }
    }

    internal int Count(string operation)
    {
        lock (_gate)
        {
            return Answered.Count(answered => answered == operation);
        }
    }

    internal async Task<HttpResponseMessage> AnswerAsync(string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var query = document.RootElement.GetProperty("query").GetString();
        var variables = document.RootElement.TryGetProperty("variables", out var values) ? values.Clone() : default;
        var operation = query switch
        {
            MondayQueries.ReadSyncBoard => "board",
            MondayQueries.ReadSyncStatusPage => "page",
            MondayQueries.ReadNextSyncStatusPage => "next",
            MondayQueries.ReadItemStatusesById => "reread",
            _ => throw new InvalidOperationException("The fake account was sent a document the connector does not send."),
        };

        int before;
        lock (_gate)
        {
            before = Answered.Count(answered => answered == operation);
            Answered.Add(operation);
        }

        if (Intercept is { } intercept && await intercept(operation, before, cancellationToken) is { } answered)
        {
            return answered;
        }

        HttpResponseMessage response;
        lock (_gate)
        {
            response = FakeMonday.Json(HttpStatusCode.OK, operation switch
            {
                "board" => Board(variables),
                "page" => FirstPage(variables),
                "next" => NextPage(variables),
                _ => Reread(variables),
            });
        }

        if (AfterAnswer is { } after)
        {
            await after(operation, before, cancellationToken);
        }

        return response;
    }

    internal static HttpResponseMessage Error(string code, int status = 200) =>
        FakeMonday.Json((HttpStatusCode)status, "{\"errors\":[{\"message\":\"An error.\",\"extensions\":{\"code\":\"" + code + "\",\"status_code\":" + status + "}}],\"account_id\":9001}");

    internal static string Iso(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    private string Board(JsonElement variables)
    {
        var id = variables.GetProperty("boardId").GetString()!;
        BoardsAsked.Add(id);
        if (!Boards.TryGetValue(id, out var board))
        {
            return "{\"data\":{\"boards\":[]},\"account_id\":9001}";
        }

        var columns = string.Join(",", board.Columns.Select(column => "{\"id\":\"" + column.Id + "\",\"type\":\"" + column.Type + "\",\"title\":\"" + BoardTitle + "\"}"));
        return "{\"data\":{\"boards\":[{\"id\":\"" + board.Id + "\",\"name\":\"" + BoardTitle + "\",\"state\":\"" + board.State + "\",\"type\":" + Text(board.Type)
            + ",\"hierarchy_type\":" + Text(board.Hierarchy) + ",\"columns\":[" + columns + "]}]},\"account_id\":9001}";
    }

    private string FirstPage(JsonElement variables)
    {
        var board = variables.GetProperty("boardId").GetString()!;
        BoardsAsked.Add(board);

        // boards(ids:) without a state answers active boards only.
        if (!Boards.TryGetValue(board, out var found) || found.State != "active")
        {
            return "{\"data\":{\"boards\":[]},\"account_id\":9001}";
        }

        return "{\"data\":{\"boards\":[{\"id\":\"" + board + "\",\"items_page\":" + Page(board, 0, variables) + "}]},\"account_id\":9001}";
    }

    private string NextPage(JsonElement variables)
    {
        var cursor = variables.GetProperty("cursor").GetString()!;
        if (!_cursors.TryGetValue(cursor, out var position) || position.Expired)
        {
            return ExpiredCursorAnswersNull
                ? "{\"data\":{\"next_items_page\":null},\"account_id\":9001}"
                : "{\"errors\":[{\"message\":\"CursorExpiredError\",\"extensions\":{\"code\":\"InvalidArgumentException\",\"status_code\":400}}],\"account_id\":9001}";
        }

        return "{\"data\":{\"next_items_page\":" + Page(position.Board, position.Offset, variables) + "},\"account_id\":9001}";
    }

    private string Page(string board, int offset, JsonElement variables)
    {
        var limit = Math.Min(variables.GetProperty("limit").GetInt32(), PageSize ?? int.MaxValue);
        var columns = variables.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!).ToArray();
        var listing = Items.Values
            .Where(item => item.Board == board && item.State == "active")
            .OrderBy(item => long.Parse(item.Id, CultureInfo.InvariantCulture))
            .ToList();
        var page = listing.Skip(offset).Take(limit).ToList();
        string cursor = "null";
        if (offset + limit < listing.Count)
        {
            var issued = "cursor-" + Guid.NewGuid().ToString("N") + "-SENTINEL";
            _cursors[issued] = (board, offset + limit, false);
            IssuedCursors.Add(issued);
            cursor = "\"" + issued + "\"";
        }

        return "{\"cursor\":" + cursor + ",\"items\":[" + string.Join(",", page.Select(item => Item(item, columns))) + "]}";
    }

    private string Reread(JsonElement variables)
    {
        var ids = variables.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
        var columns = variables.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!).ToArray();
        var items = ids.Where(Items.ContainsKey).Select(id => Item(Items[id], columns));
        return "{\"data\":{\"items\":[" + string.Join(",", items) + "]},\"account_id\":9001}";
    }

    // An item as monday answers it: its id, state and board, and each requested column its board has, with the typed
    // status fields when the column is a status; monday's own name for the item is returned too, and never consumed.
    private string Item(FakeItem item, string[] columns)
    {
        var board = Boards.TryGetValue(item.Board, out var found) ? found : null;
        var values = new List<string>();
        foreach (var column in columns)
        {
            if (item.RawColumns.TryGetValue(column, out var raw))
            {
                values.Add(raw);
                continue;
            }

            var type = board?.Columns.FirstOrDefault(candidate => candidate.Id == column).Type;
            if (type is null)
            {
                continue;
            }

            var value = new StringBuilder("{\"id\":\"" + column + "\",\"type\":\"" + type + "\"");
            if (type == "status")
            {
                var (label, updatedAt) = item.Statuses.TryGetValue(column, out var status) ? status : (null, null);
                value.Append(",\"label\":" + (label is null ? "null" : JsonSerializer.Serialize(label)));
                value.Append(",\"index\":" + (label is null ? "null" : "3"));
                value.Append(",\"updated_at\":" + (updatedAt is { } at ? "\"" + Iso(at) + "\"" : "null"));
            }

            values.Add(value.Append('}').ToString());
        }

        return "{\"id\":\"" + item.Id + "\",\"name\":\"" + ItemName + "\",\"state\":\"" + item.State + "\",\"board\":{\"id\":\"" + (item.AnsweredBoard ?? item.Board)
            + "\"},\"column_values\":[" + string.Join(",", values) + "]}";
    }

    private static string Text(string? value) => value is null ? "null" : "\"" + value + "\"";

    /// <summary>The item ids the status rereads so far asked for; initial sync's reads name no item.</summary>
    internal static List<string> RereadItemIds(FakeMonday monday) =>
        monday.Requests
            .Where(request => request.Uri == MondayEndpoints.Api)
            .SelectMany(request =>
            {
                using var body = JsonDocument.Parse(request.Body);
                return body.RootElement.GetProperty("query").GetString() == MondayQueries.ReadItemStatusesById
                    ? body.RootElement.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToList()
                    : [];
            })
            .ToList();
}
