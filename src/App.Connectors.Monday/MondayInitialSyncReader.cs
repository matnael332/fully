using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.InitialSync;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The monday <see cref="IInitialSyncReader"/> (Gate 3F): validates a configured board and pages its active items' status
/// values through the 3A read client, whose every attempt acquires the connection's 3B credential.
/// </summary>
/// <remarks>
/// <para>
/// It reads only the board it is asked for, by its configured id, and only the surface 3D and 3E support: a classic
/// board's items and their status columns, found by column type, never by title. The board must be the one asked for,
/// active, of monday's board type and classic hierarchy, with at least one status column whose id is a field token;
/// anything else is refused with an explicit outcome, never skipped, so a configured board never silently disappears.
/// </para>
/// <para>
/// A first page must name the board asked for. Each item and column is then bound exactly as the 3E reread binds it, by
/// the one shared rule: the item active and on that board, the column of monday's status type with the typed
/// <c>StatusValue</c> fields, a canonical label, and the status value's own update time; one bad item misbinds no other.
/// Its evidence is the 3E evidence, naming the initial-sync read that produced it.
/// </para>
/// <para>
/// monday's cursor is handed back only as an opaque continuation, never stored or interpreted. A continuation monday
/// refuses, or one whose answer is not a next page, is reported as rejected, so the board is read again from its first
/// page. The call used must be the connection's delegated identity. It calls monday only through
/// <see cref="MondayReadClient"/>, which reads and never writes, and it writes no log.
/// </para>
/// </remarks>
public sealed class MondayInitialSyncReader : IInitialSyncReader
{
    private const string BoardType = "board";
    private const string ClassicHierarchy = "classic";
    private const string StatusType = "status";

    private readonly MondayReadClient _client;

    /// <summary>Creates a reader over the 3A read client.</summary>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    public MondayInitialSyncReader(MondayReadClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public string SourceSystem => MondayWebhooks.SourceSystem;

    /// <inheritdoc />
    public async Task<ScopeValidation> ValidateScopeAsync(ScopeReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireMonday(request);

        // A resource that cannot be a board id is refused before anything is sent.
        if (!IsBoardId(request.ResourceId))
        {
            return ScopeValidation.Refused(SyncScopeOutcome.ScopeUnsupported, identity: null);
        }

        var call = await _client.ReadSyncBoardAsync(Context(request), request.ResourceId, cancellationToken).ConfigureAwait(false);
        if (!call.IsSuccess)
        {
            return ScopeValidation.Refused(OutcomeOf(call.Failure.Kind, continuation: false), call.Identity, call.Failure.RetryAfter);
        }

        if (Delegated(call.Identity, request) is not { } identity)
        {
            return ScopeValidation.Refused(SyncScopeOutcome.IdentityMismatch, call.Identity);
        }

        var board = call.Value;
        if (!string.Equals(board.Id, request.ResourceId, StringComparison.Ordinal))
        {
            return ScopeValidation.Refused(SyncScopeOutcome.BindingMismatch, identity);
        }

        if (board.State != MondayItemState.Active)
        {
            return ScopeValidation.Refused(SyncScopeOutcome.ScopeInactive, identity);
        }

        if (!string.Equals(board.Type, BoardType, StringComparison.Ordinal)
            || !string.Equals(board.HierarchyType, ClassicHierarchy, StringComparison.Ordinal))
        {
            return ScopeValidation.Refused(SyncScopeOutcome.ScopeUnsupported, identity);
        }

        // Status columns by type and id only; each distinct id is its own field, as in 3D and 3E.
        var statusColumns = board.Columns
            .Where(column => string.Equals(column.ColumnType, StatusType, StringComparison.Ordinal))
            .Select(column => column.ColumnId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (statusColumns.Length == 0)
        {
            return ScopeValidation.Refused(SyncScopeOutcome.NoSupportedField, identity);
        }

        if (statusColumns.Length > MondayOperations.MaxColumnsPerRead || !statusColumns.All(EventText.IsToken))
        {
            return ScopeValidation.Refused(SyncScopeOutcome.ScopeUnsupported, identity);
        }

        return ScopeValidation.Supported(
            identity,
            MondayWebhookNormalizer.ObjectType,
            statusColumns.Select(column => new SyncField(column, FactDataType.TaskStatus)).ToArray());
    }

    /// <inheritdoc />
    public async Task<ScopePage> ReadPageAsync(ScopePageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireMonday(request.Scope);
        if (!string.Equals(request.ObjectType, MondayWebhookNormalizer.ObjectType, StringComparison.Ordinal)
            || request.Fields.Any(field => field.DataType != FactDataType.TaskStatus))
        {
            throw new ArgumentException("The monday initial-sync reader reads item status fields only.", nameof(request));
        }

        if (!IsBoardId(request.Scope.ResourceId) || request.Fields.Count > MondayOperations.MaxColumnsPerRead)
        {
            return ScopePage.Failed(SyncScopeOutcome.ScopeUnsupported, identity: null);
        }

        var context = Context(request.Scope);
        var board = request.Scope.ResourceId;
        var columns = request.Fields.Select(field => field.Field).ToArray();
        var continuation = request.Continuation;
        var call = continuation is null
            ? await _client.ReadSyncStatusPageAsync(context, board, MondayOperations.MaxPageSize, columns, cancellationToken).ConfigureAwait(false)
            : await _client.ReadNextSyncStatusPageAsync(context, continuation.Token, MondayOperations.MaxPageSize, columns, cancellationToken).ConfigureAwait(false);
        if (!call.IsSuccess)
        {
            return ScopePage.Failed(OutcomeOf(call.Failure.Kind, continuation is not null), call.Identity, call.Failure.RetryAfter);
        }

        if (Delegated(call.Identity, request.Scope) is not { } identity)
        {
            return ScopePage.Failed(SyncScopeOutcome.IdentityMismatch, call.Identity);
        }

        // The first page names its board, which must be the one asked for; a next page continues that listing.
        var page = call.Value;
        if (continuation is null && !string.Equals(page.BoardId, board, StringComparison.Ordinal))
        {
            return ScopePage.Failed(SyncScopeOutcome.BindingMismatch, identity);
        }

        if (!page.Items.All(item => IsBoardId(item.Id)))
        {
            return ScopePage.Failed(SyncScopeOutcome.UnsupportedResponse, identity);
        }

        var operation = continuation is null ? MondayOperations.ReadSyncStatusPage : MondayOperations.ReadNextSyncStatusPage;
        var objects = page.Items
            .Select(item => new SyncedObject(
                new ObjectReference(SourceSystem, request.Scope.ConnectionReference, MondayWebhookNormalizer.ObjectType, item.Id),
                request.Fields.ToDictionary(field => field, field => MondayStatusReader.ReadStatus(item, board, field.Field, operation))))
            .ToArray();
        return ScopePage.Read(identity, objects, page.Cursor is null ? null : new ScopeContinuation(page.Cursor));
    }

    private void RequireMonday(ScopeReadRequest request)
    {
        if (!string.Equals(request.SourceSystem, SourceSystem, StringComparison.Ordinal))
        {
            throw new ArgumentException("The monday initial-sync reader reads monday scopes only.", nameof(request));
        }
    }

    // monday's ids are numeric: a board id and an item id alike.
    private static bool IsBoardId(string? value) =>
        value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit);

    private static MondayRequestContext Context(ScopeReadRequest request) =>
        new(request.Tenant, request.ConnectionId, request.CorrelationId);

    // Only the connection's delegated identity may read a scope; the client already refused any other credential.
    private static ConnectorIdentity? Delegated(ConnectorIdentity? identity, ScopeReadRequest request) =>
        identity is { Kind: ConnectorIdentityKind.DelegatedUser } && identity.Serves(request.Tenant, request.ConnectionId)
            ? identity
            : null;

    // As 3E classifies a call, except that a continuation monday refuses, does not find, or answers with no next page is
    // rejected, so the board is read again from its first page instead of being held.
    private static SyncScopeOutcome OutcomeOf(ConnectorFailureKind kind, bool continuation) => kind switch
    {
        ConnectorFailureKind.RateLimited => SyncScopeOutcome.RateLimited,
        ConnectorFailureKind.ProviderTransient or ConnectorFailureKind.NetworkTransient => SyncScopeOutcome.ProviderUnavailable,
        ConnectorFailureKind.CredentialUnavailable => SyncScopeOutcome.CredentialUnavailable,
        ConnectorFailureKind.AuthenticationFailed or ConnectorFailureKind.ConnectionDisabled or ConnectorFailureKind.AuthorizationFailed
            => SyncScopeOutcome.ConnectionUnavailable,
        ConnectorFailureKind.ResourceNotFound or ConnectorFailureKind.ProviderValidation or ConnectorFailureKind.UnsupportedResponse
            when continuation => SyncScopeOutcome.ContinuationRejected,
        ConnectorFailureKind.ResourceNotFound => SyncScopeOutcome.ScopeMissing,
        _ => SyncScopeOutcome.UnsupportedResponse,
    };
}
