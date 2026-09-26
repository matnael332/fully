namespace WorkContinuity.Core.Tenancy;

/// <summary>
/// Canonical internal tenant identifier.
/// </summary>
/// <remarks>
/// The identifier itself carries no authority. Parsing a <see cref="TenantId"/> from a string
/// proves only that the string was well formed; it never establishes a security context.
/// A tenant becomes part of a security context exclusively through
/// <see cref="TenantContext.Resolved"/>, which demands a trusted
/// <see cref="TenantResolutionSource"/>.
/// </remarks>
public readonly record struct TenantId
{
    private readonly Guid _value;

    private TenantId(Guid value) => _value = value;

    /// <summary>Gets the underlying value. <see cref="Guid.Empty"/> means "no tenant".</summary>
    public Guid Value => _value;

    /// <summary>Gets a value indicating whether this identifier is unset.</summary>
    public bool IsEmpty => _value == Guid.Empty;

    /// <summary>Generates a new tenant identifier.</summary>
    public static TenantId New() => new(Guid.CreateVersion7());

    /// <summary>Creates a tenant identifier from a known value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public static TenantId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("A tenant id must not be an empty GUID.", nameof(value))
        : new TenantId(value);

    /// <summary>
    /// Attempts to parse a tenant identifier. Success indicates well-formedness only, never trust.
    /// </summary>
    public static bool TryParse(string? candidate, out TenantId tenantId)
    {
        tenantId = default;

        if (!Guid.TryParse(candidate, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        tenantId = new TenantId(parsed);
        return true;
    }

    public override string ToString() => _value.ToString("D");
}
