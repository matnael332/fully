namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The server-side configuration of monday webhook intake: the public address its webhooks are registered under.
/// </summary>
/// <remarks>
/// The audience a delivery's token must carry is this address followed by the connection's route, so a connection's
/// tokens are good for that connection only. The address is server configuration, never request input: a request's own
/// host decides nothing. It must be an absolute HTTPS address without user information, query or fragment; a path
/// prefix is kept.
/// </remarks>
public sealed class MondayWebhookOptions
{
    private readonly string _base;

    /// <summary>Creates the options.</summary>
    /// <param name="publicBaseAddress">The public HTTPS address the host is reached at.</param>
    /// <exception cref="ArgumentException">The address is not an absolute HTTPS address without user information, query or fragment.</exception>
    public MondayWebhookOptions(Uri publicBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(publicBaseAddress);
        if (!publicBaseAddress.IsAbsoluteUri
            || publicBaseAddress.Scheme != Uri.UriSchemeHttps
            || publicBaseAddress.UserInfo.Length > 0
            || publicBaseAddress.Query.Length > 0
            || publicBaseAddress.Fragment.Length > 0
            || string.IsNullOrEmpty(publicBaseAddress.Host))
        {
            throw new ArgumentException(
                "The public base address must be an absolute HTTPS address without user information, query or fragment.",
                nameof(publicBaseAddress));
        }

        PublicBaseAddress = publicBaseAddress;
        _base = publicBaseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Gets the public base address.</summary>
    public Uri PublicBaseAddress { get; }

    /// <summary>The audience of a connection's tokens: the address its webhooks are registered under.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <returns>The public base address followed by the connection's route.</returns>
    public string AudienceFor(Guid connectionId) => _base + MondayWebhooks.PathFor(connectionId);
}
