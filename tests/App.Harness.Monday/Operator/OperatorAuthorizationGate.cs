using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using WorkContinuity.Core.Time;
using WorkContinuity.Harness.Monday.Diagnostics;

namespace WorkContinuity.Harness.Monday.Operator;

/// <summary>What redeeming a start capability found.</summary>
/// <param name="Refusal">Why the capability was refused, or <see cref="OperatorRefusal.None"/>.</param>
/// <param name="ReauthorizeConnectionId">The connection the operator asked to restore, when the capability was minted for one.</param>
internal readonly record struct CapabilityRedemption(OperatorRefusal Refusal, Guid? ReauthorizeConnectionId);

/// <summary>
/// The operator-start capability and the browser binding of the one pending authorization (owner decision D3G-5).
/// </summary>
/// <remarks>
/// <para>
/// The harness acts as one fixed canonical user, so an authorization must be tied to the operator who asked for it and
/// to the browser that started it; the OAuth state alone ties it only to the transaction. Two values do that, each 32
/// bytes from the CSPRNG, each kept only as a SHA-256 and compared in constant time:
/// </para>
/// <list type="bullet">
/// <item>
/// The start capability is minted at the console and shown to the operator once. It is single use, valid for
/// <see cref="CapabilityLifetime"/>, and there is at most one: minting again replaces it. Without it the start route
/// opens nothing, so no page can start an authorization on the operator's behalf. A connection to restore is named when
/// the capability is minted, at the console, and is never read from a request.
/// </item>
/// <item>
/// The browser binding is set as a cookie when the start succeeds and is tied to that start's state. The callback
/// must present both. A callback without the binding, or with another start's, is refused before the authorizer sees
/// it, so it consumes nothing and the browser that started the authorization can still complete it.
/// </item>
/// </list>
/// <para>
/// The pending authorization is single use and bounded by <see cref="BindingLifetime"/>, the provider's code validity.
/// Nothing here is logged or exposed: the capability leaves once, as the console's answer, and the binding once, as a
/// cookie.
/// </para>
/// </remarks>
internal sealed class OperatorAuthorizationGate
{
    /// <summary>How long a minted start capability stays usable.</summary>
    internal static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(2);

    /// <summary>How long a started authorization's browser binding stays usable: the provider's code validity.</summary>
    internal static readonly TimeSpan BindingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The length of a capability or a binding: 32 bytes as unpadded base64url.</summary>
    internal const int ValueLength = 43;

    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private byte[]? _capabilityHash;
    private DateTimeOffset _capabilityExpiresAt;
    private bool _capabilityUsed;
    private Guid? _reauthorizeConnectionId;
    private byte[]? _stateHash;
    private byte[]? _bindingHash;
    private DateTimeOffset _bindingExpiresAt;

    internal OperatorAuthorizationGate(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Mints the start capability, replacing any earlier one and any pending authorization.
    /// </summary>
    /// <param name="reauthorizeConnectionId">The connection to restore, or null for a new connection.</param>
    /// <returns>The capability, to be shown to the operator once.</returns>
    internal string MintCapability(Guid? reauthorizeConnectionId)
    {
        var capability = NewValue();
        lock (_gate)
        {
            _capabilityHash = HashOf(capability);
            _capabilityExpiresAt = _clock.UtcNow + CapabilityLifetime;
            _capabilityUsed = false;
            _reauthorizeConnectionId = reauthorizeConnectionId;
            _stateHash = null;
            _bindingHash = null;
        }

        return capability;
    }

    /// <summary>Redeems a presented capability: at most once, and only before it expires.</summary>
    internal CapabilityRedemption Redeem(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return new CapabilityRedemption(OperatorRefusal.CapabilityMissing, null);
        }

        if (!IsWellFormed(presented))
        {
            return new CapabilityRedemption(OperatorRefusal.CapabilityMalformed, null);
        }

        var presentedHash = HashOf(presented);
        lock (_gate)
        {
            if (_capabilityHash is null || !CryptographicOperations.FixedTimeEquals(presentedHash, _capabilityHash))
            {
                return new CapabilityRedemption(OperatorRefusal.CapabilityUnknown, null);
            }

            if (_capabilityUsed)
            {
                return new CapabilityRedemption(OperatorRefusal.CapabilityUsed, null);
            }

            if (_clock.UtcNow >= _capabilityExpiresAt)
            {
                return new CapabilityRedemption(OperatorRefusal.CapabilityExpired, null);
            }

            _capabilityUsed = true;
            return new CapabilityRedemption(OperatorRefusal.None, _reauthorizeConnectionId);
        }
    }

    /// <summary>Binds the browser to the authorization a start just opened, and returns the binding for its cookie.</summary>
    /// <param name="state">The state of the authorization address the browser is being sent to.</param>
    internal string BindBrowser(string state)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);

        var binding = NewValue();
        lock (_gate)
        {
            _stateHash = HashOf(state);
            _bindingHash = HashOf(binding);
            _bindingExpiresAt = _clock.UtcNow + BindingLifetime;
        }

        return binding;
    }

    /// <summary>Forgets the pending authorization, after a start that opened none.</summary>
    internal void Abandon()
    {
        lock (_gate)
        {
            _stateHash = null;
            _bindingHash = null;
        }
    }

    /// <summary>
    /// Checks a callback's state and browser binding against the pending authorization, and uses it up when both match.
    /// </summary>
    /// <returns>Why the callback is refused, or <see cref="OperatorRefusal.None"/> when it may reach the authorizer.</returns>
    internal OperatorRefusal MatchCallback(string? state, string? binding)
    {
        if (string.IsNullOrEmpty(binding) || !IsWellFormed(binding))
        {
            return OperatorRefusal.BindingMissing;
        }

        if (string.IsNullOrEmpty(state))
        {
            return OperatorRefusal.BindingMismatch;
        }

        var stateHash = HashOf(state);
        var bindingHash = HashOf(binding);
        lock (_gate)
        {
            // Both comparisons always run, so neither value's correctness shows in the timing.
            var stateMatches = _stateHash is not null && CryptographicOperations.FixedTimeEquals(stateHash, _stateHash);
            var bindingMatches = _bindingHash is not null && CryptographicOperations.FixedTimeEquals(bindingHash, _bindingHash);
            if (!stateMatches || !bindingMatches || _clock.UtcNow >= _bindingExpiresAt)
            {
                return OperatorRefusal.BindingMismatch;
            }

            _stateHash = null;
            _bindingHash = null;
            return OperatorRefusal.None;
        }
    }

    /// <summary>Whether a value has the form this type mints: 43 base64url characters.</summary>
    internal static bool IsWellFormed(string value)
    {
        if (value.Length != ValueLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string NewValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static byte[] HashOf(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
