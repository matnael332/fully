namespace WorkContinuity.Core.Identity;

/// <summary>
/// Canonical internal user/employee identifier.
/// </summary>
/// <remarks>
/// This is the only security identifier for a person in this platform. Email addresses are
/// mutable, reassignable and provider-controlled, so they are never used as an identity key.
/// </remarks>
public readonly record struct UserId
{
    private readonly Guid _value;

    private UserId(Guid value) => _value = value;

    /// <summary>Gets the underlying value. <see cref="Guid.Empty"/> means "no user".</summary>
    public Guid Value => _value;

    /// <summary>Gets a value indicating whether this identifier is unset.</summary>
    public bool IsEmpty => _value == Guid.Empty;

    /// <summary>Generates a new user identifier.</summary>
    public static UserId New() => new(Guid.CreateVersion7());

    /// <summary>Creates a user identifier from a known value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public static UserId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("A user id must not be an empty GUID.", nameof(value))
        : new UserId(value);

    /// <summary>Attempts to parse a user identifier. Success indicates well-formedness only.</summary>
    public static bool TryParse(string? candidate, out UserId userId)
    {
        userId = default;

        if (!Guid.TryParse(candidate, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        userId = new UserId(parsed);
        return true;
    }

    public override string ToString() => _value.ToString("D");
}

/// <summary>
/// Identifies an external identity provider, keeping the platform provider-agnostic.
/// </summary>
public readonly record struct IdentityProviderId
{
    /// <summary>The maximum supported length of a provider key.</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    private IdentityProviderId(string value) => _value = value;

    /// <summary>Microsoft Entra ID, the first provider targeted by the MVP.</summary>
    public static IdentityProviderId EntraId { get; } = new("entra-id");

    /// <summary>Local/demo provider used for synthetic and demonstration accounts.</summary>
    public static IdentityProviderId LocalDemo { get; } = new("local-demo");

    /// <summary>Gets a value indicating whether this identifier is unset.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Gets the provider key.</summary>
    /// <exception cref="InvalidOperationException">The identifier is unset.</exception>
    public string Value => _value
        ?? throw new InvalidOperationException("The identity provider id is unset.");

    /// <summary>Creates a provider identifier from a lowercase key such as "entra-id".</summary>
    /// <exception cref="ArgumentException">The key is empty, too long, or has invalid characters.</exception>
    public static IdentityProviderId From(string key)
    {
        if (!TryParse(key, out var providerId))
        {
            throw new ArgumentException(
                "An identity provider key must be 1-64 characters of lowercase letters, digits, '-', '_' or '.'.",
                nameof(key));
        }

        return providerId;
    }

    /// <summary>Attempts to parse a provider identifier.</summary>
    public static bool TryParse(string? candidate, out IdentityProviderId providerId)
    {
        providerId = default;

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            var allowed = (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
                || character is '-' or '_' or '.';

            if (!allowed)
            {
                return false;
            }
        }

        providerId = new IdentityProviderId(candidate);
        return true;
    }

    public override string ToString() => _value ?? string.Empty;
}
