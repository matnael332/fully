namespace WorkContinuity.Core.Correlation;

/// <summary>
/// A correlation identifier used to trace one logical operation across the webhook, event,
/// broker, snapshot, AI, brief, action, agent and audit path.
/// </summary>
/// <remarks>
/// A correlation id frequently arrives from outside the platform, so it is treated as untrusted
/// data. <see cref="TryParse"/> enforces a strict length and character allowlist, which rejects
/// carriage returns, line feeds and other control characters. That keeps an inbound value from
/// being used to forge log entries or inject additional response headers.
/// </remarks>
public readonly record struct CorrelationId
{
    /// <summary>The minimum accepted length.</summary>
    public const int MinLength = 8;

    /// <summary>The maximum accepted length.</summary>
    public const int MaxLength = 128;

    private readonly string? _value;

    private CorrelationId(string value) => _value = value;

    /// <summary>Gets a value indicating whether this identifier is unset.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Gets the identifier text.</summary>
    /// <exception cref="InvalidOperationException">The identifier is unset.</exception>
    public string Value => _value
        ?? throw new InvalidOperationException("The correlation id is unset.");

    /// <summary>Generates a new correlation identifier.</summary>
    public static CorrelationId New() => new(Guid.CreateVersion7().ToString("N"));

    /// <summary>
    /// Attempts to accept an externally supplied correlation identifier, rejecting anything
    /// outside the safe allowlist.
    /// </summary>
    public static bool TryParse(string? candidate, out CorrelationId correlationId)
    {
        correlationId = default;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';

            if (!allowed)
            {
                return false;
            }
        }

        correlationId = new CorrelationId(candidate);
        return true;
    }

    public override string ToString() => _value ?? string.Empty;
}
