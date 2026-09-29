namespace WorkContinuity.Connectors.Monday;

/// <summary>The state monday reports for an item.</summary>
public enum MondayItemState
{
    /// <summary>Unspecified. Never produced: an unknown state makes the response unsupported.</summary>
    Unspecified = 0,

    /// <summary>The item is active.</summary>
    Active = 1,

    /// <summary>The item is archived.</summary>
    Archived = 2,

    /// <summary>The item is deleted.</summary>
    Deleted = 3,
}

/// <summary>
/// One monday item as the API reported it at the time of the read. Untrusted provider data.
/// </summary>
/// <remarks>
/// <para>
/// The identifiers are checked to be well-formed provider identifiers. Everything monday users can write — the name
/// and every column's text and raw value — is untrusted data (invariant 25), carried exactly as received: never
/// trimmed, truncated, interpreted or obeyed. Mapping it onto canonical events, and validating it against the event
/// limits, is the normalizer's work (sub-gate 3D).
/// </para>
/// <para>
/// The string form names the item by id only, so logging an item cannot leak its content.
/// </para>
/// </remarks>
public sealed class MondayItem
{
    /// <summary>Creates an item.</summary>
    /// <exception cref="ArgumentNullException">A required component is null.</exception>
    public MondayItem(
        string id,
        string name,
        MondayItemState state,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt,
        string? boardId,
        string? groupId,
        IReadOnlyList<MondayColumnValue> columnValues)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(columnValues);

        Id = id;
        Name = name;
        State = state;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        BoardId = boardId;
        GroupId = groupId;
        ColumnValues = columnValues;
    }

    /// <summary>Gets the item's monday id.</summary>
    public string Id { get; }

    /// <summary>Gets the item's name. Untrusted provider text.</summary>
    public string Name { get; }

    /// <summary>Gets the item's state.</summary>
    public MondayItemState State { get; }

    /// <summary>Gets when the item was created, when monday said.</summary>
    public DateTimeOffset? CreatedAt { get; }

    /// <summary>Gets monday's last-update time for the item, when monday said. monday exposes no item revision.</summary>
    public DateTimeOffset? UpdatedAt { get; }

    /// <summary>Gets the id of the item's board, when monday reported it.</summary>
    public string? BoardId { get; }

    /// <summary>Gets the id of the item's group, when monday reported it.</summary>
    public string? GroupId { get; }

    /// <summary>Gets the item's column values.</summary>
    public IReadOnlyList<MondayColumnValue> ColumnValues { get; }

    /// <summary>Identifies the item by id only.</summary>
    public override string ToString() => $"MondayItem({Id})";
}

/// <summary>One column value of a monday item. Untrusted provider data.</summary>
public sealed class MondayColumnValue
{
    /// <summary>Creates a column value.</summary>
    /// <exception cref="ArgumentNullException">A required component is null.</exception>
    public MondayColumnValue(string columnId, string columnType, string? text, string? rawValue)
    {
        ArgumentNullException.ThrowIfNull(columnId);
        ArgumentNullException.ThrowIfNull(columnType);

        ColumnId = columnId;
        ColumnType = columnType;
        Text = text;
        RawValue = rawValue;
    }

    /// <summary>Gets the column's id.</summary>
    public string ColumnId { get; }

    /// <summary>Gets the column's type as monday names it.</summary>
    public string ColumnType { get; }

    /// <summary>Gets the value's text form, when the column type has one. Untrusted provider text.</summary>
    public string? Text { get; }

    /// <summary>Gets the value's raw JSON exactly as received, when the column has a value. Untrusted provider data.</summary>
    public string? RawValue { get; }

    /// <summary>Identifies the column value by column only.</summary>
    public override string ToString() => $"MondayColumnValue({ColumnId}, {ColumnType})";
}

/// <summary>The items one per-item reread returned.</summary>
public sealed class MondayItemBatch
{
    /// <summary>Creates a batch.</summary>
    /// <exception cref="ArgumentNullException">The items are null.</exception>
    public MondayItemBatch(IReadOnlyList<MondayItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    /// <summary>Gets the items. An id monday did not return is simply absent.</summary>
    public IReadOnlyList<MondayItem> Items { get; }

    /// <summary>Identifies the batch by size only.</summary>
    public override string ToString() => $"MondayItemBatch({Items.Count} items)";
}

/// <summary>One page of an item listing.</summary>
public sealed class MondayItemsPage
{
    /// <summary>Creates a page.</summary>
    /// <exception cref="ArgumentNullException">The items are null.</exception>
    public MondayItemsPage(string? cursor, IReadOnlyList<MondayItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Cursor = cursor;
        Items = items;
    }

    /// <summary>Gets the opaque cursor for the next page, or null when this is the last page.</summary>
    public string? Cursor { get; }

    /// <summary>Gets the page's items.</summary>
    public IReadOnlyList<MondayItem> Items { get; }

    /// <summary>Gets a value indicating whether another page follows.</summary>
    public bool HasMore => Cursor is not null;

    /// <summary>Identifies the page by size only; the cursor is never rendered.</summary>
    public override string ToString() => $"MondayItemsPage({Items.Count} items, more: {HasMore})";
}
