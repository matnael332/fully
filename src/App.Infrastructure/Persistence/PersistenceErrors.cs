using WorkContinuity.Core.Results;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// The safe application errors a persistence operation can produce.
/// </summary>
/// <remarks>
/// Every message here is a fixed string. Database exception text can contain SQL, table and column
/// names, parameter values and connection details, so it is never propagated into an
/// <see cref="Error"/>; it is logged server-side and correlated by correlation id instead.
/// </remarks>
public static class PersistenceErrors
{
    /// <summary>No trusted tenant context was available, so the operation was refused.</summary>
    public static Error TenantContextMissing { get; } = Error.Unauthorized(
        "persistence.tenant_context_missing",
        "The operation requires a resolved tenant context.");

    /// <summary>A uniqueness constraint rejected the write.</summary>
    public static Error Duplicate { get; } = Error.Conflict(
        "persistence.duplicate",
        "The record already exists.");

    /// <summary>
    /// Storage-level tenant isolation rejected the operation, for example an attempt to write a row
    /// labelled with a different tenant than the session.
    /// </summary>
    public static Error TenantIsolationViolation { get; } = Error.Forbidden(
        "persistence.tenant_isolation_violation",
        "The operation was refused by tenant isolation.");

    /// <summary>A required foreign key or check constraint rejected the write.</summary>
    public static Error ConstraintViolation { get; } = Error.Validation(
        "persistence.constraint_violation",
        "The record failed a database constraint.");

    /// <summary>The database could not complete the operation.</summary>
    public static Error Unavailable { get; } = Error.Infrastructure(
        "persistence.unavailable",
        "The operation could not be completed. Quote the correlation id when reporting this.");
}
