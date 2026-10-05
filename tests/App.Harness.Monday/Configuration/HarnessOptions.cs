using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Harness.Monday.Configuration;

/// <summary>Why the harness does not start. A closed set: a refusal never quotes a configured value.</summary>
public enum HarnessRefusal
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>The trusted tenant id is missing or malformed.</summary>
    TenantMissingOrMalformed = 1,

    /// <summary>The canonical user id is missing or malformed.</summary>
    CanonicalUserMissingOrMalformed = 2,

    /// <summary>The runtime connection string is missing, malformed or names no database.</summary>
    DatabaseMissingOrMalformed = 3,

    /// <summary>The monday client id is missing or malformed.</summary>
    ClientIdMissingOrMalformed = 4,

    /// <summary>The redirect address is missing or is not the operator callback on loopback over HTTPS.</summary>
    RedirectMissingOrNotLoopbackHttps = 5,

    /// <summary>The webhook base address is missing or is not an HTTPS address.</summary>
    WebhookBaseAddressMissingOrNotHttps = 6,

    /// <summary>The webhook listener's port is missing or malformed.</summary>
    WebhookPortMissingOrMalformed = 7,

    /// <summary>The transport is not the local Service Bus emulator.</summary>
    TransportNotTheLocalEmulator = 8,

    /// <summary>The database is not approved for a standalone run.</summary>
    StandaloneDatabaseNotApproved = 9,

    /// <summary>No certificate was supplied for the operator listener.</summary>
    OperatorCertificateUnavailable = 10,

    /// <summary>The database role is privileged or owns the schema.</summary>
    PrivilegedDatabaseRole = 11,

    /// <summary>The database could not be reached as the trusted tenant.</summary>
    DatabaseUnavailable = 12,

    /// <summary>A listener could not be started.</summary>
    ListenerUnavailable = 13,

    /// <summary>The composition failed while it started.</summary>
    StartFaulted = 14,
}

/// <summary>
/// The harness's server-side configuration, read once at this boundary (owner decisions D3G-2 and D3G-3).
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the harness where a tenant id or a canonical user id is parsed. Both come from the
/// harness's own configuration, which the owner supplies; they are never taken from a request, a console argument or
/// provider data. Under R3B-1 the harness is the owner-operated DEV/TEST mechanism that supplies the trusted Tenant →
/// canonical user → connection chain.
/// </para>
/// <para>
/// Everything is validated here and fails closed: the redirect address must be the operator callback on the loopback
/// address over HTTPS, the webhook base address must be HTTPS, and a transport, when one is configured, must be the
/// local Service Bus emulator. No value enables a real provider call, and none names a secret: the client id is an
/// identifier (decision D), and the client secret and the Signing Secret live only in secret storage.
/// </para>
/// </remarks>
public sealed class HarnessOptions
{
    /// <summary>The configuration key of the trusted tenant id.</summary>
    public const string TenantKey = "Harness:TenantId";

    /// <summary>The configuration key of the canonical user id.</summary>
    public const string CanonicalUserKey = "Harness:CanonicalUserId";

    /// <summary>The configuration key of the monday app's client id.</summary>
    public const string ClientIdKey = "Harness:OAuth:ClientId";

    /// <summary>The configuration key of the registered redirect address.</summary>
    public const string RedirectKey = "Harness:OAuth:RedirectUri";

    /// <summary>The configuration key of the webhook listener's loopback port; 0 lets the system choose one.</summary>
    public const string WebhookPortKey = "Harness:Webhook:ListenPort";

    /// <summary>The configuration key of the public address webhooks are registered under.</summary>
    public const string WebhookBaseAddressKey = "Connectors:Monday:WebhookBaseAddress";

    /// <summary>The configuration key of the runtime role's connection string.</summary>
    public const string DatabaseKey = "ConnectionStrings:Postgres";

    /// <summary>The configuration key of the local emulator's connection string.</summary>
    public const string TransportKey = "ServiceBus:ConnectionString";

    /// <summary>The configuration key of an Azure Service Bus namespace, which the harness refuses.</summary>
    public const string TransportNamespaceKey = "ServiceBus:FullyQualifiedNamespace";

    /// <summary>The only host the operator listener serves: the loopback address, never a name.</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>The path of the operator callback, which the redirect address must name.</summary>
    public const string CallbackPath = "/operator/callback";

    private HarnessOptions(
        TenantId tenant,
        UserId canonicalUser,
        string runtimeConnectionString,
        string databaseName,
        string clientId,
        Uri redirectUri,
        Uri webhookBaseAddress,
        int webhookPort,
        string? emulatorConnectionString)
    {
        Tenant = tenant;
        CanonicalUser = canonicalUser;
        RuntimeConnectionString = runtimeConnectionString;
        DatabaseName = databaseName;
        ClientId = clientId;
        RedirectUri = redirectUri;
        WebhookBaseAddress = webhookBaseAddress;
        WebhookPort = webhookPort;
        EmulatorConnectionString = emulatorConnectionString;
    }

    /// <summary>Gets the trusted tenant every operator action and cleanup acts for.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the canonical user who owns the delegation.</summary>
    public UserId CanonicalUser { get; }

    /// <summary>Gets the runtime role's connection string. Never rendered.</summary>
    public string RuntimeConnectionString { get; }

    /// <summary>Gets the name of the database the connection string names.</summary>
    public string DatabaseName { get; }

    /// <summary>Gets the monday app's client id.</summary>
    public string ClientId { get; }

    /// <summary>Gets the registered redirect address: the operator callback.</summary>
    public Uri RedirectUri { get; }

    /// <summary>Gets the port of the operator listener, which is the redirect address's.</summary>
    public int OperatorPort => RedirectUri.Port;

    /// <summary>Gets the exact Host value the operator listener answers for.</summary>
    public string OperatorAuthority => string.Create(CultureInfo.InvariantCulture, $"{LoopbackHost}:{OperatorPort}");

    /// <summary>Gets the operator listener's origin.</summary>
    public string OperatorOrigin => "https://" + OperatorAuthority;

    /// <summary>Gets the public address webhooks are registered under.</summary>
    public Uri WebhookBaseAddress { get; }

    /// <summary>Gets the webhook listener's loopback port; 0 lets the system choose one.</summary>
    public int WebhookPort { get; }

    /// <summary>Gets the local emulator's connection string, when a transport is configured. Never rendered.</summary>
    public string? EmulatorConnectionString { get; }

    /// <summary>Reads and validates the configuration.</summary>
    /// <param name="configuration">The harness's configuration.</param>
    /// <returns>The options, or the reason there are none.</returns>
    public static HarnessOptionsRead Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!TenantId.TryParse(configuration[TenantKey], out var tenant))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.TenantMissingOrMalformed);
        }

        if (!UserId.TryParse(configuration[CanonicalUserKey], out var canonicalUser))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.CanonicalUserMissingOrMalformed);
        }

        var connectionString = configuration[DatabaseKey];
        if (!TryValue(connectionString, "Database", out var databaseName)
            || !TryValue(connectionString, "Username", out _))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.DatabaseMissingOrMalformed);
        }

        var clientId = configuration[ClientIdKey];
        if (string.IsNullOrEmpty(clientId)
            || clientId.Length > MondayOAuthOptions.MaxClientIdLength
            || clientId.Any(character => character is < '!' or > '~'))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.ClientIdMissingOrMalformed);
        }

        if (!Uri.TryCreate(configuration[RedirectKey], UriKind.Absolute, out var redirect)
            || redirect.Scheme != Uri.UriSchemeHttps
            || !string.Equals(redirect.Host, LoopbackHost, StringComparison.Ordinal)
            || redirect.IsDefaultPort
            || redirect.Port is < 1 or > 65535
            || !string.Equals(redirect.AbsolutePath, CallbackPath, StringComparison.Ordinal)
            || redirect.Query.Length > 0
            || redirect.Fragment.Length > 0
            || redirect.UserInfo.Length > 0)
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.RedirectMissingOrNotLoopbackHttps);
        }

        if (!Uri.TryCreate(configuration[WebhookBaseAddressKey], UriKind.Absolute, out var webhookBase)
            || webhookBase.Scheme != Uri.UriSchemeHttps
            || webhookBase.UserInfo.Length > 0
            || webhookBase.Query.Length > 0
            || webhookBase.Fragment.Length > 0
            || string.IsNullOrEmpty(webhookBase.Host))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.WebhookBaseAddressMissingOrNotHttps);
        }

        if (!int.TryParse(configuration[WebhookPortKey], NumberStyles.None, CultureInfo.InvariantCulture, out var webhookPort)
            || webhookPort is < 0 or > 65535
            || (webhookPort != 0 && webhookPort == redirect.Port))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.WebhookPortMissingOrMalformed);
        }

        // Only the local emulator: a namespace would be Azure Service Bus, which this slice never reaches.
        var emulator = configuration[TransportKey];
        if (!string.IsNullOrWhiteSpace(configuration[TransportNamespaceKey])
            || (!string.IsNullOrWhiteSpace(emulator) && !IsLocalEmulator(emulator)))
        {
            return HarnessOptionsRead.Refused(HarnessRefusal.TransportNotTheLocalEmulator);
        }

        return HarnessOptionsRead.Accepted(new HarnessOptions(
            tenant,
            canonicalUser,
            connectionString!,
            databaseName,
            clientId,
            redirect,
            webhookBase,
            webhookPort,
            string.IsNullOrWhiteSpace(emulator) ? null : emulator));
    }

    /// <summary>Never renders a configured value.</summary>
    public override string ToString() => "HarnessOptions(redacted)";

    private static bool IsLocalEmulator(string connectionString) =>
        TryValue(connectionString, "UseDevelopmentEmulator", out var flag)
        && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
        && TryValue(connectionString, "Endpoint", out var endpoint)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var address)
        && address.IsLoopback;

    // One value of a connection string, by a case-insensitive key. False when the string or the value is missing or
    // the string cannot be read.
    private static bool TryValue(string? connectionString, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (builder.TryGetValue(key, out var found) && found is string text && text.Length > 0)
            {
                value = text;
                return true;
            }

            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>The outcome of reading the harness's configuration: the options, or the closed reason there are none.</summary>
public sealed class HarnessOptionsRead
{
    private HarnessOptionsRead(HarnessOptions? options, HarnessRefusal refusal)
    {
        Options = options;
        Refusal = refusal;
    }

    /// <summary>Gets the options, when the configuration is complete and valid.</summary>
    public HarnessOptions? Options { get; }

    /// <summary>Gets why there are no options.</summary>
    public HarnessRefusal Refusal { get; }

    internal static HarnessOptionsRead Accepted(HarnessOptions options) => new(options, HarnessRefusal.None);

    internal static HarnessOptionsRead Refused(HarnessRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Which databases a standalone harness run may use (owner decision D3G-4).
/// </summary>
/// <remarks>
/// A new persistent standalone harness database is not authorized in the offline slice, and the standalone harness is
/// never run against the shared suite database. The list is therefore empty: the standalone entry point refuses every
/// database until the owner approves one by name. The harness's tests do not pass this gate; they start the composition
/// in process over the existing fixtures, through their established lifecycle.
/// </remarks>
public static class HarnessStandaloneGate
{
    private static readonly string[] ApprovedDatabases = [];

    /// <summary>Whether a standalone run may use the options' database.</summary>
    public static bool Permits(HarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ApprovedDatabases.Contains(options.DatabaseName, StringComparer.Ordinal);
    }
}
