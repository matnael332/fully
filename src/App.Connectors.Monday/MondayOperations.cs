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

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Rereads the current authoritative state of up to 100 items by id.</summary>
    public static ConnectorOperationDescriptor ReadItemsById { get; } = Read(
        "monday.items.read-by-id",
        "Reread the current authoritative state of up to 100 monday items by id.",
        "monday.item-ids.v1",
        "monday.items.v1",
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
        [ReadItemsById, ReadBoardItemsPage, ReadNextItemsPage, ReadAuthorizingIdentity];

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

    /// <summary>The per-item reread. <c>limit</c> is always the number of ids, since monday's default is 25.</summary>
    public const string ReadItemsById =
        "query ReadItemsById($ids: [ID!]!, $limit: Int!) { items(ids: $ids, limit: $limit) { " + ItemFields + " } }";

    /// <summary>The first page of one board's items.</summary>
    public const string ReadBoardItemsPage =
        "query ReadBoardItemsPage($boardId: ID!, $limit: Int!) { boards(ids: [$boardId]) { items_page(limit: $limit) { cursor items { "
        + ItemFields + " } } } }";

    /// <summary>The next page of an item listing.</summary>
    public const string ReadNextItemsPage =
        "query ReadNextItemsPage($cursor: String!, $limit: Int!) { next_items_page(cursor: $cursor, limit: $limit) { cursor items { "
        + ItemFields + " } } }";

    /// <summary>The delegating user and account of a token: their numeric ids only, never an email address or a name.</summary>
    public const string ReadAuthorizingIdentity = "query ReadAuthorizingIdentity { me { id account { id } } }";

    /// <summary>Every document the connector sends.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ReadItemsById, ReadBoardItemsPage, ReadNextItemsPage, ReadAuthorizingIdentity];
}
