using System.Net.Http.Headers;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// Performs the typed, read-only monday operations over GraphQL, one attempt at a time, failing closed.
/// </summary>
/// <remarks>
/// <para>
/// Every call names its tenant and connection. Before each attempt, retries included, the client acquires the
/// connection's credential from <see cref="IMondayCredentialSource"/>, so a disabled connection or a revoked token stops
/// the very next attempt, and it refuses a credential that belongs to another tenant or connection, that changes
/// identity between attempts, or that has expired. The result always records the identity whose authority was used.
/// When monday rejects a credential (401), the client asks the source once for a renewal and retries once, as monday
/// documents; a second rejection, or a renewal that fails, ends the call.
/// </para>
/// <para>
/// Every request goes to the one fixed endpoint, carries the pinned <c>API-Version</c> header, and holds a single read
/// query checked by <see cref="MondayQueryGuard"/>; provider values travel only as variables. Each attempt is bounded by
/// the operation's timeout, and each response by <see cref="MaxResponseBytes"/>. Outcomes are translated onto the
/// connector taxonomy, and only transient failures of read operations are retried, as the operation's policy allows,
/// never faster than monday asks.
/// </para>
/// <para>
/// The client writes no log. A failure carries only a fixed connector error: never a token, a provider message, a
/// response body or an exception's text. Registering the client in a host belongs to the sub-gate that first uses it.
/// </para>
/// </remarks>
public sealed class MondayReadClient
{
    /// <summary>The largest response body read.</summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IMondayCredentialSource _credentials;
    private readonly IClock _clock;
    private readonly IMondayDelay _delay;
    private readonly Func<double> _jitter;

    /// <summary>Creates a client.</summary>
    /// <param name="http">The HTTP client. Its handler should not follow redirects; a redirected answer is refused.</param>
    /// <param name="credentials">The source of the connection's credential, asked before every attempt.</param>
    /// <param name="clock">The clock, for credential expiry and date-form retry waits.</param>
    /// <param name="delay">The wait between attempts.</param>
    /// <param name="jitter">A source of uniform samples in [0, 1) for backoff jitter; random when omitted.</param>
    public MondayReadClient(
        HttpClient http,
        IMondayCredentialSource credentials,
        IClock clock,
        IMondayDelay delay,
        Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(delay);

        _http = http;
        _credentials = credentials;
        _clock = clock;
        _delay = delay;
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <summary>Rereads the current authoritative state of up to 100 items, in every state, by id.</summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="itemIds">The monday item ids: 1 to 100 distinct numeric ids.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondayItemBatch>> ReadItemsByIdAsync(
        MondayRequestContext context,
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadItemsById;

        if (itemIds is null
            || itemIds.Count is 0 or > MondayOperations.MaxItemsPerRead
            || !itemIds.All(IsMondayId)
            || itemIds.Distinct(StringComparer.Ordinal).Count() != itemIds.Count)
        {
            return Task.FromResult(Invalid<MondayItemBatch>(operation));
        }

        var ids = itemIds.ToArray();
        var request = new MondayGraphQlRequest(MondayQueries.ReadItemsById)
            .With("ids", ids)
            .With("limit", ids.Length);

        return ExecuteAsync(context, operation, request, data => MondayResponseParser.ItemsById(data, ids), cancellationToken);
    }

    /// <summary>
    /// Rereads the current status values of up to 100 items, in every state, by id: each item's state and board, and the
    /// named columns' typed <c>StatusValue</c> fields (Gate 3E).
    /// </summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="itemIds">The monday item ids: 1 to 100 distinct numeric ids.</param>
    /// <param name="columnIds">The column ids: 1 to 100 distinct column tokens.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondayItemStatusBatch>> ReadItemStatusesByIdAsync(
        MondayRequestContext context,
        IReadOnlyCollection<string> itemIds,
        IReadOnlyCollection<string> columnIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadItemStatusesById;

        if (itemIds is null
            || itemIds.Count is 0 or > MondayOperations.MaxItemsPerRead
            || !itemIds.All(IsMondayId)
            || itemIds.Distinct(StringComparer.Ordinal).Count() != itemIds.Count
            || columnIds is null
            || columnIds.Count is 0 or > MondayOperations.MaxColumnsPerRead
            || !columnIds.All(EventText.IsToken)
            || columnIds.Distinct(StringComparer.Ordinal).Count() != columnIds.Count)
        {
            return Task.FromResult(Invalid<MondayItemStatusBatch>(operation));
        }

        var ids = itemIds.ToArray();
        var columns = columnIds.ToArray();
        var request = new MondayGraphQlRequest(MondayQueries.ReadItemStatusesById)
            .With("ids", ids)
            .With("limit", ids.Length)
            .With("columns", columns);

        return ExecuteAsync(context, operation, request, data => MondayResponseParser.ItemStatuses(data, ids, columns), cancellationToken);
    }

    /// <summary>Reads the first page of one board's items.</summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="boardId">The monday board id.</param>
    /// <param name="limit">The page size: 1 to 500.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondayItemsPage>> ReadBoardItemsPageAsync(
        MondayRequestContext context,
        string boardId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadBoardItemsPage;

        if (!IsMondayId(boardId) || limit is < 1 or > MondayOperations.MaxPageSize)
        {
            return Task.FromResult(Invalid<MondayItemsPage>(operation));
        }

        var request = new MondayGraphQlRequest(MondayQueries.ReadBoardItemsPage)
            .With("boardId", boardId)
            .With("limit", limit);

        return ExecuteAsync(context, operation, request, data => MondayResponseParser.BoardItemsPage(data, limit), cancellationToken);
    }

    /// <summary>Reads the next page of an item listing by the cursor of the previous page.</summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="cursor">The cursor monday returned with the previous page.</param>
    /// <param name="limit">The page size: 1 to 500.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondayItemsPage>> ReadNextItemsPageAsync(
        MondayRequestContext context,
        string cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadNextItemsPage;

        if (!EventText.IsReference(cursor) || limit is < 1 or > MondayOperations.MaxPageSize)
        {
            return Task.FromResult(Invalid<MondayItemsPage>(operation));
        }

        var request = new MondayGraphQlRequest(MondayQueries.ReadNextItemsPage)
            .With("cursor", cursor)
            .With("limit", limit);

        return ExecuteAsync(context, operation, request, data => MondayResponseParser.NextItemsPage(data, limit), cancellationToken);
    }

    /// <summary>
    /// Reads one board's id, state, type and hierarchy and the ids and types of its columns, to validate it as an initial-sync
    /// scope (Gate 3F). The board is read in every state, so an archived or deleted board is reported as such.
    /// </summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="boardId">The configured monday board id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondaySyncBoard>> ReadSyncBoardAsync(
        MondayRequestContext context,
        string boardId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadSyncBoard;

        if (!IsMondayId(boardId))
        {
            return Task.FromResult(Invalid<MondaySyncBoard>(operation));
        }

        var request = new MondayGraphQlRequest(MondayQueries.ReadSyncBoard).With("boardId", boardId);
        return ExecuteAsync(context, operation, request, MondayResponseParser.SyncBoard, cancellationToken);
    }

    /// <summary>
    /// Reads the first initial-sync page of one board (Gate 3F): its active items' ids, states and boards, and the named
    /// columns' typed <c>StatusValue</c> fields.
    /// </summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="boardId">The configured monday board id.</param>
    /// <param name="limit">The page size: 1 to 500.</param>
    /// <param name="columnIds">The status column ids: 1 to 100 distinct column tokens.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondayStatusPage>> ReadSyncStatusPageAsync(
        MondayRequestContext context,
        string boardId,
        int limit,
        IReadOnlyCollection<string> columnIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadSyncStatusPage;

        if (!IsMondayId(boardId) || limit is < 1 or > MondayOperations.MaxPageSize || !AreColumns(columnIds))
        {
            return Task.FromResult(Invalid<MondayStatusPage>(operation));
        }

        var columns = columnIds.ToArray();
        var request = new MondayGraphQlRequest(MondayQueries.ReadSyncStatusPage)
            .With("boardId", boardId)
            .With("limit", limit)
            .With("columns", columns);

        return ExecuteAsync(context, operation, request, data => MondayResponseParser.SyncStatusPage(data, limit, columns), cancellationToken);
    }

    /// <summary>Reads the next initial-sync page by the cursor of the previous one (Gate 3F).</summary>
    /// <param name="context">The call's tenant, connection and correlation.</param>
    /// <param name="cursor">The cursor monday returned with the previous page.</param>
    /// <param name="limit">The page size: 1 to 500.</param>
    /// <param name="columnIds">The status column ids: 1 to 100 distinct column tokens.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ConnectorCallResult<MondayStatusPage>> ReadNextSyncStatusPageAsync(
        MondayRequestContext context,
        string cursor,
        int limit,
        IReadOnlyCollection<string> columnIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = MondayOperations.ReadNextSyncStatusPage;

        if (!ScopeContinuation.IsValid(cursor) || limit is < 1 or > MondayOperations.MaxPageSize || !AreColumns(columnIds))
        {
            return Task.FromResult(Invalid<MondayStatusPage>(operation));
        }

        var columns = columnIds.ToArray();
        var request = new MondayGraphQlRequest(MondayQueries.ReadNextSyncStatusPage)
            .With("cursor", cursor)
            .With("limit", limit)
            .With("columns", columns);

        return ExecuteAsync(context, operation, request, data => MondayResponseParser.NextSyncStatusPage(data, limit, columns), cancellationToken);
    }

    private static bool IsMondayId(string? value) =>
        value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit);

    private static bool AreColumns(IReadOnlyCollection<string>? columnIds) =>
        columnIds is { Count: > 0 and <= MondayOperations.MaxColumnsPerRead }
        && columnIds.All(EventText.IsToken)
        && columnIds.Distinct(StringComparer.Ordinal).Count() == columnIds.Count;

    private static ConnectorCallResult<TValue> Invalid<TValue>(ConnectorOperationDescriptor operation) =>
        ConnectorCallResult<TValue>.Failed(
            operation,
            identity: null,
            attempts: 0,
            new ConnectorFailure(ConnectorFailureKind.InvalidRequest, ConnectorErrors.InvalidRequest));

    private async Task<ConnectorCallResult<TValue>> ExecuteAsync<TValue>(
        MondayRequestContext context,
        ConnectorOperationDescriptor operation,
        MondayGraphQlRequest request,
        Func<JsonElement, ParseOutcome<TValue>> parse,
        CancellationToken cancellationToken)
    {
        // Gate 3 reads only: a declared read with a single read query, or nothing is sent.
        if (!operation.IsReadOnly || !MondayQueryGuard.IsSingleReadQuery(request.Document))
        {
            return ConnectorCallResult<TValue>.Failed(
                operation,
                identity: null,
                attempts: 0,
                new ConnectorFailure(ConnectorFailureKind.OperationNotPermitted, ConnectorErrors.OperationNotPermitted));
        }

        var body = request.ToUtf8Json();
        ConnectorIdentity? identity = null;
        var attempts = 0;
        var renewed = false;
        MondayAccessCredential? replacement = null;

        while (true)
        {
            // A renewed credential is the acquisition for the attempt that follows the renewal; every other attempt
            // acquires afresh.
            var credential = replacement;
            replacement = null;
            if (credential is null)
            {
                var acquired = await _credentials.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
                if (acquired.Credential is null)
                {
                    return ConnectorCallResult<TValue>.Failed(
                        operation,
                        identity,
                        attempts,
                        acquired.Failure ?? new ConnectorFailure(
                            ConnectorFailureKind.CredentialUnavailable, ConnectorErrors.CredentialUnavailable));
                }

                credential = acquired.Credential;
            }

            if (!credential.Identity.Serves(context.Tenant, context.ConnectionId)
                || (identity is not null && identity != credential.Identity))
            {
                return ConnectorCallResult<TValue>.Failed(
                    operation,
                    identity,
                    attempts,
                    new ConnectorFailure(ConnectorFailureKind.CredentialUnavailable, ConnectorErrors.CredentialMismatch));
            }

            identity = credential.Identity;

            if (credential.HasExpiredAt(_clock.UtcNow))
            {
                return ConnectorCallResult<TValue>.Failed(
                    operation,
                    identity,
                    attempts,
                    new ConnectorFailure(ConnectorFailureKind.AuthenticationFailed, ConnectorErrors.CredentialExpired));
            }

            attempts++;
            var (value, failure) = await AttemptAsync(credential.ApplyTo, operation, body, parse, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return ConnectorCallResult<TValue>.Succeeded(operation, identity, attempts, value!);
            }

            // monday: after a 401, renew the token and retry. At most once per call, so a rejected renewal cannot loop.
            if (failure.Kind == ConnectorFailureKind.AuthenticationFailed && !renewed)
            {
                renewed = true;
                var renewal = await _credentials.RenewAfterRejectionAsync(context, credential, cancellationToken).ConfigureAwait(false);
                if (renewal.Credential is null)
                {
                    return ConnectorCallResult<TValue>.Failed(operation, identity, attempts, renewal.Failure ?? failure);
                }

                replacement = renewal.Credential;
                continue;
            }

            var decision = operation.RetryPolicy.Decide(failure, attempts, NextJitter());
            if (!decision.Retry)
            {
                return ConnectorCallResult<TValue>.Failed(operation, identity, attempts, failure);
            }

            await _delay.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the identity an access token acts for, once, while a connection is being authorized: the delegating monday
    /// user and account. The token has no connection yet, so no credential source is involved; the same fixed endpoint,
    /// version header, read-only guard, bounds and classification apply. Never retried and never renewed.
    /// </summary>
    internal async Task<(MondayAuthorizingIdentity? Identity, ConnectorFailure? Failure)> ReadAuthorizingIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var operation = MondayOperations.ReadAuthorizingIdentity;
        var request = new MondayGraphQlRequest(MondayQueries.ReadAuthorizingIdentity);

        if (!operation.IsReadOnly || !MondayQueryGuard.IsSingleReadQuery(request.Document) || !MondayIssuedTokens.IsToken(accessToken))
        {
            return (null, Failure(ConnectorFailureKind.OperationNotPermitted, ConnectorErrors.OperationNotPermitted));
        }

        return await AttemptAsync(
                headers => MondayAccessCredential.Apply(headers, accessToken),
                operation,
                request.ToUtf8Json(),
                MondayResponseParser.AuthorizingIdentity,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(TValue? Value, ConnectorFailure? Failure)> AttemptAsync<TValue>(
        Action<HttpRequestHeaders> authorize,
        ConnectorOperationDescriptor operation,
        byte[] body,
        Func<JsonElement, ParseOutcome<TValue>> parse,
        CancellationToken cancellationToken)
    {
        using var attemptLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptLimit.CancelAfter(operation.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, MondayEndpoints.Api);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add(MondayApiVersion.HeaderName, MondayApiVersion.Pinned);
        authorize(request.Headers);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptLimit.Token)
                .ConfigureAwait(false);

            if (response.RequestMessage?.RequestUri is { } answered && answered != MondayEndpoints.Api)
            {
                // The request was redirected elsewhere; nothing from another host is trusted.
                return (default, Failure(ConnectorFailureKind.UnsupportedResponse, ConnectorErrors.UnsupportedResponse));
            }

            var headerWait = MondayResponseClassifier.WaitFromHeaders(response.Headers, _clock.UtcNow);
            var content = await MondayHttpContent.ReadBoundedAsync(response.Content, MaxResponseBytes, attemptLimit.Token).ConfigureAwait(false);
            if (content is null)
            {
                return (default, Failure(ConnectorFailureKind.UnsupportedResponse, ConnectorErrors.UnsupportedResponse));
            }

            return Interpret((int)response.StatusCode, MondayHttpContent.IsJson(response.Content), content, headerWait, parse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (default, Failure(ConnectorFailureKind.NetworkTransient, ConnectorErrors.Timeout));
        }
        catch (HttpRequestException)
        {
            return (default, Failure(ConnectorFailureKind.NetworkTransient, ConnectorErrors.NetworkFailure));
        }
        catch (IOException)
        {
            return (default, Failure(ConnectorFailureKind.NetworkTransient, ConnectorErrors.NetworkFailure));
        }
    }

    private static (TValue? Value, ConnectorFailure? Failure) Interpret<TValue>(
        int status,
        bool isJson,
        byte[] content,
        TimeSpan? headerWait,
        Func<JsonElement, ParseOutcome<TValue>> parse)
    {
        JsonDocument? document = null;
        try
        {
            if (content.Length > 0)
            {
                try
                {
                    document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
                }
                catch (JsonException)
                {
                    document = null;
                }
            }

            var root = document?.RootElement;
            var failure = MondayResponseClassifier.Classify(status, root, headerWait);
            if (failure is not null)
            {
                return (default, failure);
            }

            // A successful transport answer must be a JSON object with an object of data.
            if (!isJson
                || root is not { ValueKind: JsonValueKind.Object } body
                || !body.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return (default, Failure(ConnectorFailureKind.UnsupportedResponse, ConnectorErrors.UnsupportedResponse));
            }

            var outcome = parse(data);
            return outcome.IsParsed
                ? (outcome.Value, null)
                : (default, outcome.FailureKind == ConnectorFailureKind.ResourceNotFound
                    ? Failure(ConnectorFailureKind.ResourceNotFound, ConnectorErrors.ResourceNotFound)
                    : Failure(ConnectorFailureKind.UnsupportedResponse, ConnectorErrors.UnsupportedResponse));
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static ConnectorFailure Failure(ConnectorFailureKind kind, Error error) => new(kind, error);

    private double NextJitter()
    {
        var sample = _jitter();
        return sample is >= 0 and < 1 ? sample : 0.5;
    }
}
