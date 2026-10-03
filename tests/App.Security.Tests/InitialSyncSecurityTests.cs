using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3F properties that must hold by construction: the ingress-position trigger's function has no definer rights, reads
/// nothing and only refuses a change; the migration forces row-level security, refuses deletes and grants exactly read,
/// insert and the column updates initial sync needs; the scope-set version migration only relaxes and adds, has the
/// database refuse a Live row on an earlier scope set, and re-asserts exactly those grants; no sync or scope record can hold
/// a value, a label, a cursor, a credential or a response; fences are ingress positions and versions are counters, never
/// times; held receipts complete their message and wait for the replay; the engine confirms only through the 3E path and
/// never touches the Snapshot itself; its logs carry ids, counts and codes only; the reader reads through the read client
/// only; and no host registers or runs initial sync.
/// </summary>
public sealed class InitialSyncSecurityTests
{
    private const string MigrationFile = "src/App.Infrastructure/Persistence/Migrations/20261002140325_InitialSync.cs";
    private const string ScopeVersionsMigrationFile = "src/App.Infrastructure/Persistence/Migrations/20261002193124_InitialSyncScopeVersions.cs";
    private const string ActiveScopesMigrationFile = "src/App.Infrastructure/Persistence/Migrations/20261002212331_InitialSyncActiveScopes.cs";
    private const string EngineFile = "src/App.Infrastructure/InitialSync/InitialSyncEngine.cs";
    private const string ScopesFile = "src/App.Infrastructure/InitialSync/ConnectorSourceScopes.cs";
    private const string GateFile = "src/App.Infrastructure/Processing/InitialSyncGate.cs";
    private const string ProcessorFile = "src/App.Infrastructure/Processing/EventProcessor.cs";
    private const string HandlerFile = "src/App.Infrastructure/Processing/EventProcessingRegistration.cs";
    private const string IngressFile = "src/App.Infrastructure/Ingress/EventIngressService.cs";
    private const string ReaderFile = "src/App.Connectors.Monday/MondayInitialSyncReader.cs";

    private static readonly string[] Tables = ["connector_source_scopes", "connection_syncs", "connection_sync_scopes"];

    [Fact]
    public void The_ingress_position_trigger_function_has_no_definer_rights_reads_nothing_and_only_refuses_a_change()
    {
        var migration = Read(MigrationFile);
        Assert.Contains("CREATE FUNCTION public.keep_ingress_sequence()", migration, StringComparison.Ordinal);
        Assert.Contains("SET search_path = pg_catalog, pg_temp", migration, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON FUNCTION public.keep_ingress_sequence() FROM PUBLIC;", migration, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OF ingress_sequence ON public.raw_receipts", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY DEFINER", migration, StringComparison.OrdinalIgnoreCase);

        // Its body compares the row's old and new position and raises; it reads, writes and calls nothing.
        var start = migration.IndexOf("AS $function$", StringComparison.Ordinal);
        var body = migration[start..migration.IndexOf("$function$;", start + 1, StringComparison.Ordinal)];
        Assert.Contains("IF NEW.ingress_sequence IS DISTINCT FROM OLD.ingress_sequence THEN", body, StringComparison.Ordinal);
        Assert.Contains("USING ERRCODE = 'check_violation', CONSTRAINT = 'keep_ingress_sequence_raw_receipts'", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b(SELECT|INSERT|UPDATE|DELETE|EXECUTE|PERFORM|COPY)\b", RegexOptions.IgnoreCase), body);
    }

    [Fact]
    public void The_migration_forces_row_level_security_refuses_deletes_and_grants_exactly_read_insert_and_column_updates()
    {
        var migration = Read(MigrationFile);
        Assert.Contains("private static readonly string[] NewTables = [\"connector_source_scopes\", \"connection_syncs\", \"connection_sync_scopes\"];", migration, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;", migration, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE {table} FORCE ROW LEVEL SECURITY;", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE POLICY no_delete_{table} ON {table} AS RESTRICTIVE FOR DELETE USING (false);", migration, StringComparison.Ordinal);
        Assert.Equal(Tables.Select(table => $"REVOKE ALL PRIVILEGES ON {table} FROM workcontinuity_app;"), Lines(migration, "REVOKE ALL PRIVILEGES"));
        Assert.Equal(
            [
                "GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;",
                "GRANT UPDATE (enabled, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;",
                "GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;",
                "GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, buffer_start_fence,",
                "GRANT SELECT, INSERT ON connection_sync_scopes TO workcontinuity_app;",
                "GRANT UPDATE (state, attempts, last_outcome, pages_read, objects_read, fields_applied, completed_at_utc)",
            ],
            Lines(migration, "GRANT "));

        // The replay floor and every identity column are never updatable by the runtime role.
        var grantStart = migration.IndexOf("GRANT UPDATE (generation", StringComparison.Ordinal);
        var syncGrant = migration[grantStart..migration.IndexOf("ON connection_syncs TO workcontinuity_app;", grantStart, StringComparison.Ordinal)];
        foreach (var immutable in new[] { "replay_floor", "tenant_id", "connection_id", "source_system" })
        {
            Assert.DoesNotContain(immutable, syncGrant, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[] { "SECURITY DEFINER", "BYPASSRLS", "CREATE ROLE", "PASSWORD", "DROP POLICY", "GRANT DELETE", "GRANT ALL" })
        {
            Assert.DoesNotContain(forbidden, migration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_current_work_view_reads_as_its_caller_is_read_only_to_the_runtime_role_and_rewrites_no_history()
    {
        var migration = Read(ActiveScopesMigrationFile);
        var up = migration[..migration.IndexOf("protected override void Down(", StringComparison.Ordinal)];

        // Both views run as their caller, so every table's row-level security applies to whoever reads them.
        Assert.Equal(2, Regex.Count(up, @"CREATE VIEW public\.active_snapshot_(fields|objects) WITH \(security_invoker = true\) AS"));
        Assert.Equal(2, Regex.Count(up, "CREATE VIEW"));

        // A participating connection's field is current only through an enabled scope with a baseline and no awaited one.
        Assert.Contains("OR (c.enabled AND c.baseline_generation IS NOT NULL AND t.awaiting_baseline_version IS NULL)", up, StringComparison.Ordinal);
        Assert.Contains("baseline_generation IS NULL OR (enabled AND baseline_generation >= 1)", up, StringComparison.Ordinal);

        // Exactly the scope grants again, now with the baseline; the views are read-only to the runtime role.
        Assert.Equal(
            [
                "REVOKE ALL PRIVILEGES ON active_snapshot_objects, active_snapshot_fields FROM PUBLIC;",
                "REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;",
                "REVOKE ALL PRIVILEGES ON active_snapshot_objects, active_snapshot_fields FROM workcontinuity_app;",
            ],
            Lines(up, "REVOKE "));
        Assert.Equal(
            [
                "GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;",
                "GRANT UPDATE (enabled, baseline_generation, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;",
                "GRANT SELECT ON active_snapshot_objects, active_snapshot_fields TO workcontinuity_app;",
            ],
            Lines(up, "GRANT "));

        // Going up, nothing is dropped, deleted, truncated or rewritten, and nothing is granted beyond reading.
        foreach (var forbidden in new[]
                 {
                     "SECURITY DEFINER", "BYPASSRLS", "CREATE ROLE", "PASSWORD", "DROP ", "Drop", "DELETE", "TRUNCATE", "UPDATE public.",
                     "UPDATE snapshot", "UPDATE reconciliation", "UPDATE connector", "INSERT INTO", "GRANT ALL", "security_invoker = false",
                 })
        {
            Assert.DoesNotContain(forbidden, up, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_scope_version_migration_only_relaxes_and_adds_and_re_asserts_exactly_the_sync_grants()
    {
        var migration = Read(ScopeVersionsMigrationFile);
        var up = migration[..migration.IndexOf("protected override void Down(", StringComparison.Ordinal)];

        // The database itself refuses a Live row on an earlier scope set, and a NotStarted row carrying a generation's data.
        Assert.Contains("(state <> 'Live' OR generation_scope_version = scope_version)", up, StringComparison.Ordinal);
        Assert.Contains("name: \"ck_connection_syncs_not_started\"", up, StringComparison.Ordinal);

        // Exactly the sync row's grants again, now with the two version columns, and never the replay floor or an identity.
        Assert.Equal(["REVOKE ALL PRIVILEGES ON connection_syncs FROM workcontinuity_app;"], Lines(migration, "REVOKE ALL PRIVILEGES"));
        Assert.Equal(
            [
                "GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;",
                "GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, scope_version, generation_scope_version,",
            ],
            Lines(migration, "GRANT "));
        var grantStart = migration.IndexOf("GRANT UPDATE (generation", StringComparison.Ordinal);
        var syncGrant = migration[grantStart..migration.IndexOf("ON connection_syncs TO workcontinuity_app;", grantStart, StringComparison.Ordinal)];
        foreach (var immutable in new[] { "replay_floor", "tenant_id", "connection_id", "source_system" })
        {
            Assert.DoesNotContain(immutable, syncGrant, StringComparison.Ordinal);
        }

        // Going up, nothing is dropped but two checks it re-adds, no row is removed, and nothing else is granted or bypassed.
        foreach (var forbidden in new[] { "SECURITY DEFINER", "BYPASSRLS", "CREATE ROLE", "PASSWORD", "DROP POLICY", "GRANT DELETE", "GRANT ALL", "DropColumn", "DropTable", "DELETE FROM", "TRUNCATE" })
        {
            Assert.DoesNotContain(forbidden, up, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_sync_or_scope_record_can_hold_no_value_label_cursor_credential_or_response()
    {
        Assert.Equal(
            ["Attempts", "AuthorizedAtUtc", "BufferStartFence", "ClaimId", "ClaimedUntilUtc", "ConnectionId", "CutoverFence", "Generation",
             "GenerationScopeVersion", "IsRunning", "LastOutcome", "LiveAtUtc", "NextAttemptAtUtc", "ProviderSubject", "ReplayFence", "ReplayFloor",
             "ScopeVersion", "SourceSystem", "StartedAtUtc", "State", "TenantId", "UpdatedAtUtc"],
            Properties(typeof(ConnectionSyncRecord)));
        Assert.Equal(
            ["Attempts", "CompletedAtUtc", "ConnectionId", "FieldsApplied", "Generation", "LastOutcome", "ObjectsRead", "PagesRead", "ResourceId", "State", "TenantId"],
            Properties(typeof(ConnectionSyncScopeRecord)));
        Assert.Equal(
            ["BaselineGeneration", "ConnectionId", "CreatedAtUtc", "Enabled", "ResourceId", "SourceSystem", "TenantId", "UpdatedAtUtc"],
            Properties(typeof(ConnectorSourceScopeRecord)));

        // Fences are ingress positions: numbers, never times.
        foreach (var fence in new[] { "ReplayFloor", "BufferStartFence", "ReplayFence", "CutoverFence" })
        {
            var type = typeof(ConnectionSyncRecord).GetProperty(fence)!.PropertyType;
            Assert.True(type == typeof(long) || type == typeof(long?), fence);
        }

        // Scope-set versions are counters, never times.
        foreach (var version in new[] { "ScopeVersion", "GenerationScopeVersion" })
        {
            var type = typeof(ConnectionSyncRecord).GetProperty(version)!.PropertyType;
            Assert.True(type == typeof(long) || type == typeof(long?), version);
        }

        // Outcome codes are fixed tokens; a continuation never renders.
        Assert.All(SyncScopeOutcomes.All, outcome => Assert.Matches("^initial_sync\\.[a-z_.]+$", SyncScopeOutcomes.CodeOf(outcome)));
        Assert.Equal("ScopeContinuation(redacted)", new ScopeContinuation("SENTINEL").ToString());
    }

    [Fact]
    public void Fences_are_ingress_positions_never_receipt_times_source_times_or_cursors()
    {
        var gate = Read(GateFile);
        Assert.Contains("SELECT COALESCE(MAX(ingress_sequence), 0) AS \"Value\"", gate, StringComparison.Ordinal);
        Assert.Contains("ingressSequence > sync.ReplayFloor", gate, StringComparison.Ordinal);

        foreach (var file in new[] { GateFile, EngineFile })
        {
            var text = Read(file);
            foreach (var time in new[] { "received_at", "ReceivedAt", "SourceUpdatedAt", "OccurredAt", "updated_at", "changedAt", "triggerTime", ".Token", "Cursor" })
            {
                Assert.DoesNotContain(time, text, StringComparison.Ordinal);
            }
        }

        // Every fence the engine records comes from the ingress position.
        var engine = Read(EngineFile);
        Assert.Equal(4, Regex.Count(engine, @"InitialSyncGate\.IngressFenceAsync\("));
        Assert.Contains("sync.BeginReplay(claimId, replayFence, now);", engine, StringComparison.Ordinal);
        Assert.Contains("sync!.GoLive(claimId, fence, now);", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingress_locks_the_connection_before_storing_and_a_held_receipt_completes_its_message_and_waits_for_the_replay()
    {
        var ingress = Read(IngressFile);
        var locked = ingress.IndexOf("await InitialSyncGate.LockForIngressAsync(dbContext, connection.TenantId, submission.ConnectionId, token);", StringComparison.Ordinal);
        Assert.True(locked > 0 && locked < ingress.IndexOf("dbContext.RawReceipts.Add(", StringComparison.Ordinal));
        Assert.Contains("FOR KEY SHARE", Read(GateFile), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Read(GateFile), "FROM public.connection_syncs\\s+WHERE tenant_id = \\{tenant\\.Value\\} AND connection_id = \\{connectionId\\}\\s+FOR SHARE"));

        // Processing takes the gate's share lock before the receipt's, decides nothing for a held receipt, and the handler
        // completes its message: no redelivery loop, no poison message, and the receipt stays durable for the replay.
        var processor = Read(ProcessorFile);
        Assert.Equal(1, Regex.Count(processor, @"InitialSyncGate\.HoldsAsync\("));
        Assert.True(processor.IndexOf("InitialSyncGate.HoldsAsync(", StringComparison.Ordinal) < processor.IndexOf("FROM public.raw_receipts WHERE receipt_id = {receiptId} FOR UPDATE", StringComparison.Ordinal));
        Assert.True(processor.IndexOf("if (held)", StringComparison.Ordinal) < processor.IndexOf("var result = await DecideAsync(", StringComparison.Ordinal));
        Assert.Contains("processed.IsSuccess || processed.Error == ProcessingErrors.HeldForInitialSync", Read(HandlerFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_confirms_only_through_the_3E_path_and_never_touches_the_snapshot_or_targets_itself()
    {
        var engine = Read(EngineFile);
        Assert.Equal(1, Regex.Count(engine, @"SourceReconciler\.ConfirmAsync\("));
        Assert.Contains("ReconciliationTargets.EstablishAsync(", engine, StringComparison.Ordinal);
        Assert.Contains("_processor.ReplayHeldAsync(", engine, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "SourceValueConfirmation", "new NormalizedSourceEvent(", "DecideAndProjectAsync", "SnapshotProjection", "SnapshotFields", "SnapshotObjects",
                     "snapshot_fields", "snapshot_objects", "FieldOrderingStates", "reconciliation_targets", "event_ledger", "RawReceipts.Add",
                 })
        {
            Assert.DoesNotContain(forbidden, engine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_engine_and_scope_configuration_log_ids_counts_fences_and_codes_only()
    {
        var engine = Read(EngineFile);
        var logCalls = Regex.Matches(engine, @"_logger\.Log\w+\((?<arguments>.*?)\);", RegexOptions.Singleline).Select(match => match.Groups["arguments"].Value).ToList();
        Assert.Equal(6, logCalls.Count);
        foreach (var arguments in logCalls)
        {
            foreach (var content in new[] { "reading", "synced", "State.", "Label", "Evidence", "Document", "Continuation", "Token", "page.", "validation.", "exception,", ".Message", "Subject" })
            {
                Assert.DoesNotContain(content, arguments, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("_logger", Read(ScopesFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_initial_sync_reader_reads_through_the_read_client_only_and_keeps_no_state_clock_or_log()
    {
        var reader = Read(ReaderFile);
        foreach (var forbidden in new[]
                 {
                     "HttpClient", "IMondaySecretStore", "IConnectorAuthorizationStore", "IMondayCredentialSource", "MondayOAuthClient", "ILogger", "IClock",
                     "UtcNow", "Console.", "Environment.", "File.", "Random", "Guid.New", "mutation", "webhooks:write", "SourceValueConfirmation",
                 })
        {
            Assert.DoesNotContain(forbidden, reader, StringComparison.Ordinal);
        }

        var fields = typeof(MondayInitialSyncReader).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal([typeof(MondayReadClient)], fields.Select(field => field.FieldType));
        Assert.Equal([typeof(IInitialSyncReader)], typeof(MondayInitialSyncReader).GetInterfaces());

        // It names no item, column or board text: only ids, states, types and the typed status fields.
        foreach (var query in new[] { MondayQueries.ReadSyncBoard, MondayQueries.ReadSyncStatusPage, MondayQueries.ReadNextSyncStatusPage })
        {
            Assert.True(MondayQueryGuard.IsSingleReadQuery(query));
            Assert.DoesNotMatch(new Regex(@"\b(name|title|text|value|description|email|updates|creator)\b"), query);
        }
    }

    [Fact]
    public void No_host_registers_schedules_or_runs_initial_sync()
    {
        foreach (var host in new[] { "src/App.Web/Program.cs", "src/App.Worker/Program.cs" })
        {
            var program = Read(host);
            foreach (var registration in new[] { "AddInitialSync", "IInitialSync", "MondayInitialSyncReader", "IConnectorSourceScopes" })
            {
                Assert.DoesNotContain(registration, program, StringComparison.Ordinal);
            }
        }

        var initialSync = RepositoryLayout.ProductionSourceFiles
            .Where(file => RepositoryLayout.RelativePath(file).StartsWith("src/App.Infrastructure/InitialSync/", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, initialSync.Count);
        Assert.All(initialSync.Append(new FileInfo(Path.Combine(RepositoryLayout.Root.FullName, GateFile))), file =>
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var scheduling in new[] { "IHostedService", "BackgroundService", "PeriodicTimer", "Task.Delay", "Thread.Sleep", "AddHostedService", "SpinWait" })
            {
                Assert.DoesNotContain(scheduling, text, StringComparison.Ordinal);
            }
        });
    }

    private static string[] Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    private static List<string> Lines(string text, string prefix) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
}
