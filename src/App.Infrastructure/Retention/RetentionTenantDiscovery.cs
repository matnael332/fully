using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Retention;

/// <summary>
/// The only code that reads receipt data for retention before a tenant is known.
/// </summary>
/// <remarks>
/// It calls exactly one database function, the retention function of Gate 2 owner decision 1: it returns the
/// ids of the tenants holding content that the database's own clock shows to be past the 28-day purge age, oldest
/// due content first, at most 100 of them, and nothing else: no receipt, no content, no count, no lock. It takes no
/// cutoff, so no application clock can move what is due. The statement is fixed and parameterised; the limit is its
/// only input. Purging happens afterwards under each tenant's own forced row-level security.
/// </remarks>
internal sealed class RetentionTenantDiscovery(WorkContinuityDbContext context)
{
    private const string DiscoverSql = "SELECT tenant_id FROM public.retention_expired_tenants(@limit)";

    /// <summary>Returns up to <paramref name="limit"/> tenants holding due content, oldest due content first.</summary>
    public async Task<IReadOnlyList<TenantId>> FindAsync(int limit, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = DiscoverSql;
            command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });

            var tenants = new List<TenantId>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tenants.Add(TenantId.From(reader.GetGuid(0)));
            }

            return tenants;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
