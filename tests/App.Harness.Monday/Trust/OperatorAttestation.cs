namespace WorkContinuity.Harness.Monday.Trust;

/// <summary>
/// The owner's attestation for one standalone run (owner decision B-12): the run is DEV/TEST only, and the person at the
/// console and the browser is the product owner, acting as the configured canonical user for the configured tenant.
/// </summary>
/// <remarks>
/// The harness's console, loopback listener and browser binding are the owner-operated DEV/TEST mechanism of R3B-1, never
/// the production trust model. The standalone entry point shows this statement and starts nothing until the operator types
/// the exact answer; anything else, an empty line or the end of input included, stops the run. The statement names
/// neither the tenant nor the user.
/// </remarks>
public static class OperatorAttestation
{
    /// <summary>The exact line the operator types to attest.</summary>
    public const string Answer = "attest";

    /// <summary>The statement the operator attests to, as the console shows it.</summary>
    public static IReadOnlyList<string> Statement { get; } =
    [
        "harness: DEV/TEST only. The operator's identity provider is '" + HarnessTrust.OperatorProviderKey
            + "', the owner-operated harness: never the production trust model, which Gate 7's Entra sign-in replaces.",
        "harness: type '" + Answer
            + "' to confirm that you are the product owner, acting as the configured canonical user for the configured tenant, for this run only.",
    ];

    /// <summary>Whether a line is the attestation: exactly the answer, nothing more and nothing less.</summary>
    /// <param name="line">The line the operator typed; null at the end of input.</param>
    /// <returns>True only for the exact answer.</returns>
    public static bool IsGiven(string? line) => string.Equals(line, Answer, StringComparison.Ordinal);
}
