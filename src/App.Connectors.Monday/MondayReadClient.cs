using System.Net.Http.Headers;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;
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

    private static bool IsMondayId(string? value) =>
        value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit);

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

        while (true)
        {
            var acquired = await _credentials.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
            if (acquired.Credential is not { } credential)
            {
                return ConnectorCallResult<TValue>.Failed(
                    operation,
                    identity,
                    attempts,
                    acquired.Failure ?? new ConnectorFailure(
                        ConnectorFailureKind.CredentialUnavailable, ConnectorErrors.CredentialUnavailable));
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
            var (value, failure) = await AttemptAsync(credential, operation, body, parse, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return ConnectorCallResult<TValue>.Succeeded(operation, identity, attempts, value!);
            }

            var decision = operation.RetryPolicy.Decide(failure, attempts, NextJitter());
            if (!decision.Retry)
            {
                return ConnectorCallResult<TValue>.Failed(operation, identity, attempts, failure);
            }

            await _delay.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(TValue? Value, ConnectorFailure? Failure)> AttemptAsync<TValue>(
        MondayAccessCredential credential,
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
        credential.ApplyTo(request.Headers);

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
            var content = await ReadBoundedAsync(response.Content, attemptLimit.Token).ConfigureAwait(false);
            if (content is null)
            {
                return (default, Failure(ConnectorFailureKind.UnsupportedResponse, ConnectorErrors.UnsupportedResponse));
            }

            return Interpret((int)response.StatusCode, IsJson(response.Content), content, headerWait, parse);
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

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static bool IsJson(HttpContent content) =>
        content.Headers.ContentType?.MediaType is { } mediaType
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static ConnectorFailure Failure(ConnectorFailureKind kind, Error error) => new(kind, error);

    private double NextJitter()
    {
        var sample = _jitter();
        return sample is >= 0 and < 1 ? sample : 0.5;
    }
}
