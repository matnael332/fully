using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3E properties that must hold by construction: only the reconciler, from a bound authoritative read, ever declares a
/// value confirmed; a confirmed read carries no fabricated revision or occurrence and no time but the source's own; the
/// reconciler decides through the processing core and never touches the Snapshot itself; targets can hold no value; the
/// reader reads only its declared surface and logs nothing; the migration grants exactly read, insert and update; and no
/// host schedules or registers reconciliation.
/// </summary>
public sealed class ReconciliationSecurityTests
{
    private const string ReconcilerFile = "src/App.Infrastructure/Reconciliation/SourceReconciler.cs";
    private const string TargetsFile = "src/App.Infrastructure/Reconciliation/ReconciliationTargets.cs";
    private const string ProcessorFile = "src/App.Infrastructure/Processing/EventProcessor.cs";
    private const string ReaderFile = "src/App.Connectors.Monday/MondayStatusReader.cs";
    private const string NormalizerFile = "src/App.Connectors.Monday/MondayWebhookNormalizer.cs";
    private const string MigrationFile = "src/App.Infrastructure/Persistence/Migrations/20261001073251_ReconciliationTargets.cs";

    [Fact]
    public void Only_the_reconciler_declares_a_value_confirmed_and_only_from_a_bound_read()
    {
        // Where "Confirmed" is named at all: its definition, processing's comparison, and the reconciler's one construction.
        var naming = ProductionFiles()
            .Where(file => File.ReadAllText(file.FullName).Contains("SourceValueConfirmation.Confirmed", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["src/App.Core/Processing/SourceNormalization.cs", ProcessorFile, ReconcilerFile], naming);

        var reconciler = Read(ReconcilerFile);
        Assert.Equal(1, Regex.Count(reconciler, @"SourceValueConfirmation\.Confirmed"));
        Assert.Equal(1, Regex.Count(reconciler, @"new NormalizedSourceEvent\("));
        Assert.Contains("SourceValueConfirmation.Confirmed);", reconciler, StringComparison.Ordinal);

        // The construction comes after the read was bound to a state, the identity checked and the evidence received.
        Assert.True(reconciler.IndexOf("reading.State is not { } state", StringComparison.Ordinal) < reconciler.IndexOf("new NormalizedSourceEvent(", StringComparison.Ordinal));
        Assert.True(reconciler.IndexOf("ReconciliationOutcome.IdentityMismatch", StringComparison.Ordinal) < reconciler.IndexOf("new NormalizedSourceEvent(", StringComparison.Ordinal));

        // Only two production files build a normalized event at all: the webhook normalizer, unconfirmed, and the reconciler.
        var builders = ProductionFiles()
            .Where(file => File.ReadAllText(file.FullName).Contains("new NormalizedSourceEvent(", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal);
        Assert.Equal([NormalizerFile, ReconcilerFile], builders);
        Assert.DoesNotContain("SourceValueConfirmation.Confirmed", Read(NormalizerFile), StringComparison.Ordinal);
    }

    [Fact]
    public void A_confirmed_read_carries_no_fabricated_revision_or_occurrence_and_no_time_but_the_sources_own()
    {
        var reconciler = Read(ReconcilerFile);
        Assert.Contains("sourceRevision: null,", reconciler, StringComparison.Ordinal);
        Assert.Contains("occurredAt: null,", reconciler, StringComparison.Ordinal);
        Assert.Contains("sourceUpdatedAt: state.SourceUpdatedAt,", reconciler, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(reconciler, "sourceUpdatedAt:"));
        Assert.DoesNotContain("ReceivedAtUtc", reconciler, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", reconciler.Replace("DateTimeOffset", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("UtcNow", reconciler.Replace("_clock.UtcNow", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // The reader reports the status value's own time, parsed by exact formats; it keeps no clock and reads no item time.
        var reader = Read(ReaderFile);
        foreach (var forbidden in new[] { "IClock", "UtcNow", "DateTime.Now", "SourceRevision", "sourceRevision", "item.UpdatedAt", "CreatedAt" })
        {
            Assert.DoesNotContain(forbidden, reader, StringComparison.Ordinal);
        }

        Assert.Contains("DateTimeOffset.TryParseExact(", reader, StringComparison.Ordinal);
        var itemLevel = MondayQueries.ReadItemStatusesById[..MondayQueries.ReadItemStatusesById.IndexOf("column_values", StringComparison.Ordinal)];
        Assert.DoesNotContain("updated_at", itemLevel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reconciler_decides_through_the_processing_core_and_never_touches_the_snapshot_itself()
    {
        var reconciler = Read(ReconcilerFile);
        Assert.Contains("EventProcessor.DecideAndProjectAsync(", reconciler, StringComparison.Ordinal);
        foreach (var snapshot in new[] { "SnapshotProjection.ProjectAsync", "SnapshotFields", "SnapshotObjects", "snapshot_fields", "snapshot_objects", "FieldOrderingStates.Add", ".Advance(" })
        {
            Assert.DoesNotContain(snapshot, reconciler, StringComparison.Ordinal);
        }

        // Reconciliation's own decision never requests reconciliation again; only a delivery's hold does.
        var processor = Read(ProcessorFile);
        Assert.Contains("requestReconciliation: false", processor, StringComparison.Ordinal);
        Assert.Contains("requestReconciliation: true", processor, StringComparison.Ordinal);
        var requesters = ProductionFiles()
            .Where(file => File.ReadAllText(file.FullName).Contains("ReconciliationTargets.RequestAsync", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath);
        Assert.Equal([ProcessorFile], requesters);

        // Only the request and the reconciler write targets, through SQL that names the table.
        var writers = ProductionFiles()
            .Where(file => !RepositoryLayout.RelativePath(file).Contains("/Persistence/Migrations/", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file.FullName).Contains("public.reconciliation_targets", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal);
        Assert.Equal([TargetsFile, ReconcilerFile], writers);
    }

    [Fact]
    public void A_target_can_hold_no_value_label_response_credential_or_display_text()
    {
        var properties = typeof(ReconciliationTargetRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "Attempts", "ClaimId", "ClaimedGeneration", "ClaimedUntilUtc", "CreatedAtUtc", "DataType", "Field", "Generation",
                "LastAttemptedAtUtc", "LastOutcome", "NextAttemptAtUtc", "ObjectConnection", "ObjectId", "ObjectSystem", "ObjectType",
                "RequestKind", "RequestedAtUtc", "RequestedByEventId", "ResolvedAtUtc", "ResolvedByEventId", "ResolvedGeneration",
                "SourceScope", "State", "TenantId", "VerifiedAtUtc",
            ],
            properties);

        // The outcome column holds fixed codes only.
        Assert.All(ReconciliationOutcomes.All, outcome => Assert.Matches("^reconciliation\\.[a-z_.]+$", ReconciliationOutcomes.CodeOf(outcome)));
    }

    [Fact]
    public void The_reader_reads_through_the_read_client_only_and_keeps_no_state_clock_or_log()
    {
        var reader = Read(ReaderFile);
        foreach (var forbidden in new[]
                 {
                     "HttpClient", "IMondaySecretStore", "IConnectorAuthorizationStore", "IMondayCredentialSource", "MondayOAuthClient", "ILogger",
                     "Console.", "Environment.", "File.", "Random", "Guid.New", "mutation", "webhooks:write",
                 })
        {
            Assert.DoesNotContain(forbidden, reader, StringComparison.Ordinal);
        }

        var fields = typeof(MondayStatusReader).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal([typeof(MondayReadClient)], fields.Select(field => field.FieldType));
        Assert.Equal([typeof(IAuthoritativeSourceReader)], typeof(MondayStatusReader).GetInterfaces());

        // Its evidence has its own schema, distinct from every delivery schema.
        Assert.Equal("monday.api-read.v1", MondayStatusReader.EvidenceSchemaVersion);
        Assert.NotEqual(MondayWebhooks.SchemaVersion, MondayStatusReader.EvidenceSchemaVersion);
        Assert.False(new MondayWebhookNormalizer().Supports(MondayStatusReader.EvidenceSchemaVersion));
    }

    [Fact]
    public void The_reconcilers_logs_name_ids_codes_and_counts_only()
    {
        var reconciler = Read(ReconcilerFile);
        var logCalls = Regex.Matches(reconciler, @"_logger\.Log\w+\((?<arguments>.*?)\);", RegexOptions.Singleline).Select(match => match.Groups["arguments"].Value).ToList();
        Assert.Equal(5, logCalls.Count);
        foreach (var arguments in logCalls)
        {
            foreach (var content in new[] { "state", "Evidence", "Document", "reading", "read.", "Label", "exception,", ".Message" })
            {
                Assert.DoesNotContain(content, arguments, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_migration_forces_row_level_security_refuses_deletes_and_grants_exactly_read_insert_and_update()
    {
        var migration = Read(MigrationFile);
        Assert.Contains("ALTER TABLE {Table} ENABLE ROW LEVEL SECURITY;", migration, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE {Table} FORCE ROW LEVEL SECURITY;", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE POLICY no_delete_{Table} ON {Table} AS RESTRICTIVE FOR DELETE USING (false);", migration, StringComparison.Ordinal);
        Assert.Equal(["REVOKE ALL PRIVILEGES ON reconciliation_targets FROM workcontinuity_app;"], Lines(migration, "REVOKE "));
        Assert.Equal(["GRANT SELECT, INSERT, UPDATE ON reconciliation_targets TO workcontinuity_app;"], Lines(migration, "GRANT "));
        foreach (var forbidden in new[] { "SECURITY DEFINER", "BYPASSRLS", "CREATE FUNCTION", "CREATE ROLE", "PASSWORD", "DROP POLICY" })
        {
            Assert.DoesNotContain(forbidden, migration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_host_registers_or_schedules_reconciliation()
    {
        foreach (var host in new[] { "src/App.Web/Program.cs", "src/App.Worker/Program.cs" })
        {
            var program = Read(host);
            foreach (var registration in new[] { "AddSourceReconciliation", "ISourceReconciler", "MondayStatusReader", "IAuthoritativeSourceReader" })
            {
                Assert.DoesNotContain(registration, program, StringComparison.Ordinal);
            }
        }

        var reconciliation = ProductionFiles()
            .Where(file => RepositoryLayout.RelativePath(file).StartsWith("src/App.Infrastructure/Reconciliation/", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, reconciliation.Count);
        Assert.All(reconciliation, file =>
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var scheduling in new[] { "IHostedService", "BackgroundService", "PeriodicTimer", "Task.Delay", "Thread.Sleep", "AddHostedService" })
            {
                Assert.DoesNotContain(scheduling, text, StringComparison.Ordinal);
            }
        });
    }

    private static IEnumerable<FileInfo> ProductionFiles() => RepositoryLayout.ProductionSourceFiles;

    private static List<string> Lines(string text, string prefix) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
}
