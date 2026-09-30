using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3C webhook intake properties that must hold by construction: no public surface carries a token, a body or a
/// secret; the Signing Secret is reached only through the secret-storage port; the connector reads only the claims it
/// needs and never the token's user or short-lived credential; the endpoint logs identifiers, codes and statuses only;
/// and the challenge path never touches a token or ingress.
/// </summary>
public sealed class MondayWebhookIntakeSecurityTests
{
    private const string TokenFile = "src/App.Connectors.Monday/MondayWebhookToken.cs";
    private const string EndpointFile = "src/App.Web/Connectors/MondayWebhookEndpoint.cs";
    private const string RegistrationFile = "src/App.Web/Connectors/MondayWebhookRegistration.cs";

    private static readonly string[] ConnectorWebhookFiles =
    [
        "src/App.Connectors.Monday/MondayWebhookAuthenticator.cs",
        "src/App.Connectors.Monday/MondayWebhookBody.cs",
        "src/App.Connectors.Monday/MondayWebhookOptions.cs",
        TokenFile,
        "src/App.Connectors.Monday/MondayWebhookVerifier.cs",
        "src/App.Connectors.Monday/MondayWebhooks.cs",
    ];

    [Fact]
    public void No_public_member_of_the_webhook_types_carries_a_token_a_body_or_a_secret()
    {
        Assert.Equal(["ConnectionId", "IsVerified", "Status"], PublicProperties<MondayWebhookAuthentication>());
        Assert.Equal(["Kind", "SourceEventId"], PublicProperties<MondayWebhookBody>());
        Assert.Equal(["PublicBaseAddress"], PublicProperties<MondayWebhookOptions>());
        Assert.Empty(PublicProperties<MondayWebhookAuthenticator>());
        Assert.Equal(["SourceSystem"], PublicProperties<MondayWebhookVerifier>());

        // The verified token exists only for the connector, to become the ingress proof.
        var token = typeof(MondayWebhookAuthentication).GetProperty("Token", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(token);
        Assert.False(token.GetMethod!.IsPublic);
        Assert.Empty(typeof(MondayWebhookAuthentication).GetConstructors());
        Assert.Empty(typeof(MondayWebhookBody).GetConstructors());
    }

    [Fact]
    public void The_signing_secret_is_named_once_and_read_only_through_the_secret_storage_port()
    {
        Assert.Equal("wce-monday-signing-secret", MondaySecretName.SigningSecret.Value);

        var holders = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("SigningSecret", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["src/App.Connectors.Monday/IMondaySecretStore.cs", "src/App.Connectors.Monday/MondayWebhookAuthenticator.cs"], holders);

        // Configuration carries only the public base address; a secret never comes from configuration or a request.
        var registration = Read(RegistrationFile);
        Assert.Equal(1, Regex.Count(registration, @"configuration\["));
        Assert.Contains("configuration[BaseAddressKey]", registration, StringComparison.Ordinal);
        var configurationFiles = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src"))
            .EnumerateFiles("appsettings*.json", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)
                && !file.FullName.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(configurationFiles);
        Assert.All(configurationFiles, file => Assert.DoesNotContain("signing", File.ReadAllText(file.FullName), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_connector_reads_only_the_claims_it_needs_and_never_the_user_or_the_short_lived_credential()
    {
        var token = Read(TokenFile);
        var read = Regex.Matches(token, "TryGetProperty\\(\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Concat(Regex.Matches(token, "TryReadTime\\(claims, \"([^\"]+)\"").Select(match => match.Groups[1].Value))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["accountId", "alg", "aud", "crit", "exp", "iat", "nbf", "typ"], read);

        foreach (var file in ConnectorWebhookFiles)
        {
            var text = Read(file);
            foreach (var forbidden in new[] { "\"userId\"", "\"shortLivedToken\"", "\"user_id\"", "\"short_lived_token\"" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_endpoint_logs_identifiers_codes_and_statuses_only()
    {
        var endpoint = Read(EndpointFile);

        var placeholders = Regex.Matches(endpoint, @"\{([A-Z][A-Za-z]*)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["ConnectionId", "CorrelationId", "ErrorCode", "IngressStatus", "ReceiptId", "StatusCode"], placeholders);

        // No interpolated text anywhere in the endpoint, so no value can be spliced into a log line or an error.
        Assert.DoesNotContain("$\"", endpoint, StringComparison.Ordinal);

        // The Authorization header is read once, and only handed to the authenticator.
        Assert.Equal(1, Regex.Count(endpoint, @"Headers\.Authorization"));
        Assert.Contains(".AuthenticateAsync(connectionId, authorization.Count == 1 ? authorization[0] : null, cancellationToken)", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_challenge_path_never_reads_a_token_and_never_reaches_ingress()
    {
        var endpoint = Read(EndpointFile);
        var start = endpoint.IndexOf("private static async Task AnswerChallengeAsync(", StringComparison.Ordinal);
        var end = endpoint.IndexOf("private static async Task ReceiveEventAsync(", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);

        var challenge = endpoint[start..end];
        foreach (var forbidden in new[] { "Authorization", "IEventIngress", "MondayWebhookAuthenticator", "SubmitAsync", "ToSubmission" })
        {
            Assert.DoesNotContain(forbidden, challenge, StringComparison.Ordinal);
        }

        Assert.Contains("GetRequiredService<IIngressConnectionCheck>()", challenge, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_is_submitted_only_after_its_token_is_verified_and_answered_success_only_when_acknowledged()
    {
        var endpoint = Read(EndpointFile);

        var authenticate = endpoint.IndexOf(".AuthenticateAsync(", StringComparison.Ordinal);
        var refuseUnverified = endpoint.IndexOf("if (!authentication.IsVerified)", StringComparison.Ordinal);
        var submit = endpoint.IndexOf(".SubmitAsync(delivery.ToSubmission(authentication, correlationId)", StringComparison.Ordinal);
        var acknowledged = endpoint.IndexOf("if (outcome.IsAcknowledged)", StringComparison.Ordinal);
        Assert.True(authenticate > 0 && authenticate < refuseUnverified && refuseUnverified < submit && submit < acknowledged);

        // The one success answer for an event follows the acknowledged outcome.
        Assert.Equal(2, Regex.Count(endpoint, @"StatusCodes\.Status200OK"));
        Assert.True(endpoint.LastIndexOf("StatusCodes.Status200OK", StringComparison.Ordinal) > acknowledged);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));

    private static string[] PublicProperties<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
