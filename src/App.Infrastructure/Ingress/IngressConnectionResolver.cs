using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Ingress;

/// <summary>An active connection's owner and source system, as the approved function reports them.</summary>
internal sealed record ResolvedIngressConnection(TenantId TenantId, string SourceSystem);

/// <summary>
/// The only code that reads connection data before a tenant is known.
/// </summary>
/// <remarks>
/// It calls exactly one database function, the one Gate 2 owner decision 1 approves for resolving an
/// inbound connection: given one connection id, the function returns that connection's tenant id and
/// source system if the connection is active, and nothing otherwise. No payload, no other column, no
/// list, and no way to search. The statement is fixed and parameterised; the connection id is its only
/// input. Everything after this lookup runs tenant-scoped under forced row-level security.
/// </remarks>
internal sealed class IngressConnectionResolver(WorkContinuityDbContext context)
{
    private const string ResolveSql =
        "SELECT tenant_id, source_system FROM public.resolve_ingress_connection(@connection_id)";

    /// <summary>Resolves an active connection, or returns null when there is none with this id.</summary>
    public async Task<ResolvedIngressConnection?> ResolveAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = ResolveSql;
            command.Parameters.Add(new NpgsqlParameter("connection_id", NpgsqlDbType.Uuid) { Value = connectionId });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new ResolvedIngressConnection(TenantId.From(reader.GetGuid(0)), reader.GetString(1));
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
