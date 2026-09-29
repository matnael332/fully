namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The monday API version this connector is built and tested against, and the header that selects it.
/// </summary>
/// <remarks>
/// <para>
/// monday releases an API version every quarter: a release candidate becomes Current three months later and moves to
/// maintenance three months after that, so a version stays stable for at least six months. Without the header monday
/// applies whatever version is Current at the time, which moves underneath a client. This connector therefore sends
/// its pinned version on every request.
/// </para>
/// <para>
/// <see cref="Pinned"/> is <c>2026-07</c>, monday's Current version when Gate 3 was authorized (verified against the
/// official versioning documentation on 2026-09-29; <c>2026-10</c> was then the release candidate). This is the only
/// place the version is written, and an architecture test keeps it that way. Moving to a newer version is a
/// deliberate change of this constant, reviewed against monday's migration notes and proven by the connector tests;
/// it never happens implicitly.
/// </para>
/// </remarks>
public static class MondayApiVersion
{
    /// <summary>The pinned monday API version.</summary>
    public const string Pinned = "2026-07";

    /// <summary>The request header that selects the API version.</summary>
    public const string HeaderName = "API-Version";
}
