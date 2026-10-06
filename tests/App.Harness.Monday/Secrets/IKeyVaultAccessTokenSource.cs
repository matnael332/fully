namespace WorkContinuity.Harness.Monday.Secrets;

/// <summary>
/// Supplies the bearer credential for requests to the Key Vault data plane (owner decision S-2).
/// </summary>
/// <remarks>
/// A seam only. The offline slice has no implementation that reaches an identity provider: tests supply a fake, and the
/// harness supplies nothing, because the adapter is not wired. A real source is a separate, later decision.
/// </remarks>
public interface IKeyVaultAccessTokenSource
{
    /// <summary>Returns an access token for the vault, or null when none can be had. Never logs the token.</summary>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}
