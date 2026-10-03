using WorkContinuity.Core.Connectors;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The typed operations this connector can perform, each with its full declared contract. Gate 3 reads only.
/// </summary>
/// <remarks>
/// Every operation is a READ with no side effect, low risk, no recovery needed, a bounded timeout and the default read
/// retry policy. Nothing here can change monday data: the set is closed, every document is a single read query, and the
/// client checks both before sending anything.
/// </remarks>
public static class MondayOperations
{
    /// <summary>The largest number of items one per-item reread may address (monday's documented maximum).</summary>
    public const int MaxItemsPerRead = 100;

    /// <summary>The largest page of a board's items (monday's documented maximum).</summary>
    public const int MaxPageSize = 500;

    /// <summary>The largest number of distinct columns one status reread may address.</summary>
    public const int MaxColumnsPerRead = 100;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Rereads the current authoritative state of up to 100 items by id.</summary>
    public static ConnectorOperationDescriptor ReadItemsById { get; } = Read(
        "monday.items.read-by-id",
        "Reread the current authoritative state of up to 100 monday items by id.",
        "monday.item-ids.v1",
        "monday.items.v1",
        "monday.item");

    /// <summary>
    /// Rereads the current status values of up to 100 items by id, with each item's state and board (Gate 3E): the typed
    /// <c>StatusValue</c> fields of the named columns only.
    /// </summary>
    public static ConnectorOperationDescriptor ReadItemStatusesById { get; } = Read(
        "monday.items.read-status-by-id",
        "Reread the current status values of up to 100 monday items by id, with each item's state and board.",
        "monday.item-status-read.v1",
        "monday.item-statuses.v1",
        "monday.item");

    /// <summary>Reads the first page of one board's items.</summary>
    public static ConnectorOperationDescriptor ReadBoardItemsPage { get; } = Read(
        "monday.board.items-page",
        "Read the first page of one monday board's items.",
        "monday.board-page.v1",
        "monday.items-page.v1",
        "monday.board");

    /// <summary>Reads the next page of an item listing by its cursor.</summary>
    public static ConnectorOperationDescriptor ReadNextItemsPage { get; } = Read(
        "monday.items.next-page",
        "Read the next page of a monday item listing by its cursor.",
        "monday.page-cursor.v1",
        "monday.items-page.v1",
        "monday.board");

    /// <summary>
    /// Reads one configured board's id, state, type and hierarchy and the ids and types of its columns, to validate it as an
    /// initial-sync scope (Gate 3F): never a name, a title or an item.
    /// </summary>
    public static ConnectorOperationDescriptor ReadSyncBoard { get; } = Read(
        "monday.board.read-sync-scope",
        "Read one monday board's id, state, type, hierarchy and column ids and types, to validate it as an initial-sync scope.",
        "monday.board-id.v1",
        "monday.sync-board.v1",
        "monday.board");

    /// <summary>
    /// Reads the first page of one configured board's active items for initial sync (Gate 3F): each item's id, state and
    /// board, and the typed <c>StatusValue</c> fields of the named status columns only.
    /// </summary>
    public static ConnectorOperationDescriptor ReadSyncStatusPage { get; } = Read(
        "monday.board.read-sync-status-page",
        "Read the first page of one monday board's items for initial sync: ids, states, boards and the named status values.",
        "monday.sync-status-page-request.v1",
        "monday.sync-status-page.v1",
        "monday.board");

    /// <summary>Reads the next page of an initial-sync status listing by its cursor (Gate 3F).</summary>
    public static ConnectorOperationDescriptor ReadNextSyncStatusPage { get; } = Read(
        "monday.items.read-next-sync-status-page",
        "Read the next page of a monday initial-sync status listing by its cursor.",
        "monday.sync-status-cursor.v1",
        "monday.sync-status-page.v1",
        "monday.board");

    /// <summary>
    /// Reads the monday user and account a newly issued token acts for, so the connection's delegated identity is
    /// verified rather than assumed (Gate 3B). One attempt: it runs inside an authorization that is never retried.
    /// </summary>
    public static ConnectorOperationDescriptor ReadAuthorizingIdentity { get; } = new(
        "monday.me.read",
        "Read the monday user and account an authorizing token acts for.",
        "monday.none.v1",
        "monday.me.v1",
        ConnectorActionType.Read,
        ConnectorRiskLevel.Low,
        "monday.me",
        ConnectorSideEffect.None,
        ConnectorRecovery.NotRequired,
        ReadTimeout,
        ConnectorRetryPolicy.None);

    /// <summary>Every operation of the connector.</summary>
    public static IReadOnlyList<ConnectorOperationDescriptor> All { get; } =
    [
        ReadItemsById, ReadItemStatusesById, ReadBoardItemsPage, ReadNextItemsPage, ReadSyncBoard, ReadSyncStatusPage,
        ReadNextSyncStatusPage, ReadAuthorizingIdentity,
    ];

    private static ConnectorOperationDescriptor Read(
        string name,
        string purpose,
        string inputSchema,
        string outputSchema,
        string resourceScope) =>
        new(
            name,
            purpose,
            inputSchema,
            outputSchema,
            ConnectorActionType.Read,
            ConnectorRiskLevel.Low,
            resourceScope,
            ConnectorSideEffect.None,
            ConnectorRecovery.NotRequired,
            ReadTimeout,
            ConnectorRetryPolicy.DefaultRead);
}

/// <summary>
/// The GraphQL documents of the operations. Provider identifiers travel only as variables, never inside the document.
/// </summary>
/// <remarks>
/// The fields follow the monday API reference for version <see cref="MondayApiVersion.Pinned"/>. Items are read in
/// every state, so an archived or deleted item is reported as such rather than missing. <c>updated_at</c> is monday's
/// update time for an item; monday exposes no item revision.
/// </remarks>
public static class MondayQueries
{
    private const string ItemFields =
        "id name state created_at updated_at board { id } group { id } column_values { id type text value }";

    private const string SyncStatusItemFields =
        "id state board { id } column_values(ids: $columns) { id type ... on StatusValue { label index updated_at } }";

    /// <summary>The per-item reread. <c>limit</c> is always the number of ids, since monday's default is 25.</summary>
    public const string ReadItemsById =
        "query ReadItemsById($ids: [ID!]!, $limit: Int!) { items(ids: $ids, limit: $limit) { " + ItemFields + " } }";

    /// <summary>
    /// The per-item status reread (Gate 3E): each item's id, state and board, and only the named columns, each with its id,
    /// its type and, when it is a status, the typed <c>StatusValue</c> label, index and update time. Archived and deleted
    /// items are read with their state; a column monday does not return, such as a rollup status it omits by default, is
    /// simply absent. <c>limit</c> is always the number of ids.
    /// </summary>
    public const string ReadItemStatusesById =
        "query ReadItemStatusesById($ids: [ID!]!, $limit: Int!, $columns: [String!]!) { items(ids: $ids, limit: $limit) { "
        + "id state board { id } column_values(ids: $columns) { id type ... on StatusValue { label index updated_at } } } }";

    /// <summary>The first page of one board's items.</summary>
    public const string ReadBoardItemsPage =
        "query ReadBoardItemsPage($boardId: ID!, $limit: Int!) { boards(ids: [$boardId]) { items_page(limit: $limit) { cursor items { "
        + ItemFields + " } } } }";

    /// <summary>The next page of an item listing.</summary>
    public const string ReadNextItemsPage =
        "query ReadNextItemsPage($cursor: String!, $limit: Int!) { next_items_page(cursor: $cursor, limit: $limit) { cursor items { "
        + ItemFields + " } } }";

    /// <summary>
    /// The initial-sync validation of one configured board (Gate 3F): its id, its state in every state, so an archived or
    /// deleted board is reported as such rather than missing, its type and hierarchy, and only the ids and types of its
    /// columns. Never a name, a title, a description or an item.
    /// </summary>
    public const string ReadSyncBoard =
        "query ReadSyncBoard($boardId: ID!) { boards(ids: [$boardId], state: all) { id state type hierarchy_type columns { id type } } }";

    /// <summary>
    /// The first initial-sync page of one configured board (Gate 3F): the board's id, so the page binds to it, and its active
    /// items, each with only its id, state and board and the named status columns' id, type and typed <c>StatusValue</c>
    /// fields. No name, text or value, and not the item's own update time.
    /// </summary>
    public const string ReadSyncStatusPage =
        "query ReadSyncStatusPage($boardId: ID!, $limit: Int!, $columns: [String!]!) { boards(ids: [$boardId]) { id items_page(limit: $limit) { cursor items { "
        + SyncStatusItemFields + " } } } }";

    /// <summary>The next initial-sync page, by the cursor of the previous one, with the same item fields.</summary>
    public const string ReadNextSyncStatusPage =
        "query ReadNextSyncStatusPage($cursor: String!, $limit: Int!, $columns: [String!]!) { next_items_page(cursor: $cursor, limit: $limit) { cursor items { "
        + SyncStatusItemFields + " } } }";

    /// <summary>The delegating user and account of a token: their numeric ids only, never an email address or a name.</summary>
    public const string ReadAuthorizingIdentity = "query ReadAuthorizingIdentity { me { id account { id } } }";

    /// <summary>Every document the connector sends.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ReadItemsById, ReadItemStatusesById, ReadBoardItemsPage, ReadNextItemsPage, ReadSyncBoard, ReadSyncStatusPage,
        ReadNextSyncStatusPage, ReadAuthorizingIdentity,
    ];
}
