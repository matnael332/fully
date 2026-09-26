using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// Runs database work inside a transaction bound to the trusted tenant of the current scope.
/// </summary>
/// <remarks>
/// This is the only persistence entry point available to normal Web and Worker code. There is no
/// overload that accepts a tenant, so a caller cannot choose which tenant to act as: the tenant is
/// taken from the server-resolved <see cref="ITenantContext"/>.
/// </remarks>
public interface ITenantScopedDatabase
{
    /// <summary>Runs work that returns a value, committing on success.</summary>
    Task<Result<TResult>> ExecuteAsync<TResult>(
        Func<WorkContinuityDbContext, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Runs work that returns no value, committing on success.</summary>
    Task<Result> ExecuteAsync(
        Func<WorkContinuityDbContext, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The tenant-scoped persistence implementation.
/// </summary>
/// <remarks>
/// <para>
/// Each call opens a transaction and sets the trusted tenant with
/// <c>set_config('app.tenant_id', &lt;id&gt;, true)</c>. Three properties follow from that choice:
/// </para>
/// <list type="bullet">
/// <item>
/// The third argument makes the setting <em>transaction-local</em>. PostgreSQL discards it when the
/// transaction ends, so a connection returned to the pool carries no tenant. One tenant's context
/// cannot reappear on a later request that reuses the same physical connection.
/// </item>
/// <item>
/// The tenant id travels as a bound parameter, never as interpolated SQL, so the value cannot alter
/// the statement.
/// </item>
/// <item>
/// It fails closed. Without a resolved tenant the method returns a failure before opening a
/// transaction, and if a query were somehow issued without the setting, the row-level security
/// policies compare against NULL and match no rows.
/// </item>
/// </list>
/// <para>
/// Row-level security is the second boundary, not the first. Application authorization still
/// decides what a caller may ask for; this stops a mistake there from becoming a cross-tenant read.
/// </para>
/// </remarks>
public sealed class TenantScopedDatabase(
    WorkContinuityDbContext context,
    ITenantContextAccessor tenantContextAccessor,
    ILogger<TenantScopedDatabase> logger) : ITenantScopedDatabase
{
    private const string SetTenantSql = "SELECT set_config(@tenant_variable, @tenant_id, true)";

    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(
        Func<WorkContinuityDbContext, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var result = await ExecuteAsync(
            async (dbContext, token) =>
            {
                await work(dbContext, token);
                return true;
            },
            cancellationToken);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    /// <inheritdoc />
    public async Task<Result<TResult>> ExecuteAsync<TResult>(
        Func<WorkContinuityDbContext, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Fail closed: no trusted tenant means no database work at all.
        if (!tenantContextAccessor.Current.TryGetTenantId(out var tenantId))
        {
            return Result<TResult>.Failure(PersistenceErrors.TenantContextMissing);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await ApplyTenantAsync(tenantId, cancellationToken);

            var value = await work(context, cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Result<TResult>.Success(value);
        }
        catch (DbUpdateException exception)
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            context.ChangeTracker.Clear();

            var error = Classify(exception.InnerException as PostgresException);

            logger.LogWarning(
                exception,
                "Persistence write rejected for tenant {TenantId}: {ErrorCode}",
                tenantId,
                error.Code);

            return Result<TResult>.Failure(error);
        }
        catch (PostgresException exception)
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            context.ChangeTracker.Clear();

            var error = Classify(exception);

            logger.LogWarning(
                exception,
                "Persistence operation rejected for tenant {TenantId}: {ErrorCode}",
                tenantId,
                error.Code);

            return Result<TResult>.Failure(error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The work delegate threw, so nothing it wrote may survive.
            await SafeRollbackAsync(transaction, cancellationToken);
            context.ChangeTracker.Clear();

            logger.LogError(
                exception,
                "Persistence operation failed for tenant {TenantId} and was rolled back.",
                tenantId);

            return Result<TResult>.Failure(PersistenceErrors.Unavailable);
        }
    }

    private async Task ApplyTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlRawAsync(
            SetTenantSql,
            [
                new NpgsqlParameter("tenant_variable", WorkContinuityDbContext.TenantSessionVariable),
                new NpgsqlParameter("tenant_id", tenantId.Value.ToString("D")),
            ],
            cancellationToken);

    private static Error Classify(PostgresException? exception) => exception?.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => PersistenceErrors.Duplicate,
        // A row-level security WITH CHECK failure surfaces as insufficient_privilege.
        PostgresErrorCodes.InsufficientPrivilege => PersistenceErrors.TenantIsolationViolation,
        PostgresErrorCodes.ForeignKeyViolation => PersistenceErrors.ConstraintViolation,
        PostgresErrorCodes.CheckViolation => PersistenceErrors.ConstraintViolation,
        PostgresErrorCodes.NotNullViolation => PersistenceErrors.ConstraintViolation,
        _ => PersistenceErrors.Unavailable,
    };

    private async Task SafeRollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception rollbackFailure) when (rollbackFailure is InvalidOperationException
            or PostgresException
            or NpgsqlException)
        {
            // The transaction is already gone; the write did not commit either way.
            logger.LogDebug(rollbackFailure, "Rollback was unnecessary or already completed.");
        }
    }
}
