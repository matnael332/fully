using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Connectors;
using WorkContinuity.Infrastructure.Ingress;

namespace WorkContinuity.Web.Connectors;

/// <summary>Composes monday webhook intake in the Web host (Gate 3C).</summary>
public static class MondayWebhookRegistration
{
    /// <summary>The configuration key of the public HTTPS address the webhooks are registered under.</summary>
    public const string BaseAddressKey = "Connectors:Monday:WebhookBaseAddress";

    /// <summary>Registers only what webhook intake needs.</summary>
    /// <remarks>
    /// <para>
    /// The Gate 2 ingress and its connection check, the 3B authorization store the account binding reads, the webhook
    /// options, the connector's authenticator (one per request, so the Signing Secret is read at most once per request)
    /// and the production monday source verifier. Requires <c>AddPersistence</c>, an <c>ITenantContextAccessor</c> and an
    /// <c>IClock</c>. Nothing here starts a relay, a consumer, processing, a purge, reconciliation or a sync.
    /// </para>
    /// <para>
    /// The deployed secret store is an owner decision, so unless an approved <see cref="IMondaySecretStore"/> is
    /// registered first, a store that refuses every read is used: challenges are answered, and every event is refused
    /// with a retryable status. Without <see cref="BaseAddressKey"/> no token can be verified either; a configured value
    /// that is not an absolute HTTPS address stops the host at startup.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddMondayWebhookIntake(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);

        services.AddEventIngress();
        services.TryAddScoped<IConnectorAuthorizationStore, ConnectorAuthorizationStore>();
        services.TryAddSingleton<IMondaySecretStore, UnconfiguredMondaySecretStore>();
        services.AddScoped(provider => new MondayWebhookAuthenticator(
            provider.GetRequiredService<IMondaySecretStore>(),
            provider.GetRequiredService<IClock>(),
            options));
        services.AddScoped<ISourceVerifier, MondayWebhookVerifier>();
        return services;
    }

    private static MondayWebhookOptions? ReadOptions(IConfiguration configuration)
    {
        var value = configuration[BaseAddressKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var address)
            ? new MondayWebhookOptions(address)
            : throw new InvalidOperationException(
                $"The configuration value {BaseAddressKey} must be an absolute HTTPS address.");
    }
}

/// <summary>
/// The secret storage the Web host uses until an approved one is registered: it holds nothing and answers every
/// operation as unavailable, so nothing that needs a secret proceeds.
/// </summary>
/// <remarks>
/// The deployed secret store, for example an Azure Key Vault adapter with access for the Web identity, is an owner
/// decision (Gate 3B, decision D3B-2). Until then monday webhook challenges are answered and every event is refused with
/// a retryable status, because no token can be verified.
/// </remarks>
internal sealed class UnconfiguredMondaySecretStore : IMondaySecretStore
{
    /// <inheritdoc />
    public ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondaySecretStoreStatus.Unavailable);

    /// <inheritdoc />
    public ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondaySecretLookup.Unavailable());

    /// <inheritdoc />
    public ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondaySecretStoreStatus.Unavailable);
}
