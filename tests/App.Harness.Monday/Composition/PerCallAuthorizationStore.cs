using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;
using WorkContinuity.Infrastructure.Connectors;

namespace WorkContinuity.Harness.Monday.Composition;

/// <summary>
/// The production authorization store, reached through a dependency scope of its own for every call.
/// </summary>
/// <remarks>
/// The connector's credential source and authorizer outlive any one piece of work, while the store works on a
/// tenant-scoped database that lives for one scope. Giving every call its own scope keeps the store's transactions
/// off the database context of whichever engine happens to be asking for a credential, exactly as the existing test
/// stacks give the store a context of its own. It changes nothing else: each call is forwarded unchanged to
/// <see cref="ConnectorAuthorizationStore"/>, which acts in the trusted tenant of the calling flow and of no other.
/// </remarks>
internal sealed class PerCallAuthorizationStore(IServiceScopeFactory scopes) : IConnectorAuthorizationStore
{
    public Task<Result<bool>> OpenTransactionAsync(ConnectorAuthorizationTransaction transaction, TimeSpan lifetime, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.OpenTransactionAsync(transaction, lifetime, cancellationToken));

    public Task<Result<ConnectorTransactionConsumption>> ConsumeTransactionAsync(
        Guid transactionId,
        UserId canonicalUser,
        ReadOnlyMemory<byte> stateHash,
        CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ConsumeTransactionAsync(transactionId, canonicalUser, stateHash, cancellationToken));

    public Task<Result<IReadOnlyList<Guid>>> CloseExpiredTransactionsAsync(int limit, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.CloseExpiredTransactionsAsync(limit, cancellationToken));

    public Task<Result<IReadOnlyList<Guid>>> ListVerifierCleanupAsync(int limit, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ListVerifierCleanupAsync(limit, cancellationToken));

    public Task<Result<bool>> RecordVerifierDeletedAsync(Guid transactionId, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.RecordVerifierDeletedAsync(transactionId, cancellationToken));

    public Task<Result<ConnectorAuthorization?>> ReadAsync(Guid connectionId, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ReadAsync(connectionId, cancellationToken));

    public Task<Result<bool>> ActivateAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ActivateAsync(grant, cancellationToken));

    public Task<Result<bool>> ReauthorizeAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ReauthorizeAsync(grant, cancellationToken));

    public Task<Result<bool>> ClaimRefreshAsync(Guid connectionId, long generation, Guid claim, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ClaimRefreshAsync(connectionId, generation, claim, cancellationToken));

    public Task<Result<bool>> CompleteRefreshAsync(
        Guid connectionId,
        long generation,
        Guid claim,
        DateTimeOffset accessExpiresAt,
        string grantedScopes,
        CancellationToken cancellationToken) =>
        InScopeAsync(store => store.CompleteRefreshAsync(connectionId, generation, claim, accessExpiresAt, grantedScopes, cancellationToken));

    public Task<Result<bool>> ReleaseRefreshAsync(Guid connectionId, Guid claim, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.ReleaseRefreshAsync(connectionId, claim, cancellationToken));

    public Task<Result<bool>> RequireReauthorizationAsync(Guid connectionId, long generation, Guid? claim, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.RequireReauthorizationAsync(connectionId, generation, claim, cancellationToken));

    public Task<Result<ConnectorDisconnection>> DisconnectAsync(Guid connectionId, UserId requestedBy, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.DisconnectAsync(connectionId, requestedBy, cancellationToken));

    public Task<Result<bool>> RecordProviderRevocationAsync(Guid connectionId, bool confirmed, CancellationToken cancellationToken) =>
        InScopeAsync(store => store.RecordProviderRevocationAsync(connectionId, confirmed, cancellationToken));

    private async Task<TResult> InScopeAsync<TResult>(Func<ConnectorAuthorizationStore, Task<TResult>> call)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<ConnectorAuthorizationStore>()).ConfigureAwait(false);
    }
}
