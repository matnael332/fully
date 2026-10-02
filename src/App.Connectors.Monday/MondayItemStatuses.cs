namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// One monday item as a status reread reported it (Gate 3E): its id, state and board, and the named columns. Untrusted
/// provider data.
/// </summary>
/// <remarks>
/// Besides the typed values, every value the reader may consume keeps the exact JSON text monday returned, so the
/// evidence of a confirmed read is a copy of the provider's own values, never a rewrite. The string form names the item
/// by id only.
/// </remarks>
public sealed class MondayItemStatus
{
    internal MondayItemStatus(
        string id,
        MondayItemState state,
        string? boardId,
        IReadOnlyList<MondayStatusColumn> columns,
        string rawId,
        string rawState,
        string? rawBoardId)
    {
        Id = id;
        State = state;
        BoardId = boardId;
        Columns = columns;
        RawId = rawId;
        RawState = rawState;
        RawBoardId = rawBoardId;
    }

    /// <summary>Gets the item's monday id.</summary>
    public string Id { get; }

    /// <summary>Gets the item's state.</summary>
    public MondayItemState State { get; }

    /// <summary>Gets the id of the item's board, or null when monday reported none.</summary>
    public string? BoardId { get; }

    /// <summary>Gets the named columns monday returned, at most once each.</summary>
    public IReadOnlyList<MondayStatusColumn> Columns { get; }

    /// <summary>Gets the item id's exact JSON text.</summary>
    internal string RawId { get; }

    /// <summary>Gets the state's exact JSON text.</summary>
    internal string RawState { get; }

    /// <summary>Gets the board id's exact JSON text, or null when monday reported no board.</summary>
    internal string? RawBoardId { get; }

    /// <summary>Identifies the item by id only.</summary>
    public override string ToString() => $"MondayItemStatus({Id})";
}

/// <summary>One column of a status reread. Untrusted provider data.</summary>
public sealed class MondayStatusColumn
{
    internal MondayStatusColumn(
        string columnId,
        string columnType,
        MondayStatusFields? status,
        string rawId,
        string rawType)
    {
        ColumnId = columnId;
        ColumnType = columnType;
        Status = status;
        RawId = rawId;
        RawType = rawType;
    }

    /// <summary>Gets the column's id.</summary>
    public string ColumnId { get; }

    /// <summary>Gets the column's type as monday names it.</summary>
    public string ColumnType { get; }

    /// <summary>
    /// Gets the typed status fields, or null when the <c>StatusValue</c> fragment did not match: the column is not a status
    /// value, for example a rollup status, which monday reports as another type.
    /// </summary>
    public MondayStatusFields? Status { get; }

    /// <summary>Gets the column id's exact JSON text.</summary>
    internal string RawId { get; }

    /// <summary>Gets the type's exact JSON text.</summary>
    internal string RawType { get; }

    /// <summary>Identifies the column by id and type only.</summary>
    public override string ToString() => $"MondayStatusColumn({ColumnId}, {ColumnType})";
}

/// <summary>The typed <c>StatusValue</c> fields of one column, exactly as monday returned them. Untrusted provider data.</summary>
public sealed class MondayStatusFields
{
    internal MondayStatusFields(string? label, long? index, string? updatedAt, string rawLabel, string rawIndex, string rawUpdatedAt)
    {
        Label = label;
        Index = index;
        UpdatedAt = updatedAt;
        RawLabel = rawLabel;
        RawIndex = rawIndex;
        RawUpdatedAt = rawUpdatedAt;
    }

    /// <summary>Gets the label, or null when the status is empty. Untrusted provider text.</summary>
    public string? Label { get; }

    /// <summary>Gets the label's index, or null when the status is empty.</summary>
    public long? Index { get; }

    /// <summary>Gets the value's own update time as monday wrote it, or null. Not yet parsed: the reader parses it strictly.</summary>
    public string? UpdatedAt { get; }

    /// <summary>Gets the label's exact JSON text.</summary>
    internal string RawLabel { get; }

    /// <summary>Gets the index's exact JSON text.</summary>
    internal string RawIndex { get; }

    /// <summary>Gets the update time's exact JSON text.</summary>
    internal string RawUpdatedAt { get; }

    /// <summary>Identifies the fields without their values.</summary>
    public override string ToString() => "MondayStatusFields(values redacted)";
}

/// <summary>The items one status reread returned.</summary>
public sealed class MondayItemStatusBatch
{
    /// <summary>Creates a batch.</summary>
    /// <exception cref="ArgumentNullException">The items are null.</exception>
    internal MondayItemStatusBatch(IReadOnlyList<MondayItemStatus> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    /// <summary>Gets the items, each at most once. An id monday did not return is simply absent.</summary>
    public IReadOnlyList<MondayItemStatus> Items { get; }

    /// <summary>Identifies the batch by size only.</summary>
    public override string ToString() => $"MondayItemStatusBatch({Items.Count} items)";
}
