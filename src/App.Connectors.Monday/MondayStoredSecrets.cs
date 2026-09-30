using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// One version of a connection's credential as the connector keeps it in secret storage: the token pair, bound to its
/// tenant, connection, version and delegated monday identity.
/// </summary>
/// <remarks>
/// The binding travels inside the stored value, so a value moved or copied under another name, including another
/// tenant's, is refused on read instead of being used. The type never renders its tokens and exists only inside the
/// connector.
/// </remarks>
internal sealed class MondayTokenMaterial
{
    private const string Format = "wce-monday-tokens-v1";

    internal MondayTokenMaterial(
        TenantId tenant,
        Guid connectionId,
        Guid version,
        string subject,
        string account,
        MondayIssuedTokens tokens)
    {
        Tenant = tenant;
        ConnectionId = connectionId;
        Version = version;
        Subject = subject;
        Account = account;
        Tokens = tokens;
    }

    internal TenantId Tenant { get; }

    internal Guid ConnectionId { get; }

    internal Guid Version { get; }

    internal string Subject { get; }

    internal string Account { get; }

    internal MondayIssuedTokens Tokens { get; }

    /// <summary>Identifies the material without its tokens.</summary>
    public override string ToString() => $"MondayTokenMaterial(connection {ConnectionId:D}, version {Version:D}, tokens redacted)";

    /// <summary>Reads a stored value, refusing it unless it is well formed and bound to exactly this tenant, connection and version.</summary>
    internal static MondayTokenMaterial? Read(ReadOnlySpan<byte> value, TenantId tenant, Guid connectionId, Guid version)
    {
        using var document = StoredJson.Parse(value, expectedProperties: 10);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        if (StoredJson.Text(root, "format") != Format
            || StoredJson.Id(root, "tenant") != tenant.Value
            || StoredJson.Id(root, "connection") != connectionId
            || StoredJson.Id(root, "version") != version
            || StoredJson.Text(root, "subject") is not { } subject || !IsProviderIdentifier(subject)
            || StoredJson.Text(root, "account") is not { } account || !IsProviderIdentifier(account)
            || StoredJson.Text(root, "access") is not { } access || !MondayIssuedTokens.IsToken(access)
            || StoredJson.Text(root, "refresh") is not { } refresh || !MondayIssuedTokens.IsToken(refresh)
            || StoredJson.Text(root, "scopes") is not { } scopes || !ConnectorScopes.IsWellFormed(scopes)
            || !DateTimeOffset.TryParseExact(
                StoredJson.Text(root, "expires"),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var expires))
        {
            return null;
        }

        return new MondayTokenMaterial(tenant, connectionId, version, subject, account, new MondayIssuedTokens(access, refresh, expires, scopes));
    }

    /// <summary>The value to store.</summary>
    internal byte[] ToBytes() => StoredJson.Write(writer =>
    {
        writer.WriteString("format", Format);
        writer.WriteString("tenant", Tenant.Value.ToString("D"));
        writer.WriteString("connection", ConnectionId.ToString("D"));
        writer.WriteString("version", Version.ToString("D"));
        writer.WriteString("subject", Subject);
        writer.WriteString("account", Account);
        writer.WriteString("access", Tokens.AccessToken);
        writer.WriteString("refresh", Tokens.RefreshToken);
        writer.WriteString("expires", Tokens.AccessExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("scopes", Tokens.GrantedScopes);
    });

    private static bool IsProviderIdentifier(string value) =>
        EventText.IsIdentifier(value) && !value.Contains('@', StringComparison.Ordinal);
}

/// <summary>A started authorization's PKCE verifier as secret storage keeps it, bound to its tenant and transaction.</summary>
internal sealed class MondayVerifierMaterial
{
    private const string Format = "wce-monday-pkce-v1";

    internal MondayVerifierMaterial(TenantId tenant, Guid transactionId, string verifier)
    {
        Tenant = tenant;
        TransactionId = transactionId;
        Verifier = verifier;
    }

    internal TenantId Tenant { get; }

    internal Guid TransactionId { get; }

    internal string Verifier { get; }

    /// <summary>Identifies the material without its verifier.</summary>
    public override string ToString() => $"MondayVerifierMaterial(transaction {TransactionId:D}, verifier redacted)";

    /// <summary>Reads a stored value, refusing it unless it is well formed and bound to exactly this tenant and transaction.</summary>
    internal static MondayVerifierMaterial? Read(ReadOnlySpan<byte> value, TenantId tenant, Guid transactionId)
    {
        using var document = StoredJson.Parse(value, expectedProperties: 4);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        return StoredJson.Text(root, "format") == Format
            && StoredJson.Id(root, "tenant") == tenant.Value
            && StoredJson.Id(root, "transaction") == transactionId
            && StoredJson.Text(root, "verifier") is { } verifier
            && MondayPkce.IsVerifier(verifier)
                ? new MondayVerifierMaterial(tenant, transactionId, verifier)
                : null;
    }

    /// <summary>The value to store.</summary>
    internal byte[] ToBytes() => StoredJson.Write(writer =>
    {
        writer.WriteString("format", Format);
        writer.WriteString("tenant", Tenant.Value.ToString("D"));
        writer.WriteString("transaction", TransactionId.ToString("D"));
        writer.WriteString("verifier", Verifier);
    });
}

/// <summary>The monday app's client secret, read from secret storage for one token call.</summary>
internal static class MondayClientSecret
{
    /// <summary>The longest accepted client secret.</summary>
    internal const int MaxLength = 1024;

    /// <summary>The client secret, or null unless the stored value is 1–1024 visible ASCII characters.</summary>
    internal static string? Read(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > MaxLength)
        {
            return null;
        }

        foreach (var octet in value)
        {
            if (octet is < (byte)'!' or > (byte)'~')
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(value);
    }
}

/// <summary>Strict JSON for stored values: one flat object of string properties, nothing more.</summary>
internal static class StoredJson
{
    internal static JsonDocument? Parse(ReadOnlySpan<byte> value, int expectedProperties)
    {
        if (value.IsEmpty || value.Length > 64 * 1024)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
        }
        catch (JsonException)
        {
            return null;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != expectedProperties
            || root.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    internal static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static Guid Id(JsonElement root, string name) =>
        Guid.TryParseExact(Text(root, name), "D", out var id) ? id : Guid.Empty;

    internal static byte[] Write(Action<Utf8JsonWriter> properties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            properties(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
