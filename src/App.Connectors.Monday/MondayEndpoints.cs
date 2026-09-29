namespace WorkContinuity.Connectors.Monday;

/// <summary>The monday endpoints this connector calls.</summary>
/// <remarks>
/// The API endpoint is a fixed constant, never read from configuration or from provider data, so no request can be
/// steered to another host. An architecture test keeps this the only place the host is written.
/// </remarks>
public static class MondayEndpoints
{
    /// <summary>Gets monday's GraphQL API endpoint.</summary>
    public static Uri Api { get; } = new("https://api.monday.com/v2", UriKind.Absolute);
}
