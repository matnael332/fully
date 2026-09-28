using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Transport;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 2C transport properties that must hold by construction: the one tenant-less outbox function has one
/// internal caller with a fixed statement; the Service Bus SDK and the broker's tenant property stay where
/// they belong; only the content-free marker travels; nothing re-drives on its own; no host runs the transport
/// before a downstream handler exists; and the queues give the consumer room to decide before the broker does.
/// </summary>
public sealed class TransportSecurityTests
{
    private const string DiscoveryFile = "src/App.Infrastructure/Transport/OutboxTenantDiscovery.cs";
    private const string RelayMigrationFile =
        "src/App.Infrastructure/Persistence/Migrations/20260928114056_OutboxRelayTenantDiscovery.cs";

    [Fact]
    public void Only_the_discovery_class_calls_the_tenant_less_outbox_function_and_only_its_migration_defines_it()
    {
        var mentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("outbox_pending_tenants(", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([RelayMigrationFile, DiscoveryFile], mentions);

        // The statement is fixed, parameterised and schema-qualified; the limit is its only input.
        var discovery = Read(DiscoveryFile);
        Assert.Equal(1, Occurrences(discovery, "outbox_pending_tenants("));
        Assert.Contains(
            "SELECT tenant_id FROM public.outbox_pending_tenants(@limit)",
            discovery,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_tenant_less_discovery_is_one_internal_method_taking_only_a_limit_and_returning_tenant_ids()
    {
        var discovery = typeof(EventTransportRegistration).Assembly.GetType(
            "WorkContinuity.Infrastructure.Transport.OutboxTenantDiscovery",
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
        Assert.Equal([typeof(int), typeof(CancellationToken)], method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(Task<IReadOnlyList<TenantId>>), method.ReturnType);
    }

    [Fact]
    public void The_relay_consumer_publisher_and_dead_letter_queue_are_internal_behind_core_ports()
    {
        var assembly = typeof(EventTransportRegistration).Assembly;
        foreach (var name in new[]
                 {
                     "OutboxRelay",
                     "EventTransportConsumer",
                     "ServiceBusEventPublisher",
                     "ServiceBusDeadLetterQueue",
                     "ServiceBusTransportSettings",
                     "ServiceBusTransportMapping",
                 })
        {
            var type = assembly.GetType($"WorkContinuity.Infrastructure.Transport.{name}", throwOnError: true)!;
            Assert.False(type.IsPublic, $"{name} must stay internal.");
        }

        var publicTransportTypes = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "WorkContinuity.Infrastructure.Transport")
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["EventTransportOptions", "EventTransportRegistration", "ServiceBusEventConsumerHost"], publicTransportTypes);

        // The consumer host cannot be built around the consumer from outside: only the registration composes it.
        Assert.Empty(typeof(ServiceBusEventConsumerHost).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void The_service_bus_sdk_is_used_only_by_infrastructure_readiness_and_transport()
    {
        var users = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("Azure.Messaging.ServiceBus", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToList();

        Assert.NotEmpty(users);
        Assert.All(users, path => Assert.True(
            path.StartsWith("src/App.Infrastructure/Health/", StringComparison.Ordinal)
            || path.StartsWith("src/App.Infrastructure/Transport/", StringComparison.Ordinal),
            $"{path} uses the Service Bus SDK"));
    }

    [Fact]
    public void Only_the_envelope_validator_reads_a_tenant_from_broker_metadata()
    {
        // The consumer restores a tenant only from a validated envelope, and only the envelope reads the
        // broker's tenant property; the literal property name is declared once.
        var readers = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("TransportProperties.Tenant", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal(["src/App.Core/Transport/TransportEnvelope.cs"], readers);

        var declarations = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("\"wce-tenant\"", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .ToArray();
        Assert.Equal(["src/App.Core/Transport/TransportContract.cs"], declarations);
    }

    [Fact]
    public void Only_the_content_free_receipt_marker_is_carried()
    {
        // Adding a message type is deliberate: its body shape must be proven content-free first, or raw
        // work data would reach the broker and its dead-letter queue, outside the 30-day retention.
        Assert.Equal([IngressMessageTypes.ReceiptReceived], TransportContract.MessageTypeQueues.Keys);
        Assert.Equal(TransportContract.EventProcessingQueue, TransportContract.MessageTypeQueues[IngressMessageTypes.ReceiptReceived]);
        Assert.True(TransportContract.MaxBodyBytes <= 1024);
    }

    [Fact]
    public void Nothing_re_drives_automatically()
    {
        // Re-drive is declared by the port and implemented by the adapter; no production code calls it.
        var mentions = RepositoryLayout.ProductionSourceFiles
            .Where(file => File.ReadAllText(file.FullName).Contains("RedriveAsync(", StringComparison.Ordinal))
            .Select(RepositoryLayout.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["src/App.Core/Transport/DeadLetterQueue.cs", "src/App.Infrastructure/Transport/ServiceBusDeadLetterQueue.cs"],
            mentions);
        Assert.Equal(1, Occurrences(Read("src/App.Infrastructure/Transport/ServiceBusDeadLetterQueue.cs"), "RedriveAsync("));
    }

    [Fact]
    public void No_host_runs_the_event_transport_before_a_downstream_handler_exists()
    {
        // Gate 2C composes the transport but activates it nowhere. The Gate that registers a downstream
        // handler and activates the Worker updates this list deliberately.
        string[] activation = ["AddEventTransport", "AddServiceBusEventTransport", "IOutboxRelay", "ServiceBusEventConsumerHost", "ITransportMessageHandler"];

        var hosts = RepositoryLayout.ProductionSourceFiles
            .Where(file =>
            {
                var path = RepositoryLayout.RelativePath(file);
                return path.StartsWith("src/App.Web/", StringComparison.Ordinal)
                    || path.StartsWith("src/App.Worker/", StringComparison.Ordinal);
            })
            .ToList();
        Assert.NotEmpty(hosts);

        foreach (var file in hosts)
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var marker in activation)
            {
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{RepositoryLayout.RelativePath(file)} mentions {marker}");
            }
        }
    }

    [Fact]
    public void Every_event_processing_queue_uses_sessions_dead_letters_expiry_and_leaves_the_consumer_room_to_decide()
    {
        var attempts = TransportRetryPolicy.Default.MaxAttempts;

        // Local emulator.
        using var emulator = JsonDocument.Parse(Read("infra/local/servicebus/config.json"));
        var queue = emulator.RootElement
            .GetProperty("UserConfig").GetProperty("Namespaces")[0].GetProperty("Queues")
            .EnumerateArray()
            .Single(item => item.GetProperty("Name").GetString() == TransportContract.EventProcessingQueue)
            .GetProperty("Properties");
        Assert.True(queue.GetProperty("RequiresSession").GetBoolean());
        Assert.True(queue.GetProperty("RequiresDuplicateDetection").GetBoolean());
        Assert.True(queue.GetProperty("DeadLetteringOnMessageExpiration").GetBoolean());
        Assert.True(queue.GetProperty("MaxDeliveryCount").GetInt32() >= attempts);

        // Azure.
        var bicep = Read("infra/bicep/modules/serviceBus.bicep");
        var eventProcessing = Regex.Match(
            bicep,
            @"name:\s*'event-processing'\s*properties:\s*\{(.*?)\}",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("requiresSession: true", eventProcessing, StringComparison.Ordinal);
        Assert.Contains("requiresDuplicateDetection: true", eventProcessing, StringComparison.Ordinal);
        Assert.Contains("deadLetteringOnMessageExpiration: true", eventProcessing, StringComparison.Ordinal);
        Assert.Contains("maxDeliveryCount: maxDeliveryCount", eventProcessing, StringComparison.Ordinal);
        var maxDeliveryCount = int.Parse(
            Regex.Match(bicep, @"param maxDeliveryCount int = (\d+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(maxDeliveryCount >= attempts);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
