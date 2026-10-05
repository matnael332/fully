using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Diagnostics;

namespace WorkContinuity.Harness.Monday.Hosting;

/// <summary>One log record as the harness's sink receives it: nothing else of a log call survives.</summary>
/// <param name="Category">The logger's category.</param>
/// <param name="Level">The level.</param>
/// <param name="EventId">The numeric event id.</param>
/// <param name="Message">The rendered message of an allowed category.</param>
/// <param name="ExceptionType">The type name of the exception logged with it, never its text.</param>
public sealed record HarnessLogRecord(string Category, LogLevel Level, int EventId, string Message, string? ExceptionType);

/// <summary>Where the harness's log records go.</summary>
public interface IHarnessLogSink
{
    /// <summary>Receives one record.</summary>
    void Write(HarnessLogRecord record);
}

/// <summary>
/// Which log records may reach the harness's sink (owner decision D3G-6): by category first, then by whose the record is.
/// </summary>
/// <remarks>
/// <para>
/// Category. Only the product's and the harness's own categories pass. Each of those is written to log ids, fixed codes
/// and counts only, and its sub-gate's tests hold it to that. Every framework category is silenced whatever its level:
/// the host's request log would write a callback's query string, the routing and server logs name request targets,
/// and the persistence and messaging libraries log statements and addresses. Silencing is by category, not by level,
/// so no level setting can turn them back on.
/// </para>
/// <para>
/// Owner. The harness works for one configured tenant, but the product's relay and intake, which it runs unchanged,
/// work for every tenant the database holds and log each tenant's identifiers as they go. A record of a product
/// category is therefore shown only when it is the configured tenant's: the flow that logged it runs as that tenant, or
/// the record names that tenant, and in neither case does the record name another. A record that names another tenant,
/// a record logged by a flow that runs as another tenant, and a product record that no tenant can be told for are
/// withheld and only counted. The harness's own categories log closed values and are shown, unless a record of theirs
/// names another tenant.
/// </para>
/// </remarks>
public static class HarnessLogPolicy
{
    /// <summary>The prefix every allowed category starts with.</summary>
    public const string AllowedPrefix = "WorkContinuity.";

    /// <summary>The prefix of the harness's own categories.</summary>
    public const string HarnessPrefix = "WorkContinuity.Harness.";

    /// <summary>Whether records of the category reach the sink.</summary>
    public static bool Allows(string? category) =>
        category is not null && category.StartsWith(AllowedPrefix, StringComparison.Ordinal);

    /// <summary>Whose a record of an allowed category is.</summary>
    /// <param name="category">The record's category.</param>
    /// <param name="configured">The tenant the harness is configured for.</param>
    /// <param name="flow">The tenant context of the flow that is logging.</param>
    /// <param name="values">The record's structured values, when it has any.</param>
    public static LogRecordOwner OwnerOf(
        string category,
        TenantId configured,
        ITenantContext flow,
        IEnumerable<KeyValuePair<string, object?>>? values)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(flow);

        // A record that names another tenant is that tenant's, whoever logged it and on whatever flow.
        var namesConfigured = false;
        foreach (var (name, value) in values ?? [])
        {
            if (NamesTenant(name, value, configured) is not { } isConfigured)
            {
                continue;
            }

            if (!isConfigured)
            {
                return LogRecordOwner.OtherTenant;
            }

            namesConfigured = true;
        }

        if (category.StartsWith(HarnessPrefix, StringComparison.Ordinal))
        {
            return LogRecordOwner.Harness;
        }

        if (flow.TryGetTenantId(out var running))
        {
            return running == configured ? LogRecordOwner.ConfiguredTenant : LogRecordOwner.OtherTenant;
        }

        return namesConfigured ? LogRecordOwner.ConfiguredTenant : LogRecordOwner.Unattributed;
    }

    /// <summary>Whether a record of that owner reaches the sink.</summary>
    public static bool Shows(LogRecordOwner owner) => owner is LogRecordOwner.Harness or LogRecordOwner.ConfiguredTenant;

    // Null when the value names no tenant; otherwise whether the tenant it names is the configured one. A value names a
    // tenant when it is a tenant id, or when the product's template calls it one. A tenant named in a form that cannot be
    // compared counts as another tenant.
    private static bool? NamesTenant(string name, object? value, TenantId configured)
    {
        if (value is TenantId tenant)
        {
            return tenant == configured;
        }

        var bare = name.TrimStart('@', '$');
        if (!string.Equals(bare, "TenantId", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bare, "Tenant", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value switch
        {
            Guid id => id == configured.Value,
            string text => string.Equals(text, configured.ToString(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

/// <summary>
/// The harness's logger factory: the policy and the redaction are the factory itself, not a filter laid over one.
/// </summary>
/// <remarks>
/// Each host of the harness is given this factory in place of the framework's, so there is no filter rule for
/// configuration to override and no provider list to extend: a provider added later is refused and counted. A record of
/// an allowed category that is the configured tenant's or the harness's own keeps its rendered message and loses its
/// exception, of which only the type name is passed on, because an exception's text can quote what it was working on.
/// Any other record is counted and nothing of it is rendered or kept. Scopes are dropped.
/// </remarks>
public sealed class HarnessLoggerFactory : ILoggerFactory
{
    private readonly IHarnessLogSink _sink;
    private readonly HarnessDiagnostics _diagnostics;
    private readonly TenantId _tenant;
    private readonly ITenantContextAccessor _flow;

    /// <summary>Creates the factory.</summary>
    /// <param name="sink">Where shown records go.</param>
    /// <param name="diagnostics">Counts refused providers and withheld records.</param>
    /// <param name="tenant">The tenant the harness is configured for.</param>
    /// <param name="flow">Reads the tenant context of the flow that is logging; it is never assigned here.</param>
    public HarnessLoggerFactory(IHarnessLogSink sink, HarnessDiagnostics diagnostics, TenantId tenant, ITenantContextAccessor flow)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(flow);
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("The configured tenant is required.", nameof(tenant));
        }

        _sink = sink;
        _diagnostics = diagnostics;
        _tenant = tenant;
        _flow = flow;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        HarnessLogPolicy.Allows(categoryName) ? new SinkLogger(categoryName, this) : NullLogger.Instance;

    /// <summary>Refuses the provider: nothing logs behind the policy.</summary>
    public void AddProvider(ILoggerProvider provider) => _diagnostics.LogProviderRefused();

    /// <inheritdoc />
    public void Dispose()
    {
        // The sink belongs to whoever supplied it.
    }

    private sealed class SinkLogger(string category, HarnessLoggerFactory factory) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None)
            {
                return;
            }

            // Whose the record is, decided before anything of it is rendered: a record that is not shown leaves a count.
            var owner = HarnessLogPolicy.OwnerOf(
                category, factory._tenant, factory._flow.Current, state as IEnumerable<KeyValuePair<string, object?>>);
            if (!HarnessLogPolicy.Shows(owner))
            {
                factory._diagnostics.LogRecordWithheld(owner);
                return;
            }

            // The formatter is never handed the exception, so its text cannot be rendered into the message.
            factory._sink.Write(new HarnessLogRecord(category, logLevel, eventId.Id, formatter(state, null), exception?.GetType().Name));
        }
    }
}

/// <summary>Writes records at Information and above to a text writer, one line each.</summary>
public sealed class TextWriterLogSink : IHarnessLogSink
{
    private readonly TextWriter _output;
    private readonly Lock _gate = new();

    /// <summary>Creates the sink.</summary>
    /// <param name="output">Where the lines go.</param>
    public TextWriterLogSink(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <inheritdoc />
    public void Write(HarnessLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Level < LogLevel.Information)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {record.Level} {record.Category} {record.Message}{(record.ExceptionType is null ? string.Empty : " [" + record.ExceptionType + "]")}");
        lock (_gate)
        {
            _output.WriteLine(line);
        }
    }
}
