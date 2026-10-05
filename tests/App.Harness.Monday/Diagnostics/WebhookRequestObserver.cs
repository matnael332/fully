using System.Buffers.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WorkContinuity.Connectors.Monday;

namespace WorkContinuity.Harness.Monday.Diagnostics;

/// <summary>
/// Counts each request to the harness's webhook listener by the shape of its <c>Authorization</c> header and by how
/// the existing endpoint answered (owner decision D3G-6).
/// </summary>
/// <remarks>
/// <para>
/// It observes; it decides nothing. The existing endpoint and its authenticator verify every token exactly as in
/// production, and this type neither sees their result nor changes the request: it reads the header's form, and the
/// names of a token's header and claims, to closed enumerations and numbers, and it never reads the body.
/// </para>
/// <para>
/// What it keeps is the shape only: the header's form; the algorithm and the type the token's header names, each from
/// a closed list; the answer's status class; and, only for a request the endpoint accepted, which known claim names the
/// token carries, how its audience relates to the address the connection's webhooks are registered under, and its
/// lifetime in seconds, bounded to a day. An unknown algorithm, type or claim name is counted as "Other". No token, no
/// signature, no claim value and no header value is kept anywhere.
/// </para>
/// <para>
/// Whether a request is a challenge or an event, and anything else about a delivery's body, is not observed here: that
/// would need a copy of the payload, and is deferred.
/// </para>
/// </remarks>
internal sealed class WebhookRequestObserver
{
    private const int MaxSegmentLength = 4096;
    private const string BearerPrefix = "Bearer ";

    private readonly HarnessDiagnostics _diagnostics;
    private readonly MondayWebhookOptions _options;

    internal WebhookRequestObserver(HarnessDiagnostics diagnostics, MondayWebhookOptions options)
    {
        _diagnostics = diagnostics;
        _options = options;
    }

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var observed = Observe(context);
        try
        {
            await next(context);
        }
        finally
        {
            var response = ResponseOf(context.Response.StatusCode);
            _diagnostics.WebhookRequest(new WebhookRequestShape(observed.Form, observed.Algorithm, observed.Type, response));

            // Claims are counted only for a token the endpoint itself accepted.
            if (response == WebhookResponseClass.Accepted && observed.Claims is { } claims)
            {
                foreach (var claim in claims.Names)
                {
                    _diagnostics.AcceptedWebhookClaim(claim);
                }

                _diagnostics.AcceptedWebhookAudience(claims.Audience);
                if (claims.LifetimeSeconds is { } lifetime)
                {
                    _diagnostics.AcceptedWebhookLifetime(lifetime);
                }
            }
        }
    }

    private Observation Observe(HttpContext context)
    {
        var header = context.Request.Headers.Authorization;
        if (header.Count == 0)
        {
            return new Observation(WebhookAuthorizationForm.Absent, WebhookTokenAlgorithm.NotApplicable, WebhookTokenType.NotApplicable, null);
        }

        if (header.Count > 1)
        {
            return new Observation(WebhookAuthorizationForm.Multiple, WebhookTokenAlgorithm.NotApplicable, WebhookTokenType.NotApplicable, null);
        }

        var value = header[0];
        if (string.IsNullOrEmpty(value) || value.Length > MondayWebhooks.MaxAuthorizationLength)
        {
            return new Observation(WebhookAuthorizationForm.Other, WebhookTokenAlgorithm.NotApplicable, WebhookTokenType.NotApplicable, null);
        }

        WebhookAuthorizationForm form;
        ReadOnlySpan<char> token;
        if (value.StartsWith(BearerPrefix, StringComparison.Ordinal) && !value.AsSpan(BearerPrefix.Length).Contains(' '))
        {
            form = WebhookAuthorizationForm.Bearer;
            token = value.AsSpan(BearerPrefix.Length);
        }
        else if (!value.Contains(' ', StringComparison.Ordinal))
        {
            form = WebhookAuthorizationForm.Bare;
            token = value.AsSpan();
        }
        else
        {
            return new Observation(WebhookAuthorizationForm.Other, WebhookTokenAlgorithm.NotApplicable, WebhookTokenType.NotApplicable, null);
        }

        var first = token.IndexOf('.');
        var last = token.LastIndexOf('.');
        if (first <= 0 || last <= first + 1 || token[(first + 1)..last].Contains('.'))
        {
            return new Observation(form, WebhookTokenAlgorithm.Unreadable, WebhookTokenType.Unreadable, null);
        }

        var (algorithm, type) = HeaderOf(token[..first]);
        var expectedAudience = MondayWebhooks.TryParseConnectionId(
            context.GetRouteValue(MondayWebhooks.ConnectionIdParameter) as string, out var connectionId)
            ? _options.AudienceFor(connectionId)
            : null;
        return new Observation(form, algorithm, type, ClaimsOf(token[(first + 1)..last], expectedAudience));
    }

    private static (WebhookTokenAlgorithm Algorithm, WebhookTokenType Type) HeaderOf(ReadOnlySpan<char> segment)
    {
        using var document = Parse(segment);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (WebhookTokenAlgorithm.Unreadable, WebhookTokenType.Unreadable);
        }

        var algorithm = WebhookTokenAlgorithm.Other;
        if (document.RootElement.TryGetProperty("alg", out var alg) && alg.ValueKind == JsonValueKind.String)
        {
            algorithm = alg.GetString() switch
            {
                "HS256" => WebhookTokenAlgorithm.HS256,
                "HS384" => WebhookTokenAlgorithm.HS384,
                "HS512" => WebhookTokenAlgorithm.HS512,
                "RS256" => WebhookTokenAlgorithm.RS256,
                "ES256" => WebhookTokenAlgorithm.ES256,
                "none" => WebhookTokenAlgorithm.None,
                _ => WebhookTokenAlgorithm.Other,
            };
        }

        var type = WebhookTokenType.Absent;
        if (document.RootElement.TryGetProperty("typ", out var typ))
        {
            type = typ.ValueKind == JsonValueKind.String && string.Equals(typ.GetString(), "JWT", StringComparison.Ordinal)
                ? WebhookTokenType.Jwt
                : WebhookTokenType.Other;
        }

        return (algorithm, type);
    }

    private static ClaimShape? ClaimsOf(ReadOnlySpan<char> segment, string? expectedAudience)
    {
        using var document = Parse(segment);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var names = new List<WebhookClaim>();
        var audience = AudienceRelation.Absent;
        long? expires = null, issued = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var claim = property.Name switch
            {
                "accountId" => WebhookClaim.Account,
                "userId" => WebhookClaim.Member,
                "aud" => WebhookClaim.Audience,
                "exp" => WebhookClaim.Expiry,
                "iat" => WebhookClaim.IssuedAt,
                "shortLivedToken" => WebhookClaim.ShortLivedToken,
                "iss" => WebhookClaim.Issuer,
                "sub" => WebhookClaim.Subject,
                "nbf" => WebhookClaim.NotBefore,
                "jti" => WebhookClaim.TokenId,
                _ => WebhookClaim.Other,
            };
            names.Add(claim);

            switch (claim)
            {
                case WebhookClaim.Audience:
                    audience = RelationOf(property.Value, expectedAudience);
                    break;
                case WebhookClaim.Expiry when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value):
                    expires = value;
                    break;
                case WebhookClaim.IssuedAt when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value):
                    issued = value;
                    break;
            }
        }

        return new ClaimShape(names, audience, expires is { } end && issued is { } start ? end - start : null);
    }

    private static AudienceRelation RelationOf(JsonElement audience, string? expected)
    {
        if (audience.ValueKind != JsonValueKind.String || audience.GetString() is not { Length: > 0 } presented || expected is null)
        {
            return AudienceRelation.Unreadable;
        }

        if (string.Equals(presented, expected, StringComparison.Ordinal))
        {
            return AudienceRelation.Equal;
        }

        if (string.Equals(presented.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.Ordinal))
        {
            return AudienceRelation.EqualIgnoringTrailingSlash;
        }

        return Uri.TryCreate(presented, UriKind.Absolute, out var presentedAddress)
            && Uri.TryCreate(expected, UriKind.Absolute, out var expectedAddress)
            && string.Equals(
                presentedAddress.GetLeftPart(UriPartial.Authority),
                expectedAddress.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase)
            ? AudienceRelation.SameOriginOtherPath
            : AudienceRelation.OtherOrigin;
    }

    // One base64url segment as a JSON document, or null when it is too long, not base64url or not JSON.
    private static JsonDocument? Parse(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment.Length > MaxSegmentLength || !Base64Url.IsValid(segment))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(Base64Url.DecodeFromChars(segment), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static WebhookResponseClass ResponseOf(int status) => status switch
    {
        StatusCodes.Status200OK => WebhookResponseClass.Accepted,
        StatusCodes.Status400BadRequest => WebhookResponseClass.BadRequest,
        StatusCodes.Status401Unauthorized => WebhookResponseClass.Unauthenticated,
        StatusCodes.Status404NotFound => WebhookResponseClass.NotFound,
        StatusCodes.Status413PayloadTooLarge => WebhookResponseClass.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => WebhookResponseClass.UnsupportedMediaType,
        StatusCodes.Status503ServiceUnavailable => WebhookResponseClass.Unavailable,
        _ => WebhookResponseClass.Other,
    };

    private readonly record struct Observation(
        WebhookAuthorizationForm Form,
        WebhookTokenAlgorithm Algorithm,
        WebhookTokenType Type,
        ClaimShape? Claims);

    private sealed record ClaimShape(IReadOnlyList<WebhookClaim> Names, AudienceRelation Audience, long? LifetimeSeconds);
}
