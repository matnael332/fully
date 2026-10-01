using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Processing;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3D monday normalization properties that must hold by construction: every event it produces declares its values
/// unconfirmed; it reads only the members the support matrix names, never display text as identity; it never resolves
/// an actor or invents a revision; it calls nothing, reads no credential, keeps no clock and does not log; and its
/// refusals are fixed text.
/// </summary>
public sealed class MondayNormalizationSecurityTests
{
    private const string NormalizerFile = "src/App.Connectors.Monday/MondayWebhookNormalizer.cs";

    [Fact]
    public void Every_normalized_monday_event_declares_its_values_unconfirmed()
    {
        var normalizer = Read(NormalizerFile);

        // No other value is ever named, and the one event it builds is built with it.
        var declarations = Regex.Matches(normalizer, @"SourceValueConfirmation\.(\w+)").Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.Equal(["Unconfirmed"], declarations);
        Assert.Equal(1, Regex.Count(normalizer, @"new NormalizedSourceEvent\("));
        Assert.Contains("            SourceValueConfirmation.Unconfirmed));", normalizer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_normalizer_reads_only_the_members_the_support_matrix_names()
    {
        var normalizer = Read(NormalizerFile);

        var members = Regex.Matches(normalizer, "TryGetProperty\\(\"([^\"]+)\"").Select(match => match.Groups[1].Value)
            .Concat(Regex.Matches(normalizer, "ProviderId\\(delivery, \"([^\"]+)\"").Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "boardId", "challenge", "changedAt", "columnId", "columnType", "event", "itemId", "label", "parentItemBoardId",
                "parentItemId", "previousValue", "pulseId", "text", "triggerTime", "triggerUuid", "type", "userId", "value",
            ],
            members);

        // Display text and causation are never read, so they can decide nothing.
        foreach (var never in new[] { "\"pulseName\"", "\"columnTitle\"", "\"groupId\"", "\"groupName\"", "\"originalTriggerUuid\"", "\"index\"", "\"style\"", "\"body\"", "\"textBody\"" })
        {
            Assert.DoesNotContain(never, normalizer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_normalizer_never_resolves_an_actor_or_invents_a_revision()
    {
        var normalizer = Read(NormalizerFile);

        Assert.DoesNotContain("EventActor.Resolved", normalizer, StringComparison.Ordinal);
        Assert.Contains("EventActor.Unresolved(userId)", normalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("UserId", normalizer.Replace("userId", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("sourceRevision: null,", normalizer, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(normalizer, "sourceRevision"));
    }

    [Fact]
    public void The_normalizer_calls_nothing_reads_no_credential_keeps_no_clock_and_does_not_log()
    {
        var normalizer = Read(NormalizerFile);

        foreach (var forbidden in new[]
                 {
                     "HttpClient", "IMondaySecretStore", "MondaySecretName", "IConnectorAuthorizationStore", "MondayWebhookAuthenticator",
                     "MondayReadClient", "IMondayCredentialSource", "Proof", "Authorization", "ILogger", "Console.", "async ", "Task<", "Task.Run",
                     "IClock", "UtcNow", "DateTime.Now", "Random", "Guid.New", "Environment.", "File.",
                 })
        {
            Assert.DoesNotContain(forbidden, normalizer, StringComparison.Ordinal);
        }

        // Its state is fixed configuration only: nothing an instance could accumulate or leak between receipts.
        Assert.All(
            typeof(MondayWebhookNormalizer).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => Assert.Fail("An instance field would let one receipt affect another: " + field.Name));
        Assert.True(typeof(MondayWebhookNormalizer).IsSealed);
        Assert.Equal([typeof(ISourceEventNormalizer)], typeof(MondayWebhookNormalizer).GetInterfaces());
    }

    [Fact]
    public void Refusals_are_four_fixed_content_free_errors()
    {
        var errors = typeof(MondayWebhookNormalizationErrors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Malformed", "UnsupportedColumn", "UnsupportedEvent", "UnsupportedValue"], errors);

        Assert.Equal(
            ["monday.webhook.malformed", "monday.webhook.unsupported_column", "monday.webhook.unsupported_event", "monday.webhook.unsupported_value"],
            new[]
            {
                MondayWebhookNormalizationErrors.Malformed, MondayWebhookNormalizationErrors.UnsupportedColumn,
                MondayWebhookNormalizationErrors.UnsupportedEvent, MondayWebhookNormalizationErrors.UnsupportedValue,
            }.Select(error => error.Code));

        // No refusal is built from the body: the file holds no interpolated or concatenated error text.
        var normalizer = Read(NormalizerFile);
        Assert.DoesNotContain("$\"", normalizer, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Count(normalizer, @"Error\.Validation\("));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));
}
