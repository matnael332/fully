using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Audit;

/// <summary>What happened to the audited operation.</summary>
public enum AuditOutcome
{
    /// <summary>Unspecified. Never used by a real record.</summary>
    None = 0,

    /// <summary>The operation completed.</summary>
    Succeeded = 1,

    /// <summary>The operation was attempted and failed.</summary>
    Failed = 2,

    /// <summary>The operation was refused by policy, permission or approval rules.</summary>
    Denied = 3,

    /// <summary>
    /// The outcome could not be established. Reconciliation is required; this is not the same as
    /// a failure and must not be retried as if it were.
    /// </summary>
    Unknown = 4,
}

/// <summary>The kind of actor responsible for an audited operation.</summary>
public enum AuditActorKind
{
    /// <summary>Unspecified. Never used by a real record.</summary>
    None = 0,

    /// <summary>A human user acting for themselves.</summary>
    User = 1,

    /// <summary>A service or workload identity.</summary>
    ServiceIdentity = 2,

    /// <summary>The platform acting on its own schedule, for example a retention job.</summary>
    System = 3,
}

/// <summary>
/// An opaque reference to whoever performed an operation.
/// </summary>
/// <remarks>
/// The reference is an internal identifier, never an email address and never a display name.
/// </remarks>
public sealed record AuditActor
{
    /// <summary>The maximum supported reference length.</summary>
    public const int MaxReferenceLength = 200;

    /// <summary>Creates an audit actor.</summary>
    /// <exception cref="ArgumentException">The kind or reference is invalid.</exception>
    public AuditActor(AuditActorKind kind, string reference)
    {
        if (kind == AuditActorKind.None)
        {
            throw new ArgumentException("An audit actor requires a kind.", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(reference) || reference.Length > MaxReferenceLength)
        {
            throw new ArgumentException(
                $"An audit actor reference is required and must be at most {MaxReferenceLength} characters.",
                nameof(reference));
        }

        Kind = kind;
        Reference = reference;
    }

    /// <summary>Gets the actor kind.</summary>
    public AuditActorKind Kind { get; }

    /// <summary>Gets the opaque internal actor reference.</summary>
    public string Reference { get; }
}

/// <summary>
/// An opaque pointer to the resource an operation acted on.
/// </summary>
/// <remarks>
/// This is a type plus an identifier only. It never carries the resource's contents, so an audit
/// trail can say "this task was changed" without becoming a copy of the task.
/// </remarks>
public sealed record ResourceReference
{
    /// <summary>The maximum supported length of either component.</summary>
    public const int MaxLength = 200;

    /// <summary>Creates a resource reference.</summary>
    /// <exception cref="ArgumentException">The type or id is invalid.</exception>
    public ResourceReference(string resourceType, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceType) || resourceType.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A resource type is required and must be at most {MaxLength} characters.",
                nameof(resourceType));
        }

        if (string.IsNullOrWhiteSpace(resourceId) || resourceId.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A resource id is required and must be at most {MaxLength} characters.",
                nameof(resourceId));
        }

        ResourceType = resourceType;
        ResourceId = resourceId;
    }

    /// <summary>Gets the resource type, for example "task" or "action".</summary>
    public string ResourceType { get; }

    /// <summary>Gets the opaque resource identifier.</summary>
    public string ResourceId { get; }
}

/// <summary>
/// One security/audit event.
/// </summary>
/// <remarks>
/// <para>
/// This contract carries who did what to which resource, when, under which correlation id, and
/// with what outcome. It has no payload, content, body, document or snapshot member, and that
/// omission is deliberate: raw work data and security audit data have separate purposes and
/// separate retention lifecycles, so the audit trail must not become shadow storage for work
/// content. Raw work payloads live in their own store under their own rolling retention.
/// </para>
/// <para>
/// <see cref="Reason"/> records the rule or policy that produced the outcome — for example
/// "approval expired" — not the business data that was being processed.
/// </para>
/// </remarks>
public sealed record AuditRecord
{
    /// <summary>The maximum supported length of <see cref="Operation"/>.</summary>
    public const int MaxOperationLength = 200;

    /// <summary>The maximum supported length of <see cref="Reason"/>.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>Creates an audit record.</summary>
    /// <exception cref="ArgumentException">A required component is missing or invalid.</exception>
    public AuditRecord(
        TenantId tenantId,
        AuditActor actor,
        string operation,
        DateTimeOffset occurredAtUtc,
        CorrelationId correlationId,
        AuditOutcome outcome,
        ResourceReference? resource = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("An audit record requires a tenant.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(operation) || operation.Length > MaxOperationLength)
        {
            throw new ArgumentException(
                $"An operation is required and must be at most {MaxOperationLength} characters.",
                nameof(operation));
        }

        if (correlationId.IsEmpty)
        {
            throw new ArgumentException(
                "An audit record requires a correlation id.",
                nameof(correlationId));
        }

        if (outcome == AuditOutcome.None)
        {
            throw new ArgumentException("An audit record requires an outcome.", nameof(outcome));
        }

        if (reason is not null && reason.Length > MaxReasonLength)
        {
            throw new ArgumentException(
                $"A reason must be at most {MaxReasonLength} characters.",
                nameof(reason));
        }

        TenantId = tenantId;
        Actor = actor;
        Operation = operation;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
        Outcome = outcome;
        Resource = resource;
        Reason = reason;
    }

    /// <summary>Gets the tenant the event belongs to.</summary>
    public TenantId TenantId { get; }

    /// <summary>Gets the actor responsible for the operation.</summary>
    public AuditActor Actor { get; }

    /// <summary>Gets the operation name, for example "action.approve".</summary>
    public string Operation { get; }

    /// <summary>Gets the UTC instant the event occurred.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Gets the correlation id tying this event to its wider operation.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>Gets the outcome.</summary>
    public AuditOutcome Outcome { get; }

    /// <summary>Gets the affected resource, when one applies.</summary>
    public ResourceReference? Resource { get; }

    /// <summary>Gets the rule or policy reason behind the outcome, when one applies.</summary>
    public string? Reason { get; }
}
