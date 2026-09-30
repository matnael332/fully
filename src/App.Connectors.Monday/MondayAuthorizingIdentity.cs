namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The monday user and account an access token acts for, as monday's <c>me</c> query reports them.
/// </summary>
/// <remarks>
/// Both are monday's stable numeric ids, never an email address or a name (invariant 47). The user id becomes the
/// subject of the connection's delegated identity (decision D3B-1); the string form names neither.
/// </remarks>
internal sealed record MondayAuthorizingIdentity(string UserId, string AccountId)
{
    /// <summary>Identifies the type without the ids.</summary>
    public override string ToString() => "MondayAuthorizingIdentity(ids redacted)";
}
