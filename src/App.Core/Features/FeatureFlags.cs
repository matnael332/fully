using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Features;

/// <summary>
/// Canonical feature flag keys.
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// Learned personal automation executing without per-occasion approval.
    /// </summary>
    /// <remarks>
    /// This flag is OFF for the Human MVP. The supporting signals and infrastructure may exist and
    /// be exercised synthetically, but enabling real auto-execution is a product-owner decision.
    /// Writes, changes and sends require approval regardless of this flag.
    /// </remarks>
    public const string AutoExecutionFromLearning = "AUTO_EXECUTION_FROM_LEARNING";

    /// <summary>Gets every canonical flag key.</summary>
    public static IReadOnlyList<string> All { get; } = [AutoExecutionFromLearning];
}

/// <summary>
/// Reads feature flag state. Flags are disabled unless explicitly enabled.
/// </summary>
public interface IFeatureFlagProvider
{
    /// <summary>Determines whether a flag is enabled globally.</summary>
    bool IsEnabled(string flagKey);

    /// <summary>
    /// Determines whether a flag is enabled for a tenant, falling back to the global value when
    /// the tenant has no explicit override.
    /// </summary>
    bool IsEnabled(string flagKey, TenantId tenantId);
}

/// <summary>
/// In-memory <see cref="IFeatureFlagProvider"/> used for local development and tests.
/// </summary>
/// <remarks>
/// Gate 1C deliberately ships no durable store for flags. A configuration- or database-backed
/// provider replaces this implementation in a later Gate without changing the abstraction.
/// </remarks>
public sealed class InMemoryFeatureFlagProvider : IFeatureFlagProvider
{
    private readonly Dictionary<string, bool> _global =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string FlagKey, TenantId TenantId), bool> _perTenant = [];

    /// <inheritdoc />
    public bool IsEnabled(string flagKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);

        // Absent means disabled: a flag is never on by accident.
        return _global.TryGetValue(flagKey, out var enabled) && enabled;
    }

    /// <inheritdoc />
    public bool IsEnabled(string flagKey, TenantId tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);

        return _perTenant.TryGetValue((Normalize(flagKey), tenantId), out var enabled)
            ? enabled
            : IsEnabled(flagKey);
    }

    /// <summary>Sets a flag's global state.</summary>
    public void SetGlobal(string flagKey, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        _global[flagKey] = enabled;
    }

    /// <summary>Sets a flag's state for one tenant, overriding the global value.</summary>
    public void SetForTenant(string flagKey, TenantId tenantId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException(
                "A tenant-scoped flag requires a tenant.",
                nameof(tenantId));
        }

        _perTenant[(Normalize(flagKey), tenantId)] = enabled;
    }

    private static string Normalize(string flagKey) => flagKey.ToUpperInvariant();
}
