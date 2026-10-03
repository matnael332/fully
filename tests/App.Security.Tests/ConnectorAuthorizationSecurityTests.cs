using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Connectors;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3B security properties that hold by construction: no public type of the connector exposes a token, a code, a
/// verifier or a secret; values that hold one never render or serialize; the scope policy admits no write; the HTTP
/// handler never follows a redirect; the persisted records and the Core contract hold metadata only; the store takes
/// its tenant from the trusted context alone; and the connector writes no log and uses no Azure SDK.
/// </summary>
public sealed class ConnectorAuthorizationSecurityTests
{
    private const string Sentinel = "SENTINEL-secret-7f3a";

    private static readonly string[] SecretFragments =
        ["token", "secret", "verifier", "password", "credential", "privatekey", "apikey", "bearer", "code", "state"];

    [Fact]
    public void No_public_member_of_the_connector_exposes_a_token_code_state_verifier_or_secret()
    {
        const BindingFlags visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type[] textual = [typeof(string), typeof(char[]), typeof(byte[]), typeof(ReadOnlyMemory<char>), typeof(ReadOnlyMemory<byte>),
            typeof(Memory<char>), typeof(Memory<byte>), typeof(ReadOnlySpan<byte>), typeof(ReadOnlySpan<char>)];

        var violations = typeof(MondayReadClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetProperties(visible).Select(property => (Type: type, property.Name, Kind: property.PropertyType))
                .Concat(type.GetFields(visible).Where(field => !field.IsLiteral).Select(field => (Type: type, field.Name, Kind: field.FieldType)))
                .Concat(type.GetMethods(visible).Where(method => !method.IsSpecialName).Select(method => (Type: type, method.Name, Kind: method.ReturnType))))
            .Where(member => textual.Contains(member.Kind))
            .Where(member => SecretFragments.Any(fragment => member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(member => $"{member.Type.Name}.{member.Name}")
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void The_public_shapes_of_the_lifecycle_types_are_pinned()
    {
        Assert.Equal(["ExpiresAt", "Identity"], PublicProperties<MondayAccessCredential>());
        Assert.Equal(["Status"], PublicProperties<MondaySecretLookup>());
        Assert.Empty(PublicProperties<MondayAuthorizationCallback>());
        Assert.Equal(["AuthorizationUri", "ConnectionId"], PublicProperties<MondayAuthorizationRedirect>());
        Assert.Equal(["ConnectionId", "ProviderRevocationConfirmed", "WasAlreadyDisconnected"], PublicProperties<MondayDisconnection>());
        Assert.Equal(
            ["ClientId", "HttpTimeout", "RedirectUri", "RefreshClaimTimeout", "RefreshMargin", "Scopes", "TransactionLifetime"],
            PublicProperties<MondayOAuthOptions>());

        // Secret-storage names are built only by the connector; an adapter receives them and cannot forge one.
        Assert.Empty(typeof(MondaySecretName).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(["ClientSecret", "SigningSecret", "Value"], PublicProperties<MondaySecretName>(BindingFlags.Static | BindingFlags.Instance));
    }

    [Fact]
    public void Values_that_hold_a_secret_never_render_or_serialize()
    {
        var lookup = MondaySecretLookup.Found(Encoding.UTF8.GetBytes(Sentinel));
        var callback = new MondayAuthorizationCallback(Sentinel, Sentinel, Sentinel, Sentinel);

        Assert.DoesNotContain(Sentinel, lookup.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, callback.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(lookup));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(callback));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new { Holder = callback }));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<MondayAuthorizationCallback>("{}"));
    }

    [Fact]
    public void The_scope_policy_admits_no_write_no_ai_and_nothing_unknown()
    {
        Assert.All(MondayScopePolicy.Allowed, scope =>
        {
            Assert.EndsWith(":read", scope, StringComparison.Ordinal);
            Assert.DoesNotContain("write", scope, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain("ai:consume", MondayScopePolicy.Allowed);

        foreach (var forbidden in new[] { "boards:write", "items:write", "webhooks:write", "notifications:write", "ai:consume", "admin" })
        {
            Assert.False(MondayScopePolicy.IsPermittedRequest("boards:read me:read " + forbidden), forbidden);
            Assert.False(MondayScopePolicy.IsPermittedGrant(string.Join(' ', new[] { "boards:read", "me:read", forbidden }.Order(StringComparer.Ordinal))), forbidden);
        }
    }

    [Fact]
    public void The_monday_http_handler_never_follows_a_redirect_or_keeps_cookies()
    {
        using var handler = MondayHttp.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(System.Net.DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void The_redirect_address_is_server_configuration_and_only_https()
    {
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions("client", new Uri("http://app.example.test/callback")));
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions("client", new Uri("https://user:pass@app.example.test/callback")));
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions("client", new Uri("https://app.example.test/callback#x")));

        // No lifecycle method takes a redirect address, scopes or a verifier from its caller.
        var parameters = typeof(MondayConnectionAuthorizer).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Distinct()
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["CancellationToken", "Guid", "MondayAuthorizationCallback", "Nullable`1", "Principal"], parameters);
    }

    [Fact]
    public void The_persisted_authorization_records_hold_only_the_metadata_decision_D3B_2_allows()
    {
        Assert.Equal(
            [
                "AccessExpiresAtUtc", "AuthorizedAtUtc", "CanonicalUserId", "ConnectionId", "Generation", "GrantedScopes", "MaterialVersion",
                "ProviderAccount", "ProviderRevocationConfirmed", "ProviderSubject", "ReauthorizationRequiredAtUtc", "RefreshClaimId",
                "RefreshClaimedAtUtc", "RefreshedAtUtc", "RevokedAtUtc", "State", "TenantId",
            ],
            PublicProperties<ConnectorAuthorizationRecord>());
        Assert.Equal(
            [
                "CanonicalUserId", "ConnectionId", "ConsumedAtUtc", "CreatedAtUtc", "ExpiresAtUtc", "Purpose", "RedirectHash",
                "RequestedScopes", "SourceSystem", "StateHash", "TenantId", "TransactionId", "VerifierDeletedAtUtc",
            ],
            PublicProperties<ConnectorAuthorizationTransactionRecord>());

        // The record of a verifier's deletion is an instant, never the verifier or its name.
        Assert.Equal(
            typeof(DateTimeOffset?),
            typeof(ConnectorAuthorizationTransactionRecord).GetProperty(nameof(ConnectorAuthorizationTransactionRecord.VerifierDeletedAtUtc))!.PropertyType);

        // The only binary columns are the two SHA-256 hashes, and the stored-credential reference is a Guid, which cannot hold a secret.
        Assert.Equal(
            ["RedirectHash", "StateHash"],
            typeof(ConnectorAuthorizationTransactionRecord).GetProperties().Where(property => property.PropertyType == typeof(byte[])).Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(typeof(Guid), typeof(ConnectorAuthorizationRecord).GetProperty(nameof(ConnectorAuthorizationRecord.MaterialVersion))!.PropertyType);
        Assert.Empty(typeof(ConnectorAuthorizationRecord).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Empty(typeof(ConnectorAuthorizationTransactionRecord).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void The_core_authorization_contract_carries_no_secret_and_no_tenant_parameter()
    {
        // The textual members of the Core authorization types are names, scopes and provider identifiers, nothing else.
        var textual = new[] { typeof(ConnectorAuthorizationTransaction), typeof(ConnectorAuthorizationGrant), typeof(ConnectorAuthorization) }
            .SelectMany(type => type.GetProperties().Where(property => property.PropertyType == typeof(string) || property.PropertyType == typeof(ReadOnlyMemory<byte>))
                .Select(property => $"{type.Name}.{property.Name}"))
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "ConnectorAuthorization.GrantedScopes", "ConnectorAuthorization.ProviderAccount", "ConnectorAuthorization.ProviderSubject",
                "ConnectorAuthorization.SourceSystem", "ConnectorAuthorizationGrant.GrantedScopes", "ConnectorAuthorizationGrant.ProviderAccount",
                "ConnectorAuthorizationGrant.ProviderSubject", "ConnectorAuthorizationGrant.SourceSystem", "ConnectorAuthorizationTransaction.RedirectHash",
                "ConnectorAuthorizationTransaction.RequestedScopes", "ConnectorAuthorizationTransaction.SourceSystem",
                "ConnectorAuthorizationTransaction.StateHash",
            ],
            textual);

        // Like the tenant-scoped database, the store offers no way to choose the tenant.
        var parameterTypes = typeof(IConnectorAuthorizationStore).GetMethods().SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType).ToList();
        Assert.DoesNotContain(typeof(TenantId), parameterTypes);
        Assert.DoesNotContain(typeof(ITenantContext), parameterTypes);
    }

    [Fact]
    public void The_3B_migration_revokes_every_privilege_then_grants_only_read_insert_and_update()
    {
        // Review item R3B-2: whatever default privileges gave the tables at creation, DELETE, TRUNCATE and everything else
        // is revoked first; only what the store uses is granted back. The fresh-database tests prove the effect.
        var migration = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src", "App.Infrastructure", "Persistence", "Migrations"))
            .EnumerateFiles("*_ConnectorAuthorizations.cs")
            .Single();
        var text = File.ReadAllText(migration.FullName);

        var grants = Regex.Matches(text, "\\bGRANT [A-Z, ]+ ON connector_authorization[a-z_, ]+").Select(match => match.Value.TrimEnd()).ToList();
        var revokes = Regex.Matches(text, "\\bREVOKE [A-Z, ]+ ON connector_authorization[a-z_, ]+").Select(match => match.Value.TrimEnd()).ToList();

        Assert.Equal(["GRANT SELECT, INSERT, UPDATE ON connector_authorizations, connector_authorization_transactions"], grants);
        Assert.Equal(["REVOKE ALL PRIVILEGES ON connector_authorizations, connector_authorization_transactions"], revokes);
        Assert.True(
            text.IndexOf(revokes[0], StringComparison.Ordinal) < text.IndexOf(grants[0], StringComparison.Ordinal),
            "The revocation must come before the grant, or it would take the granted privileges back too.");
    }

    [Fact]
    public void The_verifier_cleanup_migration_changes_no_privilege_and_runs_no_sql_of_its_own()
    {
        // Pre-3G maintenance: recording a verifier's deletion needs only the UPDATE that R3B-2 already grants, so the
        // migration grants, revokes and executes nothing, and the runtime role still cannot delete a transaction.
        var migration = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src", "App.Infrastructure", "Persistence", "Migrations"))
            .EnumerateFiles("*_ConnectorAuthorizationVerifierCleanup.cs")
            .Single();
        var text = File.ReadAllText(migration.FullName);

        Assert.DoesNotMatch("\\b(GRANT|REVOKE)\\b", text);
        Assert.DoesNotContain("migrationBuilder.Sql(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_reaches_the_database_only_through_the_tenant_scoped_entry_point()
    {
        var constructor = Assert.Single(typeof(ConnectorAuthorizationStore).GetConstructors());
        Assert.Equal(
            [typeof(ITenantScopedDatabase), typeof(ITenantContextAccessor)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void The_connector_writes_no_log_and_uses_no_azure_or_secret_store_sdk()
    {
        var references = typeof(MondayReadClient).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Azure", StringComparison.Ordinal) || name.StartsWith("Microsoft.Azure", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.Contains("KeyVault", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.StartsWith("WorkContinuity.Infrastructure", StringComparison.Ordinal));
    }

    private static string[] PublicProperties<T>(BindingFlags scope = BindingFlags.Instance) =>
        typeof(T).GetProperties(BindingFlags.Public | scope)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
