namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The fixed parameters of monday webhook intake (Gate 3C): the route, the limits and the schema version a delivery is
/// stored under.
/// </summary>
/// <remarks>
/// The route is written here once, so the address the host serves and the audience a connection's tokens must carry can
/// never drift apart. Its only parameter is the connection id; no tenant or user is ever part of it.
/// </remarks>
public static class MondayWebhooks
{
    /// <summary>The route of a connection's webhook endpoint.</summary>
    public const string RouteTemplate = RoutePrefix + "{" + ConnectionIdParameter + "}";

    /// <summary>The name of the route's only parameter.</summary>
    public const string ConnectionIdParameter = "connectionId";

    /// <summary>The schema version of a delivery as ingress stores it: the webhook body exactly as monday sent it.</summary>
    public const string SchemaVersion = "monday.webhook.v1";

    /// <summary>The largest accepted body, in bytes (256 KiB). A larger one is refused, never truncated.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>The longest accepted <c>Authorization</c> value, in characters.</summary>
    public const int MaxAuthorizationLength = 8 * 1024;

    /// <summary>The source system every monday delivery is submitted under.</summary>
    public const string SourceSystem = MondayConnectionAuthorizer.SourceSystem;

    private const string RoutePrefix = "/api/connectors/monday/webhooks/";

    /// <summary>
    /// Reads a route's connection id. Only the canonical form is accepted, 36 lowercase characters with hyphens, and
    /// never the empty id, so each connection has exactly one address.
    /// </summary>
    /// <param name="value">The route value.</param>
    /// <param name="connectionId">The connection id, when the value is canonical.</param>
    /// <returns>True when the value is a canonical, non-empty connection id.</returns>
    public static bool TryParseConnectionId(string? value, out Guid connectionId)
    {
        connectionId = Guid.Empty;
        if (value is not { Length: 36 }
            || !Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            return false;
        }

        connectionId = parsed;
        return true;
    }

    /// <summary>The path of a connection's endpoint.</summary>
    internal static string PathFor(Guid connectionId) => RoutePrefix + connectionId.ToString("D");
}
