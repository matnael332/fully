using System.Text.Json;
using System.Text.Json.Serialization;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The connector's secret storage: the only place the monday client secret, the OAuth tokens and the PKCE verifiers
/// are kept (decision D3B-2).
/// </summary>
/// <remarks>
/// <para>
/// The port is deliberately narrow: set, get and delete one value by a <see cref="MondaySecretName"/> the connector
/// builds from trusted ids. There is no list, search or enumeration, so nothing can query secrets across tenants, and an
/// implementation never needs to interpret a value. Values are opaque bytes; the connector binds each stored credential
/// to its tenant, connection and version inside the value and refuses one that does not match on read.
/// </para>
/// <para>
/// An implementation must be durable and must fail closed: a value is either returned exactly as set, reported
/// missing, or reported unavailable, never guessed. The deployed implementation (for example Azure Key Vault) is a
/// separate owner decision; Gate 3B tests with deterministic in-memory storage.
/// </para>
/// </remarks>
public interface IMondaySecretStore
{
    /// <summary>Creates or replaces a value.</summary>
    ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken);

    /// <summary>Reads a value.</summary>
    ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken);

    /// <summary>Deletes a value; deleting an absent value reports <see cref="MondaySecretStoreStatus.NotFound"/>.</summary>
    ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken);
}

/// <summary>The outcome of a secret-storage operation.</summary>
public enum MondaySecretStoreStatus
{
    /// <summary>Unspecified. Never a real outcome.</summary>
    Unspecified = 0,

    /// <summary>The operation succeeded.</summary>
    Succeeded = 1,

    /// <summary>No value exists under the name.</summary>
    NotFound = 2,

    /// <summary>The storage could not be reached or refused the operation; the outcome is unknown.</summary>
    Unavailable = 3,
}

/// <summary>
/// The name of one stored secret, built only by the connector from trusted ids.
/// </summary>
/// <remarks>
/// Names use lowercase letters, digits and dashes only and stay within 127 characters, so they are valid Azure Key Vault
/// secret names. A name identifies where a secret is kept; it is not itself secret. A credential's name carries its
/// tenant and connection, so no credential can be addressed through another tenant's ids.
/// </remarks>
public sealed class MondaySecretName : IEquatable<MondaySecretName>
{
    private MondaySecretName(string value) => Value = value;

    /// <summary>Gets the monday app's client secret, which the product owner places in storage.</summary>
    public static MondaySecretName ClientSecret { get; } = new("wce-monday-client-secret");

    /// <summary>Gets the name as storage keys it.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(MondaySecretName? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as MondaySecretName);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>The name; never a secret.</summary>
    public override string ToString() => Value;

    /// <summary>The name of one version of a connection's stored credential.</summary>
    internal static MondaySecretName ForTokens(TenantId tenant, Guid connectionId, Guid version) =>
        new($"wce-monday-tokens-{tenant.Value:N}-{connectionId:N}-{version:N}");

    /// <summary>The name of a started authorization's PKCE verifier.</summary>
    internal static MondaySecretName ForVerifier(TenantId tenant, Guid transactionId) =>
        new($"wce-monday-pkce-{tenant.Value:N}-{transactionId:N}");
}

/// <summary>The result of reading a secret: its value, or why there is none. It never renders or serializes the value.</summary>
[JsonConverter(typeof(MondaySecretLookupRefusingConverter))]
public sealed class MondaySecretLookup
{
    private readonly byte[] _value;

    private MondaySecretLookup(MondaySecretStoreStatus status, byte[] value)
    {
        Status = status;
        _value = value;
    }

    /// <summary>Gets the outcome.</summary>
    public MondaySecretStoreStatus Status { get; }

    /// <summary>A value was found.</summary>
    public static MondaySecretLookup Found(ReadOnlySpan<byte> value) => new(MondaySecretStoreStatus.Succeeded, value.ToArray());

    /// <summary>No value exists under the name.</summary>
    public static MondaySecretLookup Missing() => new(MondaySecretStoreStatus.NotFound, []);

    /// <summary>The storage could not be read.</summary>
    public static MondaySecretLookup Unavailable() => new(MondaySecretStoreStatus.Unavailable, []);

    /// <summary>Identifies the outcome; the value is never rendered.</summary>
    public override string ToString() => $"MondaySecretLookup({Status})";

    /// <summary>The value, for the connector only.</summary>
    internal ReadOnlySpan<byte> Value => _value;
}

/// <summary>Refuses to serialize or deserialize a secret lookup, so a secret can never be written by accident.</summary>
internal sealed class MondaySecretLookupRefusingConverter : JsonConverter<MondaySecretLookup>
{
    public override MondaySecretLookup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A secret lookup is never deserialized.");

    public override void Write(Utf8JsonWriter writer, MondaySecretLookup value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A secret lookup is never serialized.");
}
