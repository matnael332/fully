namespace WorkContinuity.Core.Identity;

/// <summary>
/// A person's identity at an external provider: the provider plus that provider's stable
/// subject/object identifier.
/// </summary>
/// <remarks>
/// The subject is the provider's immutable identifier (for example the Entra ID object id), not a
/// username and not an email address. Mapping an external identity to a canonical
/// <see cref="UserId"/> is a deterministic, explicitly provisioned decision; it is never inferred.
/// </remarks>
public sealed record ExternalIdentity
{
    /// <summary>The maximum supported length of a provider subject.</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>Creates an external identity.</summary>
    /// <exception cref="ArgumentException">The provider or subject is missing or malformed.</exception>
    public ExternalIdentity(IdentityProviderId provider, string subject)
    {
        if (provider.IsEmpty)
        {
            throw new ArgumentException(
                "An external identity requires an identity provider.",
                nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException(
                "An external identity requires a provider subject identifier.",
                nameof(subject));
        }

        if (subject.Length > MaxSubjectLength)
        {
            throw new ArgumentException(
                $"A provider subject identifier must be at most {MaxSubjectLength} characters.",
                nameof(subject));
        }

        foreach (var character in subject)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "A provider subject identifier must not contain control characters.",
                    nameof(subject));
            }
        }

        Provider = provider;
        Subject = subject;
    }

    /// <summary>Gets the external identity provider.</summary>
    public IdentityProviderId Provider { get; }

    /// <summary>Gets the provider's stable subject/object identifier.</summary>
    public string Subject { get; }
}
