using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Processing;
using WorkContinuity.Infrastructure.Retention;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Retention;

/// <summary>
/// A clock the test moves: ingress dates the outbox marker with it and processing reads it; neither the receipt time,
/// which the database stamps, nor the purge ever uses it.
/// </summary>
internal sealed class SettableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>A receipt's retention-relevant state as the database holds it.</summary>
internal sealed record ReceiptContent(
    Guid ReceiptId,
    Guid TenantId,
    string ConnectionReference,
    string SourceSystem,
    string? SourceEventId,
    string SchemaVersion,
    DateTimeOffset ReceivedAtUtc,
    string ProcessingState,
    bool HasPayload,
    bool HasFingerprint);

/// <summary>
/// Composes the real ingress, transport, processing and retention, ages receipts by the database's clock, runs purges
/// with the Worker's clock set anywhere, and reads back what the purge left with the schema owner.
/// </summary>
/// <remarks>
/// The database's clock alone decides what is due and how late it is, and it stamps every receipt time itself, so a
/// receipt is aged by moving its database-stamped time back by the chosen age right after ingress stored it, before
/// anything processes it. That step is test-only, and only the local superuser can take it. Its age is then exact
/// whatever any host clock says. Ages keep an hour's margin from every boundary, and the exact boundaries are proven
/// inside single database transactions in <c>RetentionDatabaseSecurityTests</c>. The database tests run one at a time,
/// and each retention test first purges whatever due content earlier tests left, so a run's counts are exactly the
/// test's own receipts.
/// </remarks>
internal static class RetentionHarness
{
    /// <summary>One microsecond, the finest step PostgreSQL stores.</summary>
    public static readonly TimeSpan Microsecond = TimeSpan.FromTicks(10);

    public static TransportHost Host(
        DatabaseFixture fixture,
        InMemoryTransport transport,
        SettableClock clock,
        Action<RawContentPurgeOptions>? purge = null,
        Action<IServiceCollection>? more = null) =>
        TransportHost.InMemory(
            fixture,
            transport,
            handlers: services =>
            {
                services.AddEventProcessing();
                services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
                services.AddRawContentRetention(purge);
                more?.Invoke(services);
            },
            clock: clock);

    /// <summary>
    /// Submits one delivery, then ages its receipt by <paramref name="age"/> by the database's clock, as if the
    /// database had stored it that long ago.
    /// </summary>
    public static async Task<Delivered> SubmitAgedAsync(
        this TransportHost host,
        DatabaseFixture fixture,
        SettableClock clock,
        TimeSpan age,
        TenantId tenant,
        Guid connection,
        SyntheticEvent synthetic,
        string? sourceEventId)
    {
        clock.UtcNow = DateTimeOffset.UtcNow;
        var delivered = await host.SubmitAsync(fixture, tenant, connection, synthetic, sourceEventId);
        await AgeReceiptAsync(fixture, delivered.ReceiptId, age);
        return delivered;
    }

    /// <summary>Moves a receipt's database-stamped time back by <paramref name="age"/>.</summary>
    /// <remarks>
    /// Test-only: the trigger that stamps every receipt time refuses any change to it, and only a superuser's replica
    /// mode skips it. Neither the runtime role nor a non-superuser owner can do this.
    /// </remarks>
    public static async Task AgeReceiptAsync(DatabaseFixture fixture, Guid receiptId, TimeSpan age)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var replica = new NpgsqlCommand("SET LOCAL session_replication_role = replica;", connection, transaction))
        {
            await replica.ExecuteNonQueryAsync(token);
        }

        await using (var older = new NpgsqlCommand(
            "UPDATE raw_receipts SET received_at_utc = received_at_utc - @age WHERE receipt_id = @receipt;", connection, transaction))
        {
            older.Parameters.AddWithValue("age", age);
            older.Parameters.AddWithValue("receipt", receiptId);
            Assert.Equal(1, await older.ExecuteNonQueryAsync(token));
        }

        await transaction.CommitAsync(token);
    }

    /// <summary>
    /// Runs one purge in its own flow, as a host would, with the Worker's clock set to <paramref name="workerNow"/>, or
    /// to this machine's now: the purge must not care.
    /// </summary>
    public static async Task<RawContentPurgeRun> PurgeAsync(this TransportHost host, SettableClock clock, DateTimeOffset? workerNow = null)
    {
        clock.UtcNow = workerNow ?? DateTimeOffset.UtcNow;
        return await Task.Run(
            () => host.Services.GetRequiredService<IRawContentPurge>().PurgeExpiredAsync(TestContext.Current.CancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// Purges, until nothing due is left, whatever due content earlier tests left, so the test's own runs count only
    /// its own receipts.
    /// </summary>
    public static async Task ClearDueAsync(this TransportHost host, SettableClock clock)
    {
        for (var run = 0; run < 50; run++)
        {
            var cleared = await host.PurgeAsync(clock);
            Assert.NotEqual(RawContentPurgeOutcome.Failed, cleared.Outcome);
            if (cleared.Outcome == RawContentPurgeOutcome.Completed)
            {
                return;
            }
        }

        Assert.Fail("Due content left by earlier tests was not cleared within 50 runs.");
    }

    /// <summary>
    /// Asserts an age a run reported, for content aged <paramref name="stamped"/> by the database's clock: never less
    /// than that, since the database's clock only moves on, and only the moments the test itself took more. A Worker
    /// clock can add or remove nothing.
    /// </summary>
    public static void AssertAge(TimeSpan stamped, TimeSpan? reported)
    {
        Assert.NotNull(reported);
        Assert.InRange(reported.Value, stamped, stamped + TimeSpan.FromMinutes(5));
    }

    public static async Task<IReadOnlyList<ReceiptContent>> ContentAsync(DatabaseFixture fixture, TenantId tenant)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_id, tenant_id, connection_reference, source_system, source_event_id, schema_version,
                   received_at_utc, processing_state, payload IS NOT NULL, payload_fingerprint IS NOT NULL
            FROM raw_receipts
            WHERE tenant_id = @tenant
            ORDER BY received_at_utc, receipt_id;
            """;
        command.Parameters.AddWithValue("tenant", tenant.Value);

        var rows = new List<ReceiptContent>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new ReceiptContent(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9)));
        }

        return rows;
    }

    /// <summary>Whether the receipt still holds its raw content, asserting the payload and fingerprint agree.</summary>
    public static async Task<bool> HoldsContentAsync(DatabaseFixture fixture, TenantId tenant, Guid receiptId)
    {
        var receipt = Assert.Single(await ContentAsync(fixture, tenant), row => row.ReceiptId == receiptId);
        Assert.Equal(receipt.HasPayload, receipt.HasFingerprint);
        return receipt.HasPayload;
    }
}
