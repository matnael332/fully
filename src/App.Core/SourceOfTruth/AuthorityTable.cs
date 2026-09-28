using WorkContinuity.Core.Events;

namespace WorkContinuity.Core.SourceOfTruth;

/// <summary>
/// Who is authoritative for a data type (PRD §5).
/// </summary>
public enum AuthorityBasis
{
    /// <summary>No authority. Nothing is authoritative.</summary>
    None = 0,

    /// <summary>The source task/SaaS system: the connector that owns the task.</summary>
    SourceTaskSystem = 1,

    /// <summary>The Local Agent.</summary>
    LocalAgent = 2,

    /// <summary>The employee.</summary>
    Employee = 3,

    /// <summary>The organization identity system.</summary>
    OrganizationIdentitySystem = 4,

    /// <summary>
    /// The system in which the value was defined. In the field-level model a field is defined by the
    /// home system of the object that carries it, reached through the object's own connection.
    /// </summary>
    DefiningSystem = 5,

    /// <summary>The authoritative destination/executor confirming an action's result.</summary>
    AuthoritativeDestination = 6,
}

/// <summary>
/// The Source-of-Truth rules: one explicit authority per data type, exactly as PRD §5 defines them.
/// </summary>
/// <remarks>
/// <para>
/// There is no universal "latest source wins"; authority is defined by data type (PRD §5). The rules
/// are code, not configuration, and nothing here consults an AI or model output, so AI can never
/// choose which source is authoritative (invariants 5 and 21).
/// </para>
/// <para>
/// A data type without a rule, including <see cref="FactDataType.None"/>, has
/// <see cref="AuthorityBasis.None"/>: nothing is authoritative for it, so it can never become a
/// Snapshot fact.
/// </para>
/// </remarks>
public static class AuthorityTable
{
    /// <summary>Gets the authority rule for every data type that has one.</summary>
    public static IReadOnlyDictionary<FactDataType, AuthorityBasis> Rules { get; } =
        new Dictionary<FactDataType, AuthorityBasis>
        {
            [FactDataType.TaskStatus] = AuthorityBasis.SourceTaskSystem,
            [FactDataType.PcActivity] = AuthorityBasis.LocalAgent,
            [FactDataType.ApprovalDecision] = AuthorityBasis.Employee,
            [FactDataType.IdentityAndPermissions] = AuthorityBasis.OrganizationIdentitySystem,
            [FactDataType.Deadline] = AuthorityBasis.DefiningSystem,
            [FactDataType.ActionResult] = AuthorityBasis.AuthoritativeDestination,
        }.AsReadOnly();

    /// <summary>Gets the authority for a data type, or <see cref="AuthorityBasis.None"/> if it has no rule.</summary>
    public static AuthorityBasis BasisFor(FactDataType dataType) =>
        Rules.TryGetValue(dataType, out var basis) ? basis : AuthorityBasis.None;
}
