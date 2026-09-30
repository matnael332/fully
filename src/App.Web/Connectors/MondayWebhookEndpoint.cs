using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Web.Platform;

namespace WorkContinuity.Web.Connectors;

/// <summary>The fixed errors of the monday webhook endpoint. None echoes the request.</summary>
public static class MondayWebhookErrors
{
    /// <summary>The body is not a challenge or an event monday sends.</summary>
    public static Error Malformed { get; } =
        Error.Validation("webhook.malformed", "The request is not a valid webhook delivery.");

    /// <summary>The body is not uncompressed JSON in UTF-8.</summary>
    public static Error UnsupportedMediaType { get; } =
        Error.Validation("webhook.unsupported_media_type", "The request must be uncompressed JSON in UTF-8.");

    /// <summary>The body is larger than the endpoint accepts.</summary>
    public static Error TooLarge { get; } =
        Error.Validation("webhook.too_large", "The request body exceeds the supported size.");

    /// <summary>The delivery's token is missing or not valid for the connection.</summary>
    public static Error Unauthenticated { get; } =
        Error.Unauthorized("webhook.unauthenticated", "The request is not authenticated.");

    /// <summary>The address names no webhook endpoint that accepts deliveries.</summary>
    public static Error NotFound { get; } =
        Error.NotFound("webhook.not_found", "No such webhook endpoint.");

    /// <summary>Nothing was stored; the source should retry.</summary>
    public static Error Unavailable { get; } =
        Error.Infrastructure("webhook.unavailable", "The delivery was not stored; retry it.");
}

/// <summary>
/// The public monday webhook endpoint (Gate 3C): <c>POST</c> on <see cref="MondayWebhooks.RouteTemplate"/>, whose only
/// parameter is the connection id.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint owns HTTP only; monday's protocol stays in the connector and storage in the Gate 2 ingress. It refuses,
/// before reading the body, a request that is not uncompressed JSON in UTF-8 or declares more than
/// <see cref="MondayWebhooks.MaxBodyBytes"/> bytes, and a connection id that is not canonical. It then reads the body to
/// at most that size, never truncating it, and tells a challenge from an event by the body alone.
/// </para>
/// <para>
/// A challenge is answered only for an active monday connection, through <see cref="IIngressConnectionCheck"/>, and is
/// never stored. An event's token is verified before anything reaches the database; the delivery then goes through
/// <see cref="IEventIngress"/>, which resolves the tenant from the connection, verifies and confirms the delivery, and
/// stores it durably. Success is answered only for an acknowledged outcome, after the commit; every other answer leaves
/// nothing stored, so the source's retries are safe. The tenant never comes from the request.
/// </para>
/// <para>
/// Logs carry the connection id, the correlation id, fixed reason codes and outcome statuses only: never the body, a
/// header, the token, a claim, a secret or provider text. Error bodies carry a fixed code, a fixed message and the
/// correlation id.
/// </para>
/// </remarks>
public static class MondayWebhookEndpoint
{
    private const string LogCategory = "WorkContinuity.Web.Connectors.MondayWebhookEndpoint";
    private const int ReadChunkBytes = 16 * 1024;

    /// <summary>Maps the endpoint.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The endpoint's convention builder.</returns>
    public static IEndpointConventionBuilder MapMondayWebhooks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost(MondayWebhooks.RouteTemplate, HandleAsync);
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        var correlationId = services.GetRequiredService<ICorrelationContextAccessor>().Current?.CorrelationId
            ?? CorrelationId.New();
        var cancellationToken = context.RequestAborted;
        var request = context.Request;

        if (!IsUncompressedJson(request))
        {
            await RefuseAsync(context, logger, StatusCodes.Status415UnsupportedMediaType, MondayWebhookErrors.UnsupportedMediaType, null, correlationId);
            return;
        }

        if (request.ContentLength > MondayWebhooks.MaxBodyBytes)
        {
            await RefuseAsync(context, logger, StatusCodes.Status413PayloadTooLarge, MondayWebhookErrors.TooLarge, null, correlationId);
            return;
        }

        if (!MondayWebhooks.TryParseConnectionId(
                context.GetRouteValue(MondayWebhooks.ConnectionIdParameter) as string,
                out var connectionId))
        {
            await RefuseAsync(context, logger, StatusCodes.Status404NotFound, MondayWebhookErrors.NotFound, null, correlationId);
            return;
        }

        var (body, tooLarge) = await ReadBodyAsync(context, cancellationToken);
        if (body is null)
        {
            await (tooLarge
                ? RefuseAsync(context, logger, StatusCodes.Status413PayloadTooLarge, MondayWebhookErrors.TooLarge, connectionId, correlationId)
                : RefuseAsync(context, logger, StatusCodes.Status400BadRequest, MondayWebhookErrors.Malformed, connectionId, correlationId));
            return;
        }

        var delivery = MondayWebhookBody.Read(body);
        switch (delivery.Kind)
        {
            case MondayWebhookBodyKind.Challenge:
                await AnswerChallengeAsync(context, logger, delivery, connectionId, correlationId, cancellationToken);
                break;
            case MondayWebhookBodyKind.Event:
                await ReceiveEventAsync(context, logger, delivery, connectionId, correlationId, cancellationToken);
                break;
            default:
                await RefuseAsync(context, logger, StatusCodes.Status400BadRequest, MondayWebhookErrors.Malformed, connectionId, correlationId);
                break;
        }
    }

    // The challenge proves that this address answers for the connection. It is never stored, never reaches ingress,
    // and needs no token: monday does not document one on the challenge.
    private static async Task AnswerChallengeAsync(
        HttpContext context,
        ILogger logger,
        MondayWebhookBody challenge,
        Guid connectionId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var status = await context.RequestServices
            .GetRequiredService<IIngressConnectionCheck>()
            .CheckAsync(connectionId, MondayWebhooks.SourceSystem, cancellationToken);

        switch (status)
        {
            case IngressConnectionStatus.Active:
                logger.LogInformation(
                    "monday webhook challenge answered on connection {ConnectionId} (correlation {CorrelationId}).",
                    connectionId,
                    correlationId);
                var response = challenge.ChallengeResponse();
                context.Response.StatusCode = StatusCodes.Status200OK;
                SetResponseHeaders(context.Response);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = response.Length;
                await context.Response.Body.WriteAsync(response, cancellationToken);
                break;
            case IngressConnectionStatus.Unknown:
                await RefuseAsync(context, logger, StatusCodes.Status404NotFound, MondayWebhookErrors.NotFound, connectionId, correlationId);
                break;
            default:
                await RefuseAsync(context, logger, StatusCodes.Status503ServiceUnavailable, MondayWebhookErrors.Unavailable, connectionId, correlationId);
                break;
        }
    }

    private static async Task ReceiveEventAsync(
        HttpContext context,
        ILogger logger,
        MondayWebhookBody delivery,
        Guid connectionId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        // The token is checked before anything reaches the database, so an unauthenticated request touches no data.
        var authorization = context.Request.Headers.Authorization;
        var authentication = await context.RequestServices
            .GetRequiredService<MondayWebhookAuthenticator>()
            .AuthenticateAsync(connectionId, authorization.Count == 1 ? authorization[0] : null, cancellationToken);

        if (authentication.Status == MondayWebhookAuthenticationStatus.Unavailable)
        {
            await RefuseAsync(context, logger, StatusCodes.Status503ServiceUnavailable, MondayWebhookErrors.Unavailable, connectionId, correlationId);
            return;
        }

        if (!authentication.IsVerified)
        {
            await RefuseAsync(context, logger, StatusCodes.Status401Unauthorized, MondayWebhookErrors.Unauthenticated, connectionId, correlationId);
            return;
        }

        var outcome = await context.RequestServices
            .GetRequiredService<IEventIngress>()
            .SubmitAsync(delivery.ToSubmission(authentication, correlationId), cancellationToken);

        if (outcome.IsAcknowledged)
        {
            // Only now, after the receipt and its outbox marker are committed, now or by an earlier delivery.
            logger.LogInformation(
                "monday webhook delivery on connection {ConnectionId} acknowledged as {IngressStatus}, receipt {ReceiptId} (correlation {CorrelationId}).",
                connectionId,
                outcome.Status,
                outcome.ReceiptId,
                correlationId);
            context.Response.StatusCode = StatusCodes.Status200OK;
            SetResponseHeaders(context.Response);
            context.Response.ContentLength = 0;
            return;
        }

        var (statusCode, error) = RefusalFor(outcome);
        await RefuseAsync(context, logger, statusCode, error, connectionId, correlationId);
    }

    // A connection that is unknown, inactive or another source's, and a tenant that is not active, all look the same.
    private static (int StatusCode, Error Error) RefusalFor(IngressOutcome outcome)
    {
        if (outcome.Status != IngressStatus.Rejected || outcome.Error is not { } error)
        {
            return (StatusCodes.Status503ServiceUnavailable, MondayWebhookErrors.Unavailable);
        }

        if (error == IngressErrors.UnknownConnection || error == IngressErrors.TenantInactive || error == IngressErrors.SourceMismatch)
        {
            return (StatusCodes.Status404NotFound, MondayWebhookErrors.NotFound);
        }

        return error == IngressErrors.SourceNotVerified
            ? (StatusCodes.Status401Unauthorized, MondayWebhookErrors.Unauthenticated)
            : (StatusCodes.Status400BadRequest, MondayWebhookErrors.Malformed);
    }

    private static bool IsUncompressedJson(HttpRequest request)
    {
        var encoding = request.Headers.ContentEncoding;
        if (encoding.Count > 1 || (encoding.Count == 1 && !string.Equals(encoding[0], "identity", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            && mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            && (!mediaType.Charset.HasValue || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));
    }

    // Reads at most the supported size and never truncates: a larger body is refused whole.
    private static async Task<(byte[]? Body, bool TooLarge)> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MondayWebhooks.MaxBodyBytes;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MondayWebhooks.MaxBodyBytes)
                {
                    return (null, true);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception)
        {
            return (null, exception.StatusCode == StatusCodes.Status413PayloadTooLarge);
        }

        return (buffer.ToArray(), false);
    }

    private static Task RefuseAsync(
        HttpContext context,
        ILogger logger,
        int statusCode,
        Error error,
        Guid? connectionId,
        CorrelationId correlationId)
    {
        logger.LogWarning(
            "monday webhook request on connection {ConnectionId} refused with {StatusCode} {ErrorCode}; nothing was stored (correlation {CorrelationId}).",
            connectionId,
            statusCode,
            error.Code,
            correlationId);

        context.Response.StatusCode = statusCode;
        SetResponseHeaders(context.Response);
        return context.Response.WriteAsJsonAsync(
            PlatformErrorResponse.From(error, correlationId.Value),
            context.RequestAborted);
    }

    private static void SetResponseHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
    }
}
