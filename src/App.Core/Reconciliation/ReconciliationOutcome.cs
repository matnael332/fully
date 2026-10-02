namespace WorkContinuity.Core.Reconciliation;

/// <summary>How one reconciliation of one target ended. A closed set, each with a fixed, content-free code.</summary>
public enum ReconciliationOutcome
{
    /// <summary>Unspecified. Never recorded.</summary>
    None = 0,

    /// <summary>The Snapshot already holds the source's state: nothing new was recorded.</summary>
    Current = 1,

    /// <summary>A confirmed change advanced the Snapshot through processing.</summary>
    Repaired = 2,

    /// <summary>The Snapshot already holds newer confirmed state than the read: processing found the read stale.</summary>
    Superseded = 3,

    /// <summary>The provider could not be reached or failed transiently.</summary>
    ProviderUnavailable = 10,

    /// <summary>The provider asked the caller to wait.</summary>
    RateLimited = 11,

    /// <summary>
    /// The credential could not be obtained just now: its state or secret could not be read, a renewal is undecided, or
    /// what was read did not match the connection. Nothing was sent; the authorization itself still stands.
    /// </summary>
    CredentialUnavailable = 12,

    /// <summary>
    /// The connection cannot be used: disabled or disconnected, unknown or of another source; its authorization revoked,
    /// needing authorization again or held by an inactive user; or the source rejected the credential or the identity.
    /// </summary>
    ConnectionUnavailable = 13,

    /// <summary>The tenant is not active.</summary>
    TenantInactive = 14,

    /// <summary>The source did not return the object. Absence is never read as deletion.</summary>
    ObjectMissing = 15,

    /// <summary>The source's response did not have a shape the reader understands.</summary>
    UnsupportedResponse = 16,

    /// <summary>The source reports the object archived or deleted; Core has no lifecycle for it, so nothing is applied.</summary>
    ObjectInactive = 20,

    /// <summary>The source answered with another object, scope or field than the target: nothing is remapped.</summary>
    BindingMismatch = 21,

    /// <summary>The source did not return the field.</summary>
    FieldMissing = 22,

    /// <summary>The field's type or shape is not one the reader supports.</summary>
    FieldUnsupported = 23,

    /// <summary>The value is not a valid canonical value, and is never cut to fit.</summary>
    ValueInvalid = 24,

    /// <summary>Ordering found the read neither newer nor older than the accepted state: nothing is forced.</summary>
    OrderingAmbiguous = 25,

    /// <summary>The read carried no ordering evidence; none is fabricated.</summary>
    OrderingUnevidenced = 26,

    /// <summary>Processing found the read's order impossible, for example a source time beyond the tolerated clock skew.</summary>
    OrderingImpossible = 27,

    /// <summary>Processing quarantined the read for another reason.</summary>
    Quarantined = 28,

    /// <summary>The target has no source scope, which its source requires.</summary>
    ScopeMissing = 29,

    /// <summary>A request named another scope or data type than the target holds: the target's identity is never replaced.</summary>
    ScopeConflict = 30,

    /// <summary>
    /// The read did not use the connection's current delegated identity, although the connection's authorization did not
    /// change while the source was read: a stable mismatch, never retried by itself.
    /// </summary>
    IdentityMismatch = 31,

    /// <summary>
    /// The connection's authorization changed while the source was read, for example because its user authorized it
    /// again: the read is stale and discarded, never applied, and the next attempt reads again under the current
    /// authorization. A credential renewal keeps the authorization and is no change.
    /// </summary>
    AuthorityChanged = 32,
}

/// <summary>What a recorded outcome does to its target.</summary>
/// <remarks>
/// Every retried target waits on the same capped backoff before its next attempt. What differs is whether retrying can
/// ever stop: a transient failure or a lack of access may outlast any number of attempts and ends by itself, so it is
/// never held; an answer the source gave but that cannot be used is retried a bounded number of times in a row; and what
/// retrying cannot change is held at once.
/// </remarks>
public enum ReconciliationDisposition
{
    /// <summary>Unspecified.</summary>
    None = 0,

    /// <summary>The target is resolved, if its generation is still the one that was claimed.</summary>
    Resolve = 1,

    /// <summary>
    /// Transient: the target stays pending and is retried on the capped backoff for as long as the failure lasts. It is
    /// never held for its number of attempts.
    /// </summary>
    Retry = 2,

    /// <summary>The target is held, unresolved, until a new request reopens it.</summary>
    Hold = 3,

    /// <summary>
    /// Access does not stand, or changed while the source was read: the target stays pending, and every later attempt
    /// checks the tenant, the connection and its authorization locally before any call, sending nothing while they do not
    /// stand, on the same capped backoff. It resumes by itself once access stands again and is never held for its number
    /// of attempts.
    /// </summary>
    AwaitAccess = 4,

    /// <summary>
    /// The source answered, but not usably: the target stays pending and is retried on the capped backoff, and is held
    /// once the bound of consecutive attempts that ended this way is reached.
    /// </summary>
    RetryThenHold = 5,
}

/// <summary>The codes and dispositions of reconciliation outcomes.</summary>
public static class ReconciliationOutcomes
{
    private static readonly IReadOnlyDictionary<ReconciliationOutcome, (string Code, ReconciliationDisposition Disposition)> Table =
        new Dictionary<ReconciliationOutcome, (string, ReconciliationDisposition)>
        {
            [ReconciliationOutcome.Current] = ("reconciliation.current", ReconciliationDisposition.Resolve),
            [ReconciliationOutcome.Repaired] = ("reconciliation.repaired", ReconciliationDisposition.Resolve),
            [ReconciliationOutcome.Superseded] = ("reconciliation.superseded", ReconciliationDisposition.Resolve),
            [ReconciliationOutcome.ProviderUnavailable] = ("reconciliation.provider.unavailable", ReconciliationDisposition.Retry),
            [ReconciliationOutcome.RateLimited] = ("reconciliation.provider.rate_limited", ReconciliationDisposition.Retry),
            [ReconciliationOutcome.CredentialUnavailable] = ("reconciliation.credential.unavailable", ReconciliationDisposition.Retry),
            [ReconciliationOutcome.ConnectionUnavailable] = ("reconciliation.connection.unavailable", ReconciliationDisposition.AwaitAccess),
            [ReconciliationOutcome.TenantInactive] = ("reconciliation.tenant.inactive", ReconciliationDisposition.AwaitAccess),
            [ReconciliationOutcome.ObjectMissing] = ("reconciliation.object.missing", ReconciliationDisposition.RetryThenHold),
            [ReconciliationOutcome.UnsupportedResponse] = ("reconciliation.provider.unsupported_response", ReconciliationDisposition.RetryThenHold),
            [ReconciliationOutcome.ObjectInactive] = ("reconciliation.object.inactive", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.BindingMismatch] = ("reconciliation.binding.mismatch", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.FieldMissing] = ("reconciliation.field.missing", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.FieldUnsupported] = ("reconciliation.field.unsupported", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.ValueInvalid] = ("reconciliation.value.invalid", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.OrderingAmbiguous] = ("reconciliation.ordering.ambiguous", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.OrderingUnevidenced] = ("reconciliation.ordering.no_evidence", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.OrderingImpossible] = ("reconciliation.ordering.impossible", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.Quarantined] = ("reconciliation.processing.quarantined", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.ScopeMissing] = ("reconciliation.scope.missing", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.ScopeConflict] = ("reconciliation.scope.conflict", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.IdentityMismatch] = ("reconciliation.identity.mismatch", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.AuthorityChanged] = ("reconciliation.authority.changed", ReconciliationDisposition.AwaitAccess),
        };

    private static readonly IReadOnlyDictionary<string, ReconciliationOutcome> ByCode =
        Table.ToDictionary(entry => entry.Value.Code, entry => entry.Key, StringComparer.Ordinal);

    private static readonly HashSet<ReconciliationOutcome> ReaderRefusals =
    [
        ReconciliationOutcome.ProviderUnavailable,
        ReconciliationOutcome.RateLimited,
        ReconciliationOutcome.CredentialUnavailable,
        ReconciliationOutcome.ConnectionUnavailable,
        ReconciliationOutcome.ObjectMissing,
        ReconciliationOutcome.UnsupportedResponse,
        ReconciliationOutcome.ObjectInactive,
        ReconciliationOutcome.BindingMismatch,
        ReconciliationOutcome.FieldMissing,
        ReconciliationOutcome.FieldUnsupported,
        ReconciliationOutcome.ValueInvalid,
        ReconciliationOutcome.ScopeMissing,
        ReconciliationOutcome.IdentityMismatch,
    ];

    /// <summary>Gets every defined outcome.</summary>
    public static IReadOnlyCollection<ReconciliationOutcome> All { get; } = Table.Keys.ToArray();

    /// <summary>The outcome's fixed code.</summary>
    /// <exception cref="ArgumentException">The outcome is not defined.</exception>
    public static string CodeOf(ReconciliationOutcome outcome) =>
        Table.TryGetValue(outcome, out var entry) ? entry.Code : throw new ArgumentException("Undefined outcome.", nameof(outcome));

    /// <summary>What the outcome does to its target.</summary>
    /// <exception cref="ArgumentException">The outcome is not defined.</exception>
    public static ReconciliationDisposition DispositionOf(ReconciliationOutcome outcome) =>
        Table.TryGetValue(outcome, out var entry) ? entry.Disposition : throw new ArgumentException("Undefined outcome.", nameof(outcome));

    /// <summary>Whether a reader may report the outcome: resolutions and processing's outcomes are never a reader's.</summary>
    public static bool IsReaderRefusal(ReconciliationOutcome outcome) => ReaderRefusals.Contains(outcome);

    /// <summary>The outcome a recorded code names, matched exactly.</summary>
    /// <param name="code">A recorded outcome code.</param>
    /// <param name="outcome">The outcome, or <see cref="ReconciliationOutcome.None"/> when the code names none.</param>
    /// <returns>Whether the code names an outcome.</returns>
    public static bool TryParse(string? code, out ReconciliationOutcome outcome)
    {
        outcome = code is not null && ByCode.TryGetValue(code, out var named) ? named : ReconciliationOutcome.None;
        return outcome != ReconciliationOutcome.None;
    }
}
