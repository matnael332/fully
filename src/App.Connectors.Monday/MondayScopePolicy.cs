using System.Collections.Frozen;
using WorkContinuity.Core.Connectors;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// Which monday OAuth scopes the connector may request and accept. Gate 3 reads and ingests only.
/// </summary>
/// <remarks>
/// <para>
/// The allowlist is every scope monday's authorization server metadata lists that ends in <c>:read</c> (reverified on
/// 2026-09-30). No <c>:write</c> scope, no <c>ai:consume</c> and no unknown scope can be configured, and a grant that
/// contains one is refused, so no Gate 3 connection can hold a write permission even when the monday app is configured
/// with one. The requested scopes come from server configuration, never from the browser; the default is the minimum
/// the connector needs: <c>me:read</c> to verify the delegating user and <c>boards:read</c> for boards and items.
/// </para>
/// </remarks>
public static class MondayScopePolicy
{
    /// <summary>The minimum scopes, requested unless configuration names others.</summary>
    public const string DefaultScopes = "boards:read me:read";

    /// <summary>Gets the scopes a connection may hold: monday's documented read scopes only.</summary>
    public static IReadOnlySet<string> Allowed { get; } = new[]
    {
        "account:read", "apps:read", "assets:read", "boards:read", "departments:read", "docs:read", "items:read",
        "me:read", "tags:read", "teams:read", "updates:read", "users:read", "webhooks:read", "workspaces:read",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Gets the scopes every connection needs: the identity check and the board reads.</summary>
    public static IReadOnlySet<string> Required { get; } =
        new[] { "boards:read", "me:read" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether a configured scope list may be requested: well formed, allowlisted, and holding the required scopes.</summary>
    public static bool IsPermittedRequest(string? scopes) => IsPermitted(scopes);

    /// <summary>Whether a granted scope list may be accepted, by the same rule as a request.</summary>
    public static bool IsPermittedGrant(string? scopes) => IsPermitted(scopes);

    /// <summary>
    /// Reads the <c>scope</c> of a token response into the canonical list: names separated by one or more spaces, each
    /// kept once and ordered. Returns <see langword="null"/> when it is not a well-formed list.
    /// </summary>
    internal static string? CanonicalGrant(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > ConnectorScopes.MaxLength)
        {
            return null;
        }

        try
        {
            return ConnectorScopes.Normalize(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsPermitted(string? scopes)
    {
        if (!ConnectorScopes.IsWellFormed(scopes))
        {
            return false;
        }

        var names = ConnectorScopes.Parse(scopes!);
        return names.All(Allowed.Contains) && Required.All(names.Contains);
    }
}
