using System.Net.Sockets;

namespace WorkContinuity.Infrastructure.Health;

public sealed record DependencyStatus(string Name, bool Healthy, string? Detail = null);

public sealed record ReadinessReport(bool Ready, IReadOnlyList<DependencyStatus> Dependencies);

public sealed class DependencyProbe
{
    public async Task<ReadinessReport> CheckAsync(
        string postgresHost,
        int postgresPort,
        Uri serviceBusHealthUri,
        CancellationToken cancellationToken = default)
    {
        var postgres = await CheckTcpAsync("postgres", postgresHost, postgresPort, cancellationToken);
        var serviceBus = await CheckHttpAsync("service-bus-emulator", serviceBusHealthUri, cancellationToken);
        var dependencies = new[] { postgres, serviceBus };

        return new ReadinessReport(dependencies.All(x => x.Healthy), dependencies);
    }

    private static async Task<DependencyStatus> CheckTcpAsync(
        string name,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return new DependencyStatus(name, true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new DependencyStatus(name, false, ex.GetType().Name);
        }
    }

    private static async Task<DependencyStatus> CheckHttpAsync(
        string name,
        Uri healthUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var http = new HttpClient();
            using var response = await http.GetAsync(healthUri, timeout.Token);
            return response.IsSuccessStatusCode
                ? new DependencyStatus(name, true)
                : new DependencyStatus(name, false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new DependencyStatus(name, false, ex.GetType().Name);
        }
    }
}
