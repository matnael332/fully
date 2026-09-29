using System.Reflection;
using System.Text.RegularExpressions;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Infrastructure.Retention;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2F retention and metrics properties that must hold by construction: the retention window is a fixed rule;
/// only the database's clock decides what is due and how late it is, and no application clock reaches retention;
/// one internal method alone calls the tenant-less retention function and gets tenant ids only; the purge reads no
/// content and writes nothing but the two content columns; nothing but an explicit Worker flag runs it; and every
/// metric tag comes from a bounded vocabulary that holds no id or value.
/// </summary>
public sealed partial class RetentionSecurityTests
{
    private const string PurgeFile = "src/App.Infrastructure/Retention/RawContentPurge.cs";
    private const string DiscoveryFile = "src/App.Infrastructure/Retention/RetentionTenantDiscovery.cs";
    private const string RegistrationFile = "src/App.Infrastructure/Retention/RawContentRetentionRegistration.cs";
    private const string CoreFile = "src/App.Core/Retention/RawContentRetention.cs";
    private const string IngressFile = "src/App.Infrastructure/Ingress/EventIngressService.cs";
    private const string ModelFile = "src/App.Infrastructure/Persistence/WorkContinuityModel.cs";
    private const string MetricsFile = "src/App.Infrastructure/Observability/EventBackboneMetrics.cs";

    [Fact]
    public void The_thirty_day_limit_and_the_twenty_eight_day_purge_age_are_fixed_rules_that_no_setting_can_change()
    {
        Assert.Equal(TimeSpan.FromDays(30), RawContentRetention.Window);
        Assert.Equal(TimeSpan.FromHours(48), RawContentRetention.PurgeLeadMargin);
        Assert.Equal(TimeSpan.FromDays(28), RawContentRetention.PurgeAge);
        Assert.Equal(TimeSpan.FromHours(678), RawContentRetention.OverdueAge);

        // Every retention age and every schedule assumption is read-only.
        var ages = typeof(RawContentRetention).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Concat(typeof(RawContentPurgeSchedule).GetProperties(BindingFlags.Public | BindingFlags.Static))
            .ToList();
        Assert.Equal(10, ages.Count);
        Assert.All(ages, property =>
        {
            Assert.Equal(typeof(TimeSpan), property.PropertyType);
            Assert.Null(property.SetMethod);
        });

        // The run's limits bound its work; none of them is a time, so none can purge early or keep content longer.
        var limits = typeof(RawContentPurgeOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(
            ["BatchSize", "MaxBatchesPerTenant", "TenantsPerRun"],
            limits.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(limits, property => Assert.Equal(typeof(int), property.PropertyType));
    }

    [Fact]
    public void Only_the_database_clock_decides_what_is_due_and_how_late_it_is_and_no_application_clock_reaches_retention()
    {
        // Core states the rule as ages alone: its only method classifies an age the database measured, and no helper
        // turns an application clock into a cutoff.
        Assert.Equal(
            ["StatusOf"],
            typeof(RawContentRetention).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name));
        Assert.DoesNotContain(
            typeof(RawContentPurgeRun).GetProperties(),
            property => property.PropertyType == typeof(DateTimeOffset) || property.PropertyType == typeof(DateTime));

        // No clock can reach the purge, its discovery or its entry point: nothing takes one and nothing holds one.
        var assembly = typeof(RawContentRetentionRegistration).Assembly;
        foreach (var type in new[]
                 {
                     assembly.GetType("WorkContinuity.Infrastructure.Retention.RawContentPurge", throwOnError: true)!,
                     assembly.GetType("WorkContinuity.Infrastructure.Retention.RetentionTenantDiscovery", throwOnError: true)!,
                 })
        {
            Assert.All(
                type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                constructor => Assert.DoesNotContain(constructor.GetParameters(), parameter => IsClock(parameter.ParameterType)));
            Assert.DoesNotContain(
                type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
                field => IsClock(field.FieldType));
        }

        Assert.DoesNotContain(
            typeof(RawContentPurgeEntryPoint).GetMethod(nameof(RawContentPurgeEntryPoint.RunOnceAsync))!.GetParameters(),
            parameter => IsClock(parameter.ParameterType));

        // Nor does the source read one: the Worker's clock only times the run, through a stopwatch, and no statement
        // takes a time.
        foreach (var file in new[] { PurgeFile, DiscoveryFile, RegistrationFile, CoreFile })
        {
            var source = Read(file);
            foreach (var forbidden in new[] { "IClock", "UtcNow", "TimeProvider", "DateTime.Now", "DateTimeOffset.Now", "@cutoff", "TimestampTz" })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }
        }

        Assert.Contains("var started = Stopwatch.GetTimestamp();", Read(PurgeFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_never_supplies_a_receipt_time_the_database_stamps_it()
    {
        // The receipt entity takes no time and sets none: only the database sets the retention age basis.
        var receive = typeof(RawReceiptRecord).GetMethod(nameof(RawReceiptRecord.Receive))!;
        Assert.DoesNotContain(receive.GetParameters(), parameter => IsTime(parameter.ParameterType));
        Assert.All(
            typeof(RawReceiptRecord).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.DoesNotContain(constructor.GetParameters(), parameter => IsTime(parameter.ParameterType)));
        Assert.Null(typeof(RawReceiptRecord).GetProperty(nameof(RawReceiptRecord.ReceivedAtUtc))!.GetSetMethod(nonPublic: false));

        // Ingress's clock dates only the outbox marker.
        var ingress = Read(IngressFile);
        Assert.Equal(1, Regex.Count(ingress, @"clock\.UtcNow"));
        Assert.Contains("var queuedAt = clock.UtcNow;", ingress, StringComparison.Ordinal);
        Assert.DoesNotContain("ReceivedAtUtc", ingress, StringComparison.Ordinal);

        // The model never writes it, refuses any change and reads the database's value back.
        var model = Read(ModelFile);
        Assert.Contains(".HasDefaultValueSql(\"now()\")", model, StringComparison.Ordinal);
        Assert.Contains("receivedAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);", model, StringComparison.Ordinal);
        Assert.Contains("receivedAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);", model, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tenant_less_retention_discovery_is_one_internal_method_taking_only_a_limit_and_returning_tenant_ids()
    {
        var discovery = typeof(RawContentRetentionRegistration).Assembly.GetType(
            "WorkContinuity.Infrastructure.Retention.RetentionTenantDiscovery",
            throwOnError: true)!;

        Assert.False(discovery.IsPublic);
        Assert.True(discovery.IsSealed);

        var methods = discovery
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToArray();
        var method = Assert.Single(methods);
        Assert.Equal("FindAsync", method.Name);
        Assert.Equal(
            [typeof(int), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(Task<IReadOnlyList<TenantId>>), method.ReturnType);

        // The purge and its discovery are internal; only their composition and limits are public.
        var purge = typeof(RawContentRetentionRegistration).Assembly.GetType(
            "WorkContinuity.Infrastructure.Retention.RawContentPurge",
            throwOnError: true)!;
        Assert.False(purge.IsPublic);
        Assert.Equal(
            ["RawContentPurgeEntryPoint", "RawContentPurgeOptions", "RawContentRetentionRegistration"],
            typeof(RawContentRetentionRegistration).Assembly.GetExportedTypes()
                .Where(type => type.Namespace == "WorkContinuity.Infrastructure.Retention")
                .Select(type => type.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_the_retention_discovery_calls_the_retention_function()
    {
        var callers = RepositoryLayout.ProductionSourceFiles
            .Where(file => !RepositoryLayout.RelativePath(file).Contains("/Persistence/Migrations/", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file.FullName).Contains("retention_expired_tenants", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();

        Assert.Equal([DiscoveryFile], callers);
        Assert.Contains(
            "\"SELECT tenant_id FROM public.retention_expired_tenants(@limit)\"",
            Read(DiscoveryFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_purge_reads_no_content_and_changes_nothing_but_the_two_content_columns_of_a_receipt()
    {
        var purge = Read(PurgeFile);

        // One write: both content columns cleared together, for content the database's clock shows due, oldest first,
        // skipping held rows; each cleared receipt's age, and the age of what is left, measured by that same clock.
        Assert.Equal("UPDATE public.raw_receipts AS receipt", Assert.Single(Regex.Matches(purge, @"UPDATE public\.[a-z_]+ AS [a-z]+")).Value);
        Assert.DoesNotContain("INSERT ", purge, StringComparison.Ordinal);
        Assert.Contains("SET payload = NULL, payload_fingerprint = NULL", purge, StringComparison.Ordinal);
        Assert.Contains("WHERE payload IS NOT NULL", purge, StringComparison.Ordinal);
        Assert.Contains("AND received_at_utc <= now() - interval '672 hours'", purge, StringComparison.Ordinal);
        Assert.DoesNotContain("720 hours", purge, StringComparison.Ordinal);
        Assert.DoesNotContain("@cutoff", purge, StringComparison.Ordinal);
        Assert.Contains("ORDER BY received_at_utc, receipt_id", purge, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE SKIP LOCKED", purge, StringComparison.Ordinal);
        Assert.Contains("RETURNING receipt.processing_state, now() - receipt.received_at_utc", purge, StringComparison.Ordinal);
        Assert.Contains(
            "\"SELECT now() - min(received_at_utc) FROM public.raw_receipts WHERE payload IS NOT NULL\"", purge, StringComparison.Ordinal);

        // Content is never selected, returned or logged, and no other table is touched.
        foreach (var forbidden in new[]
                 {
                     "payload::", "SELECT payload", "RETURNING receipt.payload", "SELECT *", ".Payload",
                     "outbox_messages", "event_ledger", "snapshot_", "field_ordering_states", "DELETE ",
                 })
        {
            Assert.DoesNotContain(forbidden, purge, StringComparison.Ordinal);
        }

        // Each tenant runs in its own trusted-service scope, never inside a caller's tenant.
        Assert.Contains("TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity)", purge, StringComparison.Ordinal);
        Assert.Contains("if (tenantContextAccessor.Current.IsResolved)", purge, StringComparison.Ordinal);
        Assert.DoesNotContain("outbox_pending_tenants", purge, StringComparison.Ordinal);
        Assert.DoesNotContain("resolve_ingress_connection", purge, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_but_the_explicit_worker_flag_runs_the_purge_and_nothing_deploys_or_schedules_it()
    {
        var runners = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("RawContentPurgeEntryPoint.RunOnceAsync", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal(["src/App.Worker/Program.cs"], runners);

        var worker = Read("src/App.Worker/Program.cs");
        Assert.Contains("if (args.Contains(\"--purge-raw-content\", StringComparer.OrdinalIgnoreCase))", worker, StringComparison.Ordinal);

        // The Worker's exit code is the tested rule, not a rule of its own.
        Assert.Contains("return RawContentPurgeEntryPoint.ExitCodeFor(run);", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("AddRawContentRetention", Read("src/App.Web/Program.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("RawContentPurge", Read("src/App.Web/Program.cs"), StringComparison.Ordinal);

        // No infrastructure, workflow or engineering script names the flag: there is no job, schedule or trigger.
        foreach (var folder in new[] { "infra", ".github", "eng" })
        {
            var directory = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, folder));
            if (!directory.Exists)
            {
                continue;
            }

            var mentions = directory.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => File.ReadAllText(file.FullName).Contains("purge-raw-content", StringComparison.OrdinalIgnoreCase))
                .Select(RepositoryLayout.RelativePath)
                .ToArray();
            Assert.Empty(mentions);
        }
    }

    [Fact]
    public void Every_metric_tag_key_is_declared_and_none_can_name_an_id_or_a_value()
    {
        var source = Read(MetricsFile);
        var keys = TagKeyLiteral().Matches(source)
            .Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(key, EventBackboneMetrics.TagKeys));
        Assert.Equal(EventBackboneMetrics.TagKeys.Order(StringComparer.Ordinal), keys);

        foreach (var forbidden in new[] { "tenant", "connection", "object", "event", "receipt", "message", "actor", "correlation", "id", "value", "user" })
        {
            Assert.DoesNotContain(EventBackboneMetrics.TagKeys, key => key.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // Nothing that carries an id is ever in reach of a tag.
        Assert.DoesNotContain("TenantId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReceiptId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EventId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CorrelationId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Metric_tag_values_come_only_from_bounded_vocabularies()
    {
        Assert.Equal("accepted_for_projection", EventBackboneMetrics.Name(EventProcessingOutcome.AcceptedForProjection));
        Assert.Equal("dead_letter", EventBackboneMetrics.Name(TransportSettlementKind.DeadLetter));
        Assert.Equal(EventBackboneMetrics.Other, EventBackboneMetrics.Name((EventProcessingOutcome)999));

        Assert.Equal(ProcessingErrors.ReceiptContentExpired.Code, EventBackboneMetrics.Code(ProcessingErrors.ReceiptContentExpired.Code));
        Assert.Equal(CanonicalEventErrors.InvalidValue.Code, EventBackboneMetrics.Code(CanonicalEventErrors.InvalidValue.Code));
        Assert.Equal(EventBackboneMetrics.None, EventBackboneMetrics.Code(null));

        // Anything outside the fixed catalogs, such as a handler's own code or text quoting an id, becomes "other".
        Assert.Equal(EventBackboneMetrics.Other, EventBackboneMetrics.Code("processing.receipt.missing"));
        Assert.Equal(EventBackboneMetrics.Other, EventBackboneMetrics.Code("tenant " + Guid.NewGuid().ToString("D")));
        Assert.Equal(EventBackboneMetrics.Other, EventBackboneMetrics.Code(string.Empty));
    }

    [Fact]
    public void Both_hosts_export_the_event_backbone_meter_through_their_existing_pipeline_only()
    {
        var web = Read("src/App.Web/Program.cs");
        var worker = Read("src/App.Worker/Program.cs");

        Assert.Contains(".WithMetrics(metrics => metrics.AddMeter(EventBackboneMetrics.MeterName))", web, StringComparison.Ordinal);
        Assert.Contains(".AddMeter(EventBackboneMetrics.MeterName)", worker, StringComparison.Ordinal);
        Assert.Contains(".AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = telemetryConnectionString)", worker, StringComparison.Ordinal);
        Assert.Equal("WorkContinuity.EventBackbone", EventBackboneMetrics.MeterName);

        // One meter name, created only by the metrics class.
        var meters = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("new Meter(", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal([MetricsFile], meters);
    }

    [GeneratedRegex("new(?:\\s+KeyValuePair<string, object\\?>)?\\(\"(?<key>[a-z_]+)\",")]
    private static partial Regex TagKeyLiteral();

    private static bool IsClock(Type type) => type == typeof(IClock) || type == typeof(TimeProvider);

    private static bool IsTime(Type type) =>
        type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?) || type == typeof(DateTime) || type == typeof(DateTime?);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));
}
