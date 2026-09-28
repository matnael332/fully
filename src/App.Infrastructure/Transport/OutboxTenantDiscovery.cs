using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// The only code that reads outbox data before a tenant is known.
/// </summary>
/// <remarks>
/// It calls exactly one database function, the one Gate 2 owner decision 1 approves for finding outbox
/// work: it returns the ids of the tenants that have unpublished outbox messages, oldest pending work first,
/// at most 100 of them, and nothing else: no message, no payload, no row id, no lock. The statement is fixed
/// and parameterised; the limit is its only input. Claiming, publishing and marking all happen afterwards
/// under each tenant's own forced row-level security.
/// </remarks>
internal sealed class OutboxTenantDiscovery(WorkContinuityDbContext context)
{
    private const string DiscoverSql = "SELECT tenant_id FROM public.outbox_pending_tenants(@limit)";

    /// <summary>Returns up to <paramref name="limit"/> tenants with pending outbox messages, oldest work first.</summary>
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
