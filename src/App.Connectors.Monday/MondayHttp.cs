using System.Net;

namespace WorkContinuity.Connectors.Monday;

/// <summary>The HTTP handler every monday client must use.</summary>
/// <remarks>
/// The handler never follows a redirect, so no request, token or client secret can be carried to another address; the
/// clients also refuse any answer that did not come from the address they called. It keeps no cookies, and its pooled
/// connections are renewed periodically so DNS changes are honoured. The host that registers the monday clients builds
/// their <see cref="HttpClient"/> instances on this handler.
/// </remarks>
public static class MondayHttp
{
    /// <summary>Creates the handler.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}

/// <summary>Reads response bodies within a size bound.</summary>
internal static class MondayHttpContent
{
    /// <summary>The body, or null when it exceeds <paramref name="maxBytes"/>, whether declared or actual.</summary>
    internal static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Whether the content declares a JSON media type.</summary>
    internal static bool IsJson(HttpContent content) =>
        content.Headers.ContentType?.MediaType is { } mediaType
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}
