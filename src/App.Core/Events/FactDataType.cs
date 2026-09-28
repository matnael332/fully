namespace WorkContinuity.Core.Events;

/// <summary>
/// The data types whose authority the product defines (PRD §5). Every field change declares one.
/// </summary>
/// <remarks>
/// <para>
/// The set is closed and mirrors PRD §5 exactly. A field whose data type is not listed cannot be
/// expressed in a canonical event, so no value can reach the Snapshot without an explicit authority
/// rule in <c>WorkContinuity.Core.SourceOfTruth.AuthorityTable</c>.
/// </para>
/// <para>
/// Adding a data type is a reviewed code change made together with its authority rule, never a
/// configuration or AI decision: Source of Truth is defined by code, and AI is never a Source of
/// Truth (invariants 5 and 21).
/// </para>
/// </remarks>
public enum FactDataType
{
    /// <summary>Unspecified. Never valid.</summary>
    None = 0,

    /// <summary>Task status. Authority: the source task/SaaS system.</summary>
    TaskStatus = 1,

    /// <summary>PC activity. Authority: the Local Agent.</summary>
    PcActivity = 2,

    /// <summary>A user approve/reject decision. Authority: the employee.</summary>
    ApprovalDecision = 3,

    /// <summary>Identity and permissions. Authority: the organization identity system.</summary>
    IdentityAndPermissions = 4,

    /// <summary>A deadline. Authority: the system in which the deadline was defined.</summary>
    Deadline = 5,

    /// <summary>An action result. Authority: the authoritative destination/executor confirmation.</summary>
    ActionResult = 6,
}
