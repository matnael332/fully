using WorkContinuity.Connectors.Monday;
using WorkContinuity.Harness.Monday.Diagnostics;

namespace WorkContinuity.Harness.Monday.Provider;

/// <summary>
/// The harness's default provider transport: it sends nothing (owner decision D3G-3).
/// </summary>
/// <remarks>
/// Every request ends here with a connection failure, the form the connector's clients read as "the request never
/// left", so nothing is retried as if the provider had answered and no connection changes state. There is no inner
/// handler and no network type behind this one: a harness that was given no fake cannot reach a provider at all.
/// </remarks>
public sealed class RefusingProviderTransport : HttpMessageHandler
{
    private int _refused;

    /// <summary>Gets how many requests ended here.</summary>
    public int Refused => Volatile.Read(ref _refused);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refused);
        return Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, "The harness has no provider transport; nothing was sent."));
    }
}

/// <summary>
/// The one path every provider request of the harness takes: only the connector's three fixed monday endpoints pass,
/// and each request is counted by endpoint and outcome.
/// </summary>
/// <remarks>
/// The connector already calls fixed addresses only; this refuses anything else before the transport sees it, as a
/// second boundary of the harness's own. Counting uses closed values: no address, header, body or provider text is kept.
/// </remarks>
public sealed class GuardedProviderTransport : DelegatingHandler
{
    private readonly HarnessDiagnostics _diagnostics;
    private readonly bool _refusesEverything;

    /// <summary>Creates the guarded path over a transport.</summary>
    /// <param name="transport">The transport requests to the three fixed endpoints are handed to.</param>
    /// <param name="diagnostics">Counts each request.</param>
    public GuardedProviderTransport(HttpMessageHandler transport, HarnessDiagnostics diagnostics)
        : base(transport)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
        _refusesEverything = transport is RefusingProviderTransport;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = EndpointOf(request.RequestUri);
        if (endpoint == ProviderEndpoint.Other)
        {
            _diagnostics.ProviderCall(endpoint, ProviderCallOutcome.RefusedForeignAddress);
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The address is not a provider endpoint; nothing was sent.");
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _diagnostics.ProviderCall(endpoint, OutcomeOf((int)response.StatusCode));
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.ProviderCall(endpoint, _refusesEverything ? ProviderCallOutcome.RefusedNoTransport : ProviderCallOutcome.NotSent);
            throw;
        }
    }

    private static ProviderEndpoint EndpointOf(Uri? address) =>
        address == MondayEndpoints.Token ? ProviderEndpoint.Token
        : address == MondayEndpoints.Revoke ? ProviderEndpoint.Revoke
        : address == MondayEndpoints.Api ? ProviderEndpoint.Api
        : ProviderEndpoint.Other;

    private static ProviderCallOutcome OutcomeOf(int status) => status switch
    {
        >= 200 and < 300 => ProviderCallOutcome.Success,
        >= 300 and < 400 => ProviderCallOutcome.Redirect,
        >= 400 and < 500 => ProviderCallOutcome.ClientError,
        >= 500 and < 600 => ProviderCallOutcome.ServerError,
        _ => ProviderCallOutcome.OtherStatus,
    };
}

/// <summary>
/// Builds the harness's provider HTTP clients: the only place one is constructed.
/// </summary>
/// <remarks>
/// A client is always built on the guarded path over the transport the composition was given, and the composition is
/// given either a test's fake or nothing, in which case the refusing transport stands in. No client is ever built
/// without a handler, so the platform's own network handler is never selected.
/// </remarks>
internal static class ProviderClients
{
    /// <summary>The guarded path over the given transport, or over the refusing one when none was given.</summary>
    internal static GuardedProviderTransport Guard(HttpMessageHandler? transport, HarnessDiagnostics diagnostics) =>
        new(transport ?? new RefusingProviderTransport(), diagnostics);

    /// <summary>A client on the guarded path. The path is shared, so disposing the client leaves it open.</summary>
    internal static HttpClient Create(GuardedProviderTransport guarded) => new HttpClient(guarded, disposeHandler: false);
}

/// <summary>
/// The harness's default secret storage: it holds nothing and answers every operation as unavailable.
/// </summary>
/// <remarks>
/// No real-credential store is approved for the harness (owner decision D3G-8), so the default composition has none:
/// an authorization cannot start, because its verifier cannot be stored, and no webhook token can be verified. Only a
/// test supplies a store, and it is a deterministic in-memory fake holding synthetic values (D3B-2).
/// </remarks>
public sealed class RefusingSecretStore : IMondaySecretStore
{
    /// <inheritdoc />
    public ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondaySecretStoreStatus.Unavailable);

    /// <inheritdoc />
    public ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondaySecretLookup.Unavailable());

    /// <inheritdoc />
    public ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondaySecretStoreStatus.Unavailable);
}
