using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>
/// Gate 2C outbox relay against the real local PostgreSQL, through the runtime role: committed markers are
/// published with their contract identity and marked only after the broker accepted them; a failed send, a
/// crash before or after the send and a lost commit all leave the message publishable and republish it under
/// the same message id; concurrent relays never publish a message twice; uncommitted rows are never claimed;
/// unrelayable rows never travel and never block; and the logs carry ids and codes only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OutboxRelayTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_committed_marker_is_published_once_with_its_contract_identity_and_then_marked()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var correlation = CorrelationId.New();

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-relay-published", IngressData.PayloadWith(IngressData.NewMarker()), correlation));
        var receiptId = Assert.NotNull(outcome.ReceiptId);
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptId, Token);

        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);

        var sent = Assert.Single(transport.Sends, wire => wire.MessageId == outboxId.ToString("D"));
        Assert.Equal("event-processing", sent.QueueName);
        Assert.Equal(tenant.Value.ToString("D"), sent.SessionId);
        Assert.Equal(correlation.Value, sent.CorrelationId);
        Assert.Equal(tenant.Value, sent.ApplicationProperties[TransportProperties.Tenant]);
        Assert.Equal(outboxId, sent.ApplicationProperties[TransportProperties.OutboxMessage]);
        Assert.Equal(receiptId, sent.ApplicationProperties[TransportProperties.Causation]);
        Assert.Equal(0, sent.ApplicationProperties[TransportProperties.RedriveCount]);
        Assert.Equal(TransportData.Marker(receiptId), Encoding.UTF8.GetString(sent.Body.Span));

        // Marked only after the broker accepted it, at the relay's clock.
        var row = Assert.Single(await TransportData.RowsAsync(fixture, [outboxId], Token));
        Assert.Equal(IngressHost.Now, row.PublishedAtUtc);
        Assert.Equal(tenant.Value, row.TenantId);

        // A later pass finds nothing more to send for it, and the caller's scope never carried a tenant.
        await host.Relay.RelayPendingAsync(Token);
        Assert.Single(transport.Sends, wire => wire.MessageId == outboxId.ToString("D"));
        Assert.False(host.TenantContextAccessor.Current.IsResolved);
    }

    [Fact]
    public async Task A_failed_send_leaves_that_message_and_the_rest_of_its_batch_pending_until_a_later_pass()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var ordered = await SubmitAsync(host, connection, count: 3, prefix: "evt-refused");

        transport.Fault = envelope => envelope.OutboxMessageId == ordered[1] ? SendFault.Refuse : SendFault.None;

        var passes = new List<OutboxRelayPass>();
        for (var pass = 0; pass < 100 && !await TransportData.AllPublishedAsync(fixture, [ordered[0]], Token); pass++)
        {
            passes.Add(await host.Relay.RelayPendingAsync(Token));
        }

        Assert.Contains(passes, pass => pass.PublishFailures > 0 && pass.HasFailures);
        var rows = await TransportData.RowsAsync(fixture, ordered, Token);
        Assert.Equal([true, false, false], rows.Select(row => row.PublishedAtUtc is not null));
        Assert.Equal([ordered[0].ToString("D")], SentIds(transport, ordered));

        transport.Fault = _ => SendFault.None;
        await host.RelayUntilPublishedAsync(fixture, ordered, Token);

        Assert.Equal(ordered.Select(id => id.ToString("D")), SentIds(transport, ordered));
        Assert.Contains(host.Logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Text.Contains(ordered[1].ToString("D"), StringComparison.Ordinal)
            && entry.Text.Contains(TransportErrors.PublishFailed.Code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_crash_before_the_send_leaves_the_message_publishable_and_it_is_sent_once()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var outboxId = (await SubmitAsync(host, connection, count: 1, prefix: "evt-crash-before"))[0];

        transport.Fault = envelope => envelope.OutboxMessageId == outboxId ? SendFault.CrashBeforeSend : SendFault.None;
        await RelayUntilCrashAsync(host, transport);

        Assert.Null(Assert.Single(await TransportData.RowsAsync(fixture, [outboxId], Token)).PublishedAtUtc);
        Assert.Empty(SentIds(transport, [outboxId]));

        transport.Fault = _ => SendFault.None;
        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);

        Assert.Single(SentIds(transport, [outboxId]));
    }

    [Fact]
    public async Task A_crash_after_the_send_and_before_the_commit_republishes_the_same_message_id_safely()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var inside = (await SubmitAsync(host, connection, count: 1, prefix: "evt-crash-inside"))[0];

        // Inside the duplicate-detection window the broker absorbs the republished message.
        transport.Fault = envelope => envelope.OutboxMessageId == inside ? SendFault.CrashAfterSend : SendFault.None;
        await RelayUntilCrashAsync(host, transport);

        Assert.Null(Assert.Single(await TransportData.RowsAsync(fixture, [inside], Token)).PublishedAtUtc);
        Assert.Single(transport.Accepted, wire => wire.MessageId == inside.ToString("D"));

        transport.Fault = _ => SendFault.None;
        await host.RelayUntilPublishedAsync(fixture, [inside], Token);

        Assert.Equal(2, SentIds(transport, [inside]).Count);
        Assert.Single(transport.Accepted, wire => wire.MessageId == inside.ToString("D"));
        Assert.True(transport.DuplicatesSuppressed >= 1);

        // Outside the window the duplicate is delivered: same identity, so it is safe by contract. The message is
        // submitted only now: had it waited in the same batch, the relay above would already have published it.
        var outside = (await SubmitAsync(host, connection, count: 1, prefix: "evt-crash-outside"))[0];
        transport.CrashSwitch = new CancellationTokenSource();
        transport.Fault = envelope => envelope.OutboxMessageId == outside ? SendFault.CrashAfterSend : SendFault.None;
        await RelayUntilCrashAsync(host, transport);
        transport.Now += TimeSpan.FromMinutes(10);
        transport.Fault = _ => SendFault.None;
        await host.RelayUntilPublishedAsync(fixture, [outside], Token);

        var copies = transport.Accepted.Where(wire => wire.MessageId == outside.ToString("D")).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(copies[0].CorrelationId, copies[1].CorrelationId);
        Assert.Equal(copies[0].SessionId, copies[1].SessionId);
        Assert.Equal(
            copies[0].ApplicationProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            copies[1].ApplicationProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.True(copies[0].Body.Span.SequenceEqual(copies[1].Body.Span));

        await transport.DrainAsync(host.Consumer);
        var handled = host.Handler.Handled.Where(item => item.Envelope.OutboxMessageId == outside).ToList();
        Assert.Equal(2, handled.Count);
        Assert.Equal(handled[0].Envelope.MessageId, handled[1].Envelope.MessageId);
        Assert.Equal(handled[0].Envelope.CausationId, handled[1].Envelope.CausationId);
    }

    [Fact]
    public async Task A_commit_lost_after_the_send_keeps_the_message_pending_and_it_is_republished_under_the_same_id()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var outboxId = (await SubmitAsync(host, connection, count: 1, prefix: "evt-commit-lost"))[0];

        // The database connection holding the relay's transaction dies between the send and the commit.
        var killed = false;
        transport.AfterSend = async envelope =>
        {
            if (envelope.OutboxMessageId == outboxId && !killed)
            {
                killed = true;
                await TerminateIdleRuntimeTransactionsAsync();
            }
        };

        var passes = new List<OutboxRelayPass>();
        for (var pass = 0; pass < 100 && !killed; pass++)
        {
            passes.Add(await host.Relay.RelayPendingAsync(Token));
        }

        Assert.True(killed);
        Assert.Contains(passes, pass => pass.CommitFailures > 0);
        Assert.Null(Assert.Single(await TransportData.RowsAsync(fixture, [outboxId], Token)).PublishedAtUtc);

        transport.AfterSend = null;
        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);

        Assert.Equal(2, SentIds(transport, [outboxId]).Count);
        Assert.Single(transport.Accepted, wire => wire.MessageId == outboxId.ToString("D"));
    }

    [Fact]
    public async Task Concurrent_relays_publish_every_message_exactly_once()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport, options => options.BatchSize = 3);
        var ids = new List<Guid>();
        for (var tenant = 0; tenant < 3; tenant++)
        {
            var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
            ids.AddRange(await SubmitAsync(host, connection, count: 8, prefix: $"evt-concurrent-{tenant}"));
        }

        for (var round = 0; round < 50 && !await TransportData.AllPublishedAsync(fixture, ids, Token); round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => host.Relay.RelayPendingAsync(Token), Token)));
        }

        Assert.True(await TransportData.AllPublishedAsync(fixture, ids, Token));
        foreach (var id in ids)
        {
            Assert.Single(SentIds(transport, [id]));
        }
    }

    [Fact]
    public async Task Only_committed_markers_are_ever_claimed()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);

        // A write that is rolled back is never published; one that commits is published after it commits.
        var rolledBack = await WriteWhileRelayingAsync(host, transport, tenant, connection, commit: false);
        var committed = await WriteWhileRelayingAsync(host, transport, tenant, connection, commit: true);

        Assert.Empty(await TransportData.RowsAsync(fixture, [rolledBack], Token));
        Assert.Empty(SentIds(transport, [rolledBack]));

        await host.RelayUntilPublishedAsync(fixture, [committed], Token);
        Assert.Single(SentIds(transport, [committed]));
    }

    [Fact]
    public async Task Each_tenant_is_relayed_only_in_its_own_scope_and_the_relay_refuses_a_tenant_scope()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        var idsA = await SubmitAsync(host, connectionA, count: 2, prefix: "evt-scope-a");
        var idsB = await SubmitAsync(host, connectionB, count: 2, prefix: "evt-scope-b");

        // Inside a scope that already carries a tenant, the relay refuses to run at all.
        await Task.Run(
            async () =>
            {
                host.TenantContextAccessor.SetResolved(
                    TenantContext.Resolved(tenantA, TenantResolutionSource.TrustedServiceIdentity));
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Relay.RelayPendingAsync(Token));
            },
            Token);
        Assert.Empty(SentIds(transport, [.. idsA, .. idsB]));

        await host.RelayUntilPublishedAsync(fixture, [.. idsA, .. idsB], Token);

        foreach (var (tenant, ids) in new[] { (tenantA, idsA), (tenantB, idsB) })
        {
            foreach (var wire in transport.Sends.Where(wire => ids.Select(id => id.ToString("D")).Contains(wire.MessageId)))
            {
                Assert.Equal(tenant.Value, wire.ApplicationProperties[TransportProperties.Tenant]);
                Assert.Equal(tenant.Value.ToString("D"), wire.SessionId);
            }
        }

        Assert.False(host.TenantContextAccessor.Current.IsResolved);
    }

    [Fact]
    public async Task Unrelayable_rows_are_never_published_or_marked_and_do_not_block_the_rows_behind_them()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(
            fixture,
            transport,
            options => (options.BatchSize, options.MaxBatchesPerTenant, options.TenantsPerPass) = (2, 10, 1));
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();

        // Older than anything else pending, so this tenant is the one a one-tenant pass visits.
        var older = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var unknownType = await TransportData.SeedOutboxRowAsync(
            fixture, tenant, "workcontinuity.unknown", TransportData.Marker(Guid.NewGuid()), older, Token);
        var content = await TransportData.SeedOutboxRowAsync(
            fixture, tenant, IngressMessageTypes.ReceiptReceived, $$"""{"receiptId":"{{Guid.NewGuid():D}}","note":"{{marker}}"}""", older, Token);
        var noMarker = await TransportData.SeedOutboxRowAsync(
            fixture, tenant, IngressMessageTypes.ReceiptReceived, $$"""{"note":"{{marker}}"}""", older, Token);
        Guid[] unrelayable = [unknownType, content, noMarker];

        try
        {
            var valid = await SubmitAsync(host, connection, count: 2, prefix: "evt-behind-unrelayable");

            var passes = await host.RelayUntilPublishedAsync(fixture, valid, Token);

            Assert.Contains(passes, pass => pass.Unrelayable >= 3);
            Assert.All(await TransportData.RowsAsync(fixture, unrelayable, Token), row => Assert.Null(row.PublishedAtUtc));
            Assert.Empty(SentIds(transport, unrelayable));

            var errors = host.Logs.Entries.Where(entry => entry.Level == LogLevel.Error).Select(entry => entry.Text).ToList();
            Assert.Contains(errors, text => text.Contains(unknownType.ToString("D"), StringComparison.Ordinal)
                && text.Contains(TransportErrors.MessageTypeUnsupported.Code, StringComparison.Ordinal));
            Assert.Contains(errors, text => text.Contains(content.ToString("D"), StringComparison.Ordinal)
                && text.Contains(TransportErrors.BodyInvalid.Code, StringComparison.Ordinal));
            Assert.Contains(errors, text => text.Contains(noMarker.ToString("D"), StringComparison.Ordinal)
                && text.Contains(TransportErrors.BodyInvalid.Code, StringComparison.Ordinal));
            Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains(marker, StringComparison.Ordinal));

            // With only unrelayable rows left, a full pass publishes nothing and asks for no immediate retry, so
            // the relay loop waits instead of spinning on them.
            var idle = await host.Relay.RelayPendingAsync(Token);
            Assert.Equal((1, 0, 3), (idle.Tenants, idle.Published, idle.Unrelayable));
            Assert.False(idle.MoreWork);
            Assert.False(idle.HasFailures);
        }
        finally
        {
            await TransportData.DeleteOutboxRowsAsync(fixture, unrelayable, Token);
        }
    }

    [Fact]
    public async Task Relay_logs_carry_ids_and_codes_but_never_the_payload_or_connection_details()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(fixture, transport);
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-relay-logs", IngressData.PayloadWith(marker)));
        var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, Assert.NotNull(outcome.ReceiptId), Token);

        var refusals = 0;
        transport.Fault = envelope => envelope.OutboxMessageId == outboxId && refusals++ < 1 ? SendFault.Refuse : SendFault.None;
        await host.RelayUntilPublishedAsync(fixture, [outboxId], Token);

        Assert.NotEmpty(host.Logs.Entries);
        Assert.Contains(host.Logs.Entries, entry => entry.Text.Contains(outboxId.ToString("D"), StringComparison.Ordinal));
        var password = new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString).Password!;
        foreach (var entry in host.Logs.Entries)
        {
            Assert.DoesNotContain(marker, entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(password, entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", entry.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_relay_loop_publishes_backs_off_on_failure_and_stops_when_cancelled()
    {
        var transport = new InMemoryTransport();
        await using var host = TransportHost.InMemory(
            fixture,
            transport,
            options => (options.IdleInterval, options.MaxFailureBackoff) = (TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200)));
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var outboxId = (await SubmitAsync(host, connection, count: 1, prefix: "evt-loop"))[0];

        var attempts = 0;
        transport.Fault = envelope => envelope.OutboxMessageId == outboxId && Interlocked.Increment(ref attempts) <= 2
            ? SendFault.Refuse
            : SendFault.None;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loop = host.Relay.RunAsync(stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await TransportData.AllPublishedAsync(fixture, [outboxId], Token) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Token);
        }

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.True(await TransportData.AllPublishedAsync(fixture, [outboxId], Token));
        Assert.True(attempts >= 3, $"attempts: {attempts}");
        Assert.Single(transport.Accepted, wire => wire.MessageId == outboxId.ToString("D"));
        Assert.True(loop.IsCompletedSuccessfully);
    }

    private async Task<IReadOnlyList<Guid>> SubmitAsync(TransportHost host, Guid connection, int count, string prefix)
    {
        var ids = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
                connection, $"{prefix}-{index}", IngressData.PayloadWith(IngressData.NewMarker())));
            Assert.Equal(IngressStatus.Accepted, outcome.Status);
            ids.Add(await TransportData.OutboxIdForReceiptAsync(fixture, outcome.ReceiptId!.Value, Token));
        }

        // The relay's order: occurrence time, then id.
        return (await TransportData.RowsAsync(fixture, ids, Token)).Select(row => row.OutboxMessageId).ToList();
    }

    private static List<string> SentIds(InMemoryTransport transport, IReadOnlyCollection<Guid> ids)
    {
        var wanted = ids.Select(id => id.ToString("D")).ToHashSet(StringComparer.Ordinal);
        return transport.Sends.Where(wire => wanted.Contains(wire.MessageId)).Select(wire => wire.MessageId).ToList();
    }

    private async Task RelayUntilCrashAsync(TransportHost host, InMemoryTransport transport)
    {
        for (var pass = 0; pass < 100 && !transport.CrashSwitch.IsCancellationRequested; pass++)
        {
            try
            {
                await host.Relay.RelayPendingAsync(transport.CrashSwitch.Token);
            }
            catch (OperationCanceledException) when (transport.CrashSwitch.IsCancellationRequested)
            {
                // The simulated crash.
            }
        }

        Assert.True(transport.CrashSwitch.IsCancellationRequested, "The crash was never reached.");
    }

    private async Task TerminateIdleRuntimeTransactionsAsync()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(pg_terminate_backend(pid))
            FROM pg_stat_activity
            WHERE usename = @role AND state = 'idle in transaction' AND pid <> pg_backend_pid();
            """,
            connection);
        command.Parameters.AddWithValue("role", PersistenceOptions.RuntimeRoleName);
        var terminated = (long)(await command.ExecuteScalarAsync(Token))!;
        Assert.True(terminated >= 1, "The relay's transaction was not found.");
    }

    private async Task<Guid> WriteWhileRelayingAsync(
        TransportHost host,
        InMemoryTransport transport,
        TenantId tenant,
        Guid connection,
        bool commit)
    {
        var receiptId = Guid.CreateVersion7();
        var outboxId = Guid.CreateVersion7();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = Task.Run(
            async () =>
            {
                var (database, context) = fixture.CreateTenantScoped(tenant);
                await using (context)
                {
                    return await database.ExecuteAsync(
                        async (dbContext, token) =>
                        {
                            dbContext.RawReceipts.Add(RawReceiptRecord.Receive(
                                receiptId, tenant, ConnectionRecord.ReferenceFor(connection), SyntheticSource.System,
                                $"evt-uncommitted-{receiptId:N}", SyntheticSource.SchemaVersion, "{}", "fingerprint"));
                            dbContext.OutboxMessages.Add(OutboxMessageRecord.Queue(
                                outboxId, tenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptId), IngressHost.Now, CorrelationId.New()));
                            await dbContext.SaveChangesAsync(token);
                            written.SetResult();
                            await release.Task.WaitAsync(token);
                            if (!commit)
                            {
                                throw new InvalidOperationException("The write is abandoned.");
                            }
                        },
                        Token);
                }
            },
            Token);

        await written.Task.WaitAsync(Token);
        for (var pass = 0; pass < 3; pass++)
        {
            await host.Relay.RelayPendingAsync(Token);
        }

        // While the write is open its rows are invisible to discovery and to the claim.
        Assert.Empty(SentIds(transport, [outboxId]));

        release.SetResult();
        var result = await writer;
        Assert.Equal(commit, result.IsSuccess);
        return outboxId;
    }
}
