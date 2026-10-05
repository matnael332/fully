using System.Collections.Concurrent;
using System.Globalization;

namespace WorkContinuity.Harness.Monday.Diagnostics;

/// <summary>Which of the connector's three fixed monday addresses a provider request named, or none of them.</summary>
public enum ProviderEndpoint
{
    /// <summary>monday's OAuth token endpoint.</summary>
    Token = 1,

    /// <summary>monday's OAuth revocation endpoint.</summary>
    Revoke = 2,

    /// <summary>monday's API endpoint.</summary>
    Api = 3,

    /// <summary>Any other address.</summary>
    Other = 4,
}

/// <summary>What became of one provider request at the harness's transport.</summary>
public enum ProviderCallOutcome
{
    /// <summary>Ended at the default transport, which sends nothing.</summary>
    RefusedNoTransport = 1,

    /// <summary>Refused before the transport: the address is not one of the three fixed endpoints.</summary>
    RefusedForeignAddress = 2,

    /// <summary>The supplied transport failed before an answer.</summary>
    NotSent = 3,

    /// <summary>An answer with a 2xx status.</summary>
    Success = 4,

    /// <summary>An answer with a 3xx status.</summary>
    Redirect = 5,

    /// <summary>An answer with a 4xx status.</summary>
    ClientError = 6,

    /// <summary>An answer with a 5xx status.</summary>
    ServerError = 7,

    /// <summary>An answer with any other status.</summary>
    OtherStatus = 8,
}

/// <summary>The operator listener's routes.</summary>
public enum OperatorRoute
{
    /// <summary>The authorization start.</summary>
    Start = 1,

    /// <summary>The authorization callback.</summary>
    Callback = 2,

    /// <summary>The parameter-free terminal page.</summary>
    Done = 3,

    /// <summary>Anything else.</summary>
    Other = 4,
}

/// <summary>Why the operator listener refused a request, or that it did not.</summary>
public enum OperatorRefusal
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>The connection was not local to this machine.</summary>
    NotLoopback = 1,

    /// <summary>The request did not arrive over TLS.</summary>
    NotHttps = 2,

    /// <summary>The Host header is not the configured operator address.</summary>
    HostMismatch = 3,

    /// <summary>The method is not GET.</summary>
    MethodNotAllowed = 4,

    /// <summary>The request names an origin other than the operator listener's own.</summary>
    ForeignOrigin = 5,

    /// <summary>The browser reports that the request is not a top-level navigation.</summary>
    NotANavigation = 6,

    /// <summary>The browser reports that another site initiated the start.</summary>
    SiteInitiatedStart = 7,

    /// <summary>The request carries parameters the route does not take.</summary>
    UnexpectedParameters = 8,

    /// <summary>The start capability is absent.</summary>
    CapabilityMissing = 9,

    /// <summary>The start capability is not well formed.</summary>
    CapabilityMalformed = 10,

    /// <summary>The start capability is not the one that was minted.</summary>
    CapabilityUnknown = 11,

    /// <summary>The start capability has expired.</summary>
    CapabilityExpired = 12,

    /// <summary>The start capability was already used.</summary>
    CapabilityUsed = 13,

    /// <summary>The callback carries no browser binding.</summary>
    BindingMissing = 14,

    /// <summary>The callback's browser binding or state is not the pending authorization's.</summary>
    BindingMismatch = 15,

    /// <summary>The callback repeats a parameter.</summary>
    CallbackMalformed = 16,

    /// <summary>The path is not an operator route.</summary>
    UnknownRoute = 17,

    /// <summary>The route failed while it handled the request.</summary>
    Faulted = 18,
}

/// <summary>How an authorization start ended.</summary>
public enum AuthorizationStartOutcome
{
    /// <summary>The browser was sent to the provider.</summary>
    Started = 1,

    /// <summary>No trusted tenant was established.</summary>
    TenantUnresolved = 2,

    /// <summary>The canonical user may not authorize a connection.</summary>
    UserNotEligible = 3,

    /// <summary>The connection cannot be reauthorized.</summary>
    NotReauthorizable = 4,

    /// <summary>The request was invalid.</summary>
    InvalidRequest = 5,

    /// <summary>The transaction or its verifier could not be stored.</summary>
    StorageFailed = 6,

    /// <summary>Any other failure.</summary>
    Other = 7,
}

/// <summary>How an authorization callback ended once it reached the authorizer.</summary>
public enum AuthorizationCompletionOutcome
{
    /// <summary>The connection is authorized.</summary>
    Completed = 1,

    /// <summary>The state names no transaction.</summary>
    TransactionNotFound = 2,

    /// <summary>The transaction expired.</summary>
    TransactionExpired = 3,

    /// <summary>The transaction belongs to another user.</summary>
    BindingMismatch = 4,

    /// <summary>The state does not match.</summary>
    StateMismatch = 5,

    /// <summary>The response was already used.</summary>
    Replayed = 6,

    /// <summary>The provider reported that the authorization was not approved.</summary>
    Denied = 7,

    /// <summary>The response names an unexpected issuer.</summary>
    IssuerMismatch = 8,

    /// <summary>The response carries no usable code.</summary>
    CodeMissing = 9,

    /// <summary>The redirect address no longer matches.</summary>
    RedirectMismatch = 10,

    /// <summary>The client configuration is unavailable.</summary>
    ClientUnavailable = 11,

    /// <summary>The provider rejected the code.</summary>
    ExchangeRejected = 12,

    /// <summary>The code could not be exchanged.</summary>
    ExchangeFailed = 13,

    /// <summary>The grant is incomplete or unusable.</summary>
    GrantUnusable = 14,

    /// <summary>The granted scopes are not permitted.</summary>
    ScopeRefused = 15,

    /// <summary>The delegated identity could not be verified.</summary>
    IdentityUnverified = 16,

    /// <summary>The delegated identity differs from the connection's.</summary>
    IdentityMismatch = 17,

    /// <summary>The authorization could not be stored.</summary>
    StorageFailed = 18,

    /// <summary>The connection cannot be reauthorized.</summary>
    NotReauthorizable = 19,

    /// <summary>The canonical user may not authorize a connection.</summary>
    UserNotEligible = 20,

    /// <summary>No trusted tenant was established.</summary>
    TenantUnresolved = 21,

    /// <summary>The request was invalid.</summary>
    InvalidRequest = 22,

    /// <summary>Any other failure.</summary>
    Other = 23,
}

/// <summary>The callback parameter names the harness recognizes. Every other name counts as <see cref="Other"/>.</summary>
public enum CallbackParameter
{
    /// <summary><c>state</c>.</summary>
    State = 1,

    /// <summary><c>code</c>.</summary>
    Code = 2,

    /// <summary><c>error</c>.</summary>
    Error = 3,

    /// <summary><c>error_description</c>.</summary>
    ErrorDescription = 4,

    /// <summary><c>iss</c>.</summary>
    Issuer = 5,

    /// <summary><c>scope</c>.</summary>
    Scope = 6,

    /// <summary>A name outside this list; only counted.</summary>
    Other = 7,
}

/// <summary>How a callback's issuer parameter relates to the provider's issuer identifier.</summary>
public enum IssuerRelation
{
    /// <summary>No issuer parameter.</summary>
    Absent = 1,

    /// <summary>Exactly the provider's issuer identifier.</summary>
    Equal = 2,

    /// <summary>Anything else.</summary>
    Different = 3,
}

/// <summary>The form of a webhook request's <c>Authorization</c> header.</summary>
public enum WebhookAuthorizationForm
{
    /// <summary>No header.</summary>
    Absent = 1,

    /// <summary>One value with no scheme.</summary>
    Bare = 2,

    /// <summary>One value with the exact scheme <c>Bearer</c> and one space.</summary>
    Bearer = 3,

    /// <summary>More than one header value.</summary>
    Multiple = 4,

    /// <summary>Any other form.</summary>
    Other = 5,
}

/// <summary>The signing algorithm a webhook token's header names, from a closed list.</summary>
public enum WebhookTokenAlgorithm
{
    /// <summary>No token to read.</summary>
    NotApplicable = 1,

    /// <summary>HS256.</summary>
    HS256 = 2,

    /// <summary>HS384.</summary>
    HS384 = 3,

    /// <summary>HS512.</summary>
    HS512 = 4,

    /// <summary>RS256.</summary>
    RS256 = 5,

    /// <summary>ES256.</summary>
    ES256 = 6,

    /// <summary>The unsigned form.</summary>
    None = 7,

    /// <summary>A name outside this list.</summary>
    Other = 8,

    /// <summary>The token's header could not be read.</summary>
    Unreadable = 9,
}

/// <summary>The type a webhook token's header names.</summary>
public enum WebhookTokenType
{
    /// <summary>No token to read.</summary>
    NotApplicable = 1,

    /// <summary>JWT.</summary>
    Jwt = 2,

    /// <summary>No type.</summary>
    Absent = 3,

    /// <summary>Another type.</summary>
    Other = 4,

    /// <summary>The token's header could not be read.</summary>
    Unreadable = 5,
}

/// <summary>How the existing webhook endpoint answered.</summary>
public enum WebhookResponseClass
{
    /// <summary>200: a challenge answered or a delivery acknowledged.</summary>
    Accepted = 1,

    /// <summary>400.</summary>
    BadRequest = 2,

    /// <summary>401.</summary>
    Unauthenticated = 3,

    /// <summary>404.</summary>
    NotFound = 4,

    /// <summary>413.</summary>
    PayloadTooLarge = 5,

    /// <summary>415.</summary>
    UnsupportedMediaType = 6,

    /// <summary>503.</summary>
    Unavailable = 7,

    /// <summary>Any other status.</summary>
    Other = 8,
}

/// <summary>The claim names the harness recognizes in an accepted webhook token. Every other name counts as <see cref="Other"/>.</summary>
public enum WebhookClaim
{
    /// <summary><c>accountId</c>.</summary>
    Account = 1,

    /// <summary><c>userId</c>.</summary>
    Member = 2,

    /// <summary><c>aud</c>.</summary>
    Audience = 3,

    /// <summary><c>exp</c>.</summary>
    Expiry = 4,

    /// <summary><c>iat</c>.</summary>
    IssuedAt = 5,

    /// <summary><c>shortLivedToken</c>.</summary>
    ShortLivedToken = 6,

    /// <summary><c>iss</c>.</summary>
    Issuer = 7,

    /// <summary><c>sub</c>.</summary>
    Subject = 8,

    /// <summary><c>nbf</c>.</summary>
    NotBefore = 9,

    /// <summary><c>jti</c>.</summary>
    TokenId = 10,

    /// <summary>A name outside this list; only counted.</summary>
    Other = 11,
}

/// <summary>How an accepted webhook token's audience relates to the address its connection's webhooks are registered under.</summary>
public enum AudienceRelation
{
    /// <summary>No audience claim.</summary>
    Absent = 1,

    /// <summary>Exactly the expected address.</summary>
    Equal = 2,

    /// <summary>The expected address but for a trailing slash.</summary>
    EqualIgnoringTrailingSlash = 3,

    /// <summary>The expected origin with another path.</summary>
    SameOriginOtherPath = 4,

    /// <summary>Another origin.</summary>
    OtherOrigin = 5,

    /// <summary>Not a single readable address.</summary>
    Unreadable = 6,
}

/// <summary>How one PKCE verifier cleanup ended.</summary>
public enum CleanupOutcome
{
    /// <summary>No cleanup has run.</summary>
    NotRun = 0,

    /// <summary>The cleanup ran and reported its counts.</summary>
    Succeeded = 1,

    /// <summary>The cleanup reported a failure.</summary>
    Failed = 2,

    /// <summary>The cleanup threw.</summary>
    Faulted = 3,
}

/// <summary>Whose a log record is, as far as the harness can tell before it shows it.</summary>
public enum LogRecordOwner
{
    /// <summary>The harness's own category, which logs closed values only. Shown.</summary>
    Harness = 1,

    /// <summary>The configured tenant: the flow that logged the record runs as it, or the record names it. Shown.</summary>
    ConfiguredTenant = 2,

    /// <summary>Another tenant: the flow that logged the record runs as one, or the record names one. Withheld.</summary>
    OtherTenant = 3,

    /// <summary>A product record that no tenant can be told for. Withheld.</summary>
    Unattributed = 4,
}

/// <summary>The shape of one webhook request, by closed values only.</summary>
/// <param name="Authorization">The form of the <c>Authorization</c> header.</param>
/// <param name="Algorithm">The algorithm the token's header names.</param>
/// <param name="Type">The type the token's header names.</param>
/// <param name="Response">How the existing endpoint answered.</param>
public readonly record struct WebhookRequestShape(
    WebhookAuthorizationForm Authorization,
    WebhookTokenAlgorithm Algorithm,
    WebhookTokenType Type,
    WebhookResponseClass Response);

/// <summary>The lifetimes of accepted webhook tokens, in whole seconds, each bounded to one day.</summary>
/// <param name="Count">How many lifetimes were measured.</param>
/// <param name="MinimumSeconds">The shortest, or 0 when none was measured.</param>
/// <param name="MaximumSeconds">The longest, or 0 when none was measured.</param>
public sealed record WebhookLifetimeSummary(long Count, int MinimumSeconds, int MaximumSeconds);

/// <summary>What the PKCE verifier cleanup has done, by counts only.</summary>
/// <param name="StartupRunCompleted">Whether the start-up run has finished.</param>
/// <param name="Runs">How many cleanups have run, the start-up run included.</param>
/// <param name="LastOutcome">How the last one ended.</param>
/// <param name="LastDeleted">Verifiers the last one deleted, bounded.</param>
/// <param name="LastRemaining">Verifiers the last one left due, bounded.</param>
/// <param name="TotalDeleted">Verifiers deleted in all.</param>
public sealed record VerifierCleanupSummary(
    bool StartupRunCompleted,
    long Runs,
    CleanupOutcome LastOutcome,
    int LastDeleted,
    int LastRemaining,
    long TotalDeleted);

/// <summary>What the relay and the consumer have done, by counts only.</summary>
/// <param name="RelayPasses">Relay passes run.</param>
/// <param name="RelayPublished">Messages those passes published.</param>
/// <param name="RelayFailedPasses">Passes that reported a failure.</param>
/// <param name="ConsumerStarted">Whether the consumer host was started.</param>
public sealed record BackboneSummary(long RelayPasses, long RelayPublished, long RelayFailedPasses, bool ConsumerStarted);

/// <summary>Everything the harness has observed about itself: closed values, booleans, counts and bounded numbers only.</summary>
/// <param name="ProviderCalls">Provider requests by endpoint and outcome.</param>
/// <param name="OperatorRequests">Operator listener requests by route and refusal.</param>
/// <param name="AuthorizationStarts">Authorization starts by outcome.</param>
/// <param name="AuthorizationCompletions">Authorization callbacks that reached the authorizer, by outcome.</param>
/// <param name="CallbackParameters">Callback parameter names, from the closed list.</param>
/// <param name="CallbackIssuers">Callback issuer relations.</param>
/// <param name="WebhookRequests">Webhook requests by shape.</param>
/// <param name="AcceptedWebhookClaims">Claim names of accepted webhook tokens, from the closed list.</param>
/// <param name="AcceptedWebhookAudiences">Audience relations of accepted webhook tokens.</param>
/// <param name="AcceptedWebhookLifetimes">Lifetimes of accepted webhook tokens.</param>
/// <param name="VerifierCleanup">The PKCE verifier cleanup.</param>
/// <param name="Backbone">The relay and the consumer.</param>
/// <param name="RefusedLogProviders">Attempts to attach a log provider behind the harness's log policy.</param>
/// <param name="WithheldLogRecords">Log records the policy did not show, by whose they were.</param>
public sealed record HarnessDiagnosticsSnapshot(
    IReadOnlyDictionary<(ProviderEndpoint Endpoint, ProviderCallOutcome Outcome), long> ProviderCalls,
    IReadOnlyDictionary<(OperatorRoute Route, OperatorRefusal Refusal), long> OperatorRequests,
    IReadOnlyDictionary<AuthorizationStartOutcome, long> AuthorizationStarts,
    IReadOnlyDictionary<AuthorizationCompletionOutcome, long> AuthorizationCompletions,
    IReadOnlyDictionary<CallbackParameter, long> CallbackParameters,
    IReadOnlyDictionary<IssuerRelation, long> CallbackIssuers,
    IReadOnlyDictionary<WebhookRequestShape, long> WebhookRequests,
    IReadOnlyDictionary<WebhookClaim, long> AcceptedWebhookClaims,
    IReadOnlyDictionary<AudienceRelation, long> AcceptedWebhookAudiences,
    WebhookLifetimeSummary AcceptedWebhookLifetimes,
    VerifierCleanupSummary VerifierCleanup,
    BackboneSummary Backbone,
    long RefusedLogProviders,
    IReadOnlyDictionary<LogRecordOwner, long> WithheldLogRecords);

/// <summary>
/// The harness's diagnostics: counters in memory, keyed by closed enumerations (owner decision D3G-6).
/// </summary>
/// <remarks>
/// Nothing here can hold a value the harness received: every recording method takes enumerations, booleans and
/// numbers, and a number is bounded before it is kept. There is no string, no byte array and no object member, so a
/// callback parameter, a claim, a header, a provider message or an exception text has nowhere to go. An unknown
/// category is counted as "Other". Nothing is written to a file, a database or a log.
/// </remarks>
public sealed class HarnessDiagnostics
{
    /// <summary>The largest lifetime kept, in seconds: one day.</summary>
    public const int MaxLifetimeSeconds = 86_400;

    /// <summary>The largest per-run count kept.</summary>
    public const int MaxRunCount = 1_000;

    private readonly ConcurrentDictionary<(ProviderEndpoint, ProviderCallOutcome), long> _providerCalls = new();
    private readonly ConcurrentDictionary<(OperatorRoute, OperatorRefusal), long> _operatorRequests = new();
    private readonly ConcurrentDictionary<AuthorizationStartOutcome, long> _starts = new();
    private readonly ConcurrentDictionary<AuthorizationCompletionOutcome, long> _completions = new();
    private readonly ConcurrentDictionary<CallbackParameter, long> _callbackParameters = new();
    private readonly ConcurrentDictionary<IssuerRelation, long> _callbackIssuers = new();
    private readonly ConcurrentDictionary<WebhookRequestShape, long> _webhookRequests = new();
    private readonly ConcurrentDictionary<WebhookClaim, long> _webhookClaims = new();
    private readonly ConcurrentDictionary<AudienceRelation, long> _webhookAudiences = new();
    private readonly ConcurrentDictionary<LogRecordOwner, long> _withheldLogRecords = new();
    private readonly Lock _gate = new();
    private long _lifetimeCount;
    private int _lifetimeMinimum;
    private int _lifetimeMaximum;
    private bool _startupCleanupCompleted;
    private long _cleanupRuns;
    private CleanupOutcome _lastCleanupOutcome;
    private int _lastCleanupDeleted;
    private int _lastCleanupRemaining;
    private long _totalCleanupDeleted;
    private long _relayPasses;
    private long _relayPublished;
    private long _relayFailedPasses;
    private bool _consumerStarted;
    private long _refusedLogProviders;

    /// <summary>Counts one provider request.</summary>
    public void ProviderCall(ProviderEndpoint endpoint, ProviderCallOutcome outcome) =>
        _providerCalls.AddOrUpdate((endpoint, outcome), 1, static (_, count) => count + 1);

    /// <summary>Counts one operator listener request.</summary>
    public void OperatorRequest(OperatorRoute route, OperatorRefusal refusal) =>
        _operatorRequests.AddOrUpdate((route, refusal), 1, static (_, count) => count + 1);

    /// <summary>Counts one authorization start.</summary>
    public void AuthorizationStart(AuthorizationStartOutcome outcome) =>
        _starts.AddOrUpdate(outcome, 1, static (_, count) => count + 1);

    /// <summary>Counts one authorization callback that reached the authorizer.</summary>
    public void AuthorizationCompletion(AuthorizationCompletionOutcome outcome) =>
        _completions.AddOrUpdate(outcome, 1, static (_, count) => count + 1);

    /// <summary>Counts one callback parameter name.</summary>
    public void CallbackParameterSeen(CallbackParameter parameter) =>
        _callbackParameters.AddOrUpdate(parameter, 1, static (_, count) => count + 1);

    /// <summary>Counts one callback's issuer relation.</summary>
    public void CallbackIssuer(IssuerRelation relation) =>
        _callbackIssuers.AddOrUpdate(relation, 1, static (_, count) => count + 1);

    /// <summary>Counts one webhook request by its shape.</summary>
    public void WebhookRequest(WebhookRequestShape shape) =>
        _webhookRequests.AddOrUpdate(shape, 1, static (_, count) => count + 1);

    /// <summary>Counts one claim name of an accepted webhook token.</summary>
    public void AcceptedWebhookClaim(WebhookClaim claim) =>
        _webhookClaims.AddOrUpdate(claim, 1, static (_, count) => count + 1);

    /// <summary>Counts one accepted webhook token's audience relation.</summary>
    public void AcceptedWebhookAudience(AudienceRelation relation) =>
        _webhookAudiences.AddOrUpdate(relation, 1, static (_, count) => count + 1);

    /// <summary>Records one accepted webhook token's lifetime, bounded to [0, one day].</summary>
    public void AcceptedWebhookLifetime(long seconds)
    {
        var bounded = (int)Math.Clamp(seconds, 0, MaxLifetimeSeconds);
        lock (_gate)
        {
            _lifetimeMinimum = _lifetimeCount == 0 ? bounded : Math.Min(_lifetimeMinimum, bounded);
            _lifetimeMaximum = _lifetimeCount == 0 ? bounded : Math.Max(_lifetimeMaximum, bounded);
            _lifetimeCount++;
        }
    }

    /// <summary>Records one PKCE verifier cleanup, with its counts bounded.</summary>
    public void VerifierCleanupRan(bool startup, CleanupOutcome outcome, int deleted, int remaining)
    {
        lock (_gate)
        {
            _cleanupRuns++;
            _startupCleanupCompleted |= startup;
            _lastCleanupOutcome = outcome;
            _lastCleanupDeleted = Math.Clamp(deleted, 0, MaxRunCount);
            _lastCleanupRemaining = Math.Clamp(remaining, 0, MaxRunCount);
            _totalCleanupDeleted += _lastCleanupDeleted;
        }
    }

    /// <summary>Records one relay pass, by counts.</summary>
    public void RelayPass(int published, bool failed)
    {
        lock (_gate)
        {
            _relayPasses++;
            _relayPublished += Math.Clamp(published, 0, MaxRunCount);
            _relayFailedPasses += failed ? 1 : 0;
        }
    }

    /// <summary>Records that the consumer host was started.</summary>
    public void ConsumerStarted()
    {
        lock (_gate)
        {
            _consumerStarted = true;
        }
    }

    /// <summary>Counts an attempt to attach a log provider behind the harness's log policy.</summary>
    public void LogProviderRefused() => Interlocked.Increment(ref _refusedLogProviders);

    /// <summary>Counts one log record the policy did not show, by whose it was. Nothing of the record is kept.</summary>
    public void LogRecordWithheld(LogRecordOwner owner) =>
        _withheldLogRecords.AddOrUpdate(owner, 1, static (_, count) => count + 1);

    /// <summary>Everything observed so far.</summary>
    public HarnessDiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HarnessDiagnosticsSnapshot(
                _providerCalls.ToDictionary(entry => entry.Key, entry => entry.Value),
                _operatorRequests.ToDictionary(entry => entry.Key, entry => entry.Value),
                _starts.ToDictionary(entry => entry.Key, entry => entry.Value),
                _completions.ToDictionary(entry => entry.Key, entry => entry.Value),
                _callbackParameters.ToDictionary(entry => entry.Key, entry => entry.Value),
                _callbackIssuers.ToDictionary(entry => entry.Key, entry => entry.Value),
                _webhookRequests.ToDictionary(entry => entry.Key, entry => entry.Value),
                _webhookClaims.ToDictionary(entry => entry.Key, entry => entry.Value),
                _webhookAudiences.ToDictionary(entry => entry.Key, entry => entry.Value),
                new WebhookLifetimeSummary(_lifetimeCount, _lifetimeMinimum, _lifetimeMaximum),
                new VerifierCleanupSummary(
                    _startupCleanupCompleted, _cleanupRuns, _lastCleanupOutcome, _lastCleanupDeleted, _lastCleanupRemaining, _totalCleanupDeleted),
                new BackboneSummary(_relayPasses, _relayPublished, _relayFailedPasses, _consumerStarted),
                Interlocked.Read(ref _refusedLogProviders),
                _withheldLogRecords.ToDictionary(entry => entry.Key, entry => entry.Value));
        }
    }

    /// <summary>The snapshot as lines of enumeration names and numbers, for the operator console.</summary>
    public IReadOnlyList<string> Render()
    {
        var snapshot = Snapshot();
        var lines = new List<string>();
        lines.AddRange(snapshot.ProviderCalls.OrderBy(entry => entry.Key).Select(entry =>
            Line("provider-call", $"{entry.Key.Endpoint}/{entry.Key.Outcome}", entry.Value)));
        lines.AddRange(snapshot.OperatorRequests.OrderBy(entry => entry.Key).Select(entry =>
            Line("operator-request", $"{entry.Key.Route}/{entry.Key.Refusal}", entry.Value)));
        lines.AddRange(snapshot.AuthorizationStarts.OrderBy(entry => entry.Key).Select(entry =>
            Line("authorization-start", entry.Key.ToString(), entry.Value)));
        lines.AddRange(snapshot.AuthorizationCompletions.OrderBy(entry => entry.Key).Select(entry =>
            Line("authorization-completion", entry.Key.ToString(), entry.Value)));
        lines.AddRange(snapshot.CallbackParameters.OrderBy(entry => entry.Key).Select(entry =>
            Line("callback-parameter", entry.Key.ToString(), entry.Value)));
        lines.AddRange(snapshot.CallbackIssuers.OrderBy(entry => entry.Key).Select(entry =>
            Line("callback-issuer", entry.Key.ToString(), entry.Value)));
        lines.AddRange(snapshot.WebhookRequests
            .OrderBy(entry => entry.Key.Authorization).ThenBy(entry => entry.Key.Algorithm).ThenBy(entry => entry.Key.Type).ThenBy(entry => entry.Key.Response)
            .Select(entry => Line(
                "webhook-request",
                $"{entry.Key.Authorization}/{entry.Key.Algorithm}/{entry.Key.Type}/{entry.Key.Response}",
                entry.Value)));
        lines.AddRange(snapshot.AcceptedWebhookClaims.OrderBy(entry => entry.Key).Select(entry =>
            Line("accepted-webhook-claim", entry.Key.ToString(), entry.Value)));
        lines.AddRange(snapshot.AcceptedWebhookAudiences.OrderBy(entry => entry.Key).Select(entry =>
            Line("accepted-webhook-audience", entry.Key.ToString(), entry.Value)));
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"accepted-webhook-lifetime count={snapshot.AcceptedWebhookLifetimes.Count} min-seconds={snapshot.AcceptedWebhookLifetimes.MinimumSeconds} max-seconds={snapshot.AcceptedWebhookLifetimes.MaximumSeconds}"));
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"verifier-cleanup startup-run={snapshot.VerifierCleanup.StartupRunCompleted} runs={snapshot.VerifierCleanup.Runs} last={snapshot.VerifierCleanup.LastOutcome} last-deleted={snapshot.VerifierCleanup.LastDeleted} last-remaining={snapshot.VerifierCleanup.LastRemaining} total-deleted={snapshot.VerifierCleanup.TotalDeleted}"));
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"backbone relay-passes={snapshot.Backbone.RelayPasses} relay-published={snapshot.Backbone.RelayPublished} relay-failed-passes={snapshot.Backbone.RelayFailedPasses} consumer-started={snapshot.Backbone.ConsumerStarted}"));
        lines.Add(Line("log-provider", "Refused", snapshot.RefusedLogProviders));
        lines.AddRange(snapshot.WithheldLogRecords.OrderBy(entry => entry.Key).Select(entry =>
            Line("log-record-withheld", entry.Key.ToString(), entry.Value)));
        return lines;

        static string Line(string kind, string key, long count) =>
            string.Create(CultureInfo.InvariantCulture, $"{kind} {key} = {count}");
    }
}
