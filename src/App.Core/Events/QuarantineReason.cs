namespace WorkContinuity.Core.Events;

/// <summary>
/// Why an inbound event was kept away from the Snapshot, AI facts, the brief and actions.
/// </summary>
/// <remarks>
/// <para>
/// These are exactly the six reasons architecture §3 lists. Unknown, invalid or untrusted events never
/// reach the Snapshot (invariant 13).
/// </para>
/// <para>
/// <see cref="UnknownTenant"/> and <see cref="InvalidAuthentication"/> are enforced by rejecting the
/// payload before anything is persisted (Gate 2 owner decision 6): no tenant-less payload store exists.
/// They are still canonical reasons, so every refusal can be recorded as a safe reason code in
/// metadata-only logs.
/// </para>
/// </remarks>
public enum QuarantineReason
{
    /// <summary>Unspecified. Never valid.</summary>
    None = 0,

    /// <summary>The event does not match its schema, or its type is unknown.</summary>
    BadSchema = 1,

    /// <summary>The event cannot be attributed to a known tenant.</summary>
    UnknownTenant = 2,

    /// <summary>The event's type is known but its schema version is not supported.</summary>
    UnknownSchemaVersion = 3,

    /// <summary>The event's ordering relative to what is known is impossible.</summary>
    ImpossibleOrdering = 4,

    /// <summary>The event asserts a fact its source is not authoritative for.</summary>
    AuthorityConflict = 5,

    /// <summary>The source failed authentication.</summary>
    InvalidAuthentication = 6,
}
