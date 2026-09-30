using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;

namespace WorkContinuity.Infrastructure.Ingress;

/// <summary>
/// Answers whether a connection id names an active connection of a source system, through the same approved resolver
/// ingress uses (Gate 3C).
/// </summary>
/// <remarks>
/// It adds no database function and no query: it calls <see cref="IngressConnectionResolver"/>, whose single statement
/// is the approved <c>resolve_ingress_connection</c>, and reduces the answer to a status. The tenant the resolver
/// returns never leaves this class, and a connection of another source system is reported exactly like an unknown one.
/// A failure is logged with the connection id only.
/// </remarks>
internal sealed class IngressConnectionCheck(
    IngressConnectionResolver connectionResolver,
    ILogger<IngressConnectionCheck> logger) : IIngressConnectionCheck
{
    /// <inheritdoc />
    public async Task<IngressConnectionStatus> CheckAsync(
        Guid connectionId,
        string sourceSystem,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty || !EventText.IsToken(sourceSystem))
        {
            return IngressConnectionStatus.Unknown;
        }

        ResolvedIngressConnection? connection;
        try
        {
            connection = await connectionResolver.ResolveAsync(connectionId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "The connection check could not resolve connection {ConnectionId}.",
                connectionId);
            return IngressConnectionStatus.Unavailable;
        }

        return connection is not null && string.Equals(connection.SourceSystem, sourceSystem, StringComparison.Ordinal)
            ? IngressConnectionStatus.Active
            : IngressConnectionStatus.Unknown;
    }
}
