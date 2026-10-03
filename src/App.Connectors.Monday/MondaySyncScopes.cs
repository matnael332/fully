namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// One monday board as an initial-sync validation reported it (Gate 3F): its id, state, type and hierarchy, and the ids
/// and types of its columns. Untrusted provider data, with no name, title or description: a board is identified by its id.
/// </summary>
public sealed class MondaySyncBoard
{
    internal MondaySyncBoard(string id, MondayItemState state, string? type, string? hierarchyType, IReadOnlyList<MondaySyncColumn> columns)
    {
        Id = id;
        State = state;
        Type = type;
        HierarchyType = hierarchyType;
        Columns = columns;
    }

    /// <summary>Gets the board's monday id.</summary>
    public string Id { get; }

    /// <summary>Gets the board's state, from monday's state enumeration, which boards and items share.</summary>
    public MondayItemState State { get; }

    /// <summary>Gets the board's type as monday names it, or null when monday reported none.</summary>
    public string? Type { get; }

    /// <summary>Gets the board's hierarchy type as monday names it, or null when monday reported none.</summary>
    public string? HierarchyType { get; }

    /// <summary>Gets the board's columns, each at most once.</summary>
    public IReadOnlyList<MondaySyncColumn> Columns { get; }

    /// <summary>Identifies the board by id only.</summary>
    public override string ToString() => $"MondaySyncBoard({Id})";
}

/// <summary>One column of a board: its id and its type as monday names it. Never its title.</summary>
public sealed class MondaySyncColumn
{
    internal MondaySyncColumn(string columnId, string columnType)
    {
        ColumnId = columnId;
        ColumnType = columnType;
    }

    /// <summary>Gets the column's id.</summary>
    public string ColumnId { get; }

    /// <summary>Gets the column's type as monday names it.</summary>
    public string ColumnType { get; }

    /// <summary>Identifies the column by id and type only.</summary>
    public override string ToString() => $"MondaySyncColumn({ColumnId}, {ColumnType})";
}

/// <summary>One page of an initial-sync status listing. Untrusted provider data.</summary>
public sealed class MondayStatusPage
{
    internal MondayStatusPage(string? boardId, string? cursor, IReadOnlyList<MondayItemStatus> items)
    {
        BoardId = boardId;
        Cursor = cursor;
        Items = items;
    }

    /// <summary>Gets the id of the board the first page was read from; null on a next page, which names no board.</summary>
    public string? BoardId { get; }

    /// <summary>Gets the opaque cursor of the next page, or null when this is the last page. Never rendered.</summary>
    internal string? Cursor { get; }

    /// <summary>Gets the page's items, each at most once.</summary>
    public IReadOnlyList<MondayItemStatus> Items { get; }

    /// <summary>Identifies the page by size only; the cursor is never rendered.</summary>
    public override string ToString() => $"MondayStatusPage({Items.Count} items, more: {Cursor is not null})";
}
