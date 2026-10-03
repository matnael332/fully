using WorkContinuity.Core.Reconciliation;

namespace WorkContinuity.Core.InitialSync;

/// <summary>How the baseline of one configured source scope ended in one attempt. A closed set with fixed codes.</summary>
public enum SyncScopeOutcome
{
    /// <summary>Unspecified. Never recorded.</summary>
    None = 0,

    /// <summary>Every page of the scope was read and every supported field applied through processing.</summary>
    Completed = 1,

    /// <summary>The source could not be reached or failed transiently.</summary>
    ProviderUnavailable = 10,

    /// <summary>The source asked the caller to wait.</summary>
    RateLimited = 11,

    /// <summary>The credential could not be obtained just now; nothing was sent.</summary>
    CredentialUnavailable = 12,

    /// <summary>
    /// The source refused to continue a scan, for example because its continuation expired: the scope is read again from
    /// its first page.
    /// </summary>
    ContinuationRejected = 13,

    /// <summary>The connection, its authorization or its user does not stand, or the source refused the credential.</summary>
    ConnectionUnavailable = 20,

    /// <summary>The tenant is not active.</summary>
    TenantInactive = 21,

    /// <summary>The connection was authorized again during the generation: what it read is stale and nothing more applies.</summary>
    AuthorityChanged = 22,

    /// <summary>
    /// The connection's enabled scope set changed: a baseline of the previous set no longer represents the configuration, so
    /// the generation must restart over the new set.
    /// </summary>
    ScopeSetChanged = 23,

    /// <summary>The source did not return the scope's resource: missing, or not accessible to the connection.</summary>
    ScopeMissing = 30,

    /// <summary>The source's response did not have a shape the reader understands.</summary>
    UnsupportedResponse = 31,

    /// <summary>The source reports the resource archived or deleted; nothing is applied from it.</summary>
    ScopeInactive = 40,

    /// <summary>The resource is of a kind or hierarchy this sync does not support.</summary>
    ScopeUnsupported = 41,

    /// <summary>The resource has no field of a supported kind.</summary>
    NoSupportedField = 42,

    /// <summary>The source answered with another resource, object or field than the one asked for: nothing is remapped.</summary>
    BindingMismatch = 43,

    /// <summary>The read did not use the connection's current delegated identity.</summary>
    IdentityMismatch = 44,
}

/// <summary>The codes and dispositions of scope outcomes, classified by what retrying can change, as in Gate 3E.</summary>
public static class SyncScopeOutcomes
{
    private static readonly IReadOnlyDictionary<SyncScopeOutcome, (string Code, ReconciliationDisposition Disposition)> Table =
        new Dictionary<SyncScopeOutcome, (string, ReconciliationDisposition)>
        {
            [SyncScopeOutcome.Completed] = ("initial_sync.scope.completed", ReconciliationDisposition.Resolve),
            [SyncScopeOutcome.ProviderUnavailable] = ("initial_sync.provider.unavailable", ReconciliationDisposition.Retry),
            [SyncScopeOutcome.RateLimited] = ("initial_sync.provider.rate_limited", ReconciliationDisposition.Retry),
            [SyncScopeOutcome.CredentialUnavailable] = ("initial_sync.credential.unavailable", ReconciliationDisposition.Retry),
            [SyncScopeOutcome.ContinuationRejected] = ("initial_sync.provider.continuation_rejected", ReconciliationDisposition.Retry),
            [SyncScopeOutcome.ConnectionUnavailable] = ("initial_sync.connection.unavailable", ReconciliationDisposition.AwaitAccess),
            [SyncScopeOutcome.TenantInactive] = ("initial_sync.tenant.inactive", ReconciliationDisposition.AwaitAccess),
            [SyncScopeOutcome.AuthorityChanged] = ("initial_sync.authority.changed", ReconciliationDisposition.AwaitAccess),
            [SyncScopeOutcome.ScopeSetChanged] = ("initial_sync.scope_set.changed", ReconciliationDisposition.AwaitAccess),
            [SyncScopeOutcome.ScopeMissing] = ("initial_sync.scope.missing", ReconciliationDisposition.RetryThenHold),
            [SyncScopeOutcome.UnsupportedResponse] = ("initial_sync.provider.unsupported_response", ReconciliationDisposition.RetryThenHold),
            [SyncScopeOutcome.ScopeInactive] = ("initial_sync.scope.inactive", ReconciliationDisposition.Hold),
            [SyncScopeOutcome.ScopeUnsupported] = ("initial_sync.scope.unsupported", ReconciliationDisposition.Hold),
            [SyncScopeOutcome.NoSupportedField] = ("initial_sync.scope.no_supported_field", ReconciliationDisposition.Hold),
            [SyncScopeOutcome.BindingMismatch] = ("initial_sync.binding.mismatch", ReconciliationDisposition.Hold),
            [SyncScopeOutcome.IdentityMismatch] = ("initial_sync.identity.mismatch", ReconciliationDisposition.Hold),
        };

    private static readonly IReadOnlyDictionary<string, SyncScopeOutcome> ByCode =
        Table.ToDictionary(entry => entry.Value.Code, entry => entry.Key, StringComparer.Ordinal);

    // What only the engine and the configuration decide: completion, the tenant's standing, an authority change and a change
    // of the scope set.
    private static readonly HashSet<SyncScopeOutcome> EngineOnly =
    [
        SyncScopeOutcome.Completed,
        SyncScopeOutcome.TenantInactive,
        SyncScopeOutcome.AuthorityChanged,
        SyncScopeOutcome.ScopeSetChanged,
    ];

    /// <summary>Gets every defined outcome.</summary>
    public static IReadOnlyCollection<SyncScopeOutcome> All { get; } = Table.Keys.ToArray();

    /// <summary>The outcome's fixed code.</summary>
    /// <exception cref="ArgumentException">The outcome is not defined.</exception>
    public static string CodeOf(SyncScopeOutcome outcome) =>
        Table.TryGetValue(outcome, out var entry) ? entry.Code : throw new ArgumentException("Undefined outcome.", nameof(outcome));

    /// <summary>What the outcome does to the scope.</summary>
    /// <exception cref="ArgumentException">The outcome is not defined.</exception>
    public static ReconciliationDisposition DispositionOf(SyncScopeOutcome outcome) =>
        Table.TryGetValue(outcome, out var entry) ? entry.Disposition : throw new ArgumentException("Undefined outcome.", nameof(outcome));

    /// <summary>The outcome a recorded code names, matched exactly.</summary>
    public static bool TryParse(string? code, out SyncScopeOutcome outcome)
    {
        outcome = code is not null && ByCode.TryGetValue(code, out var named) ? named : SyncScopeOutcome.None;
        return outcome != SyncScopeOutcome.None;
    }

    /// <summary>
    /// Whether a reader may report the outcome: completion, the tenant, an authority change and a scope-set change are not.
    /// </summary>
    public static bool IsReaderRefusal(SyncScopeOutcome outcome) => Table.ContainsKey(outcome) && !EngineOnly.Contains(outcome);
}
