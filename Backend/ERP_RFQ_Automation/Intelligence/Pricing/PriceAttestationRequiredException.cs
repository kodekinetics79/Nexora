using System.Security.Cryptography;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// Thrown when a priced customer document is about to be produced for a quote whose current
/// prices are not covered by a recorded price attestation (R5).
///
/// <para>A distinct type rather than <see cref="InvalidOperationException"/> because two
/// callers have to tell this failure apart from every other refusal: the PDF endpoint maps
/// it to 409 with the rep-facing reason, and the quote-delivery dispatcher must NOT retry it
/// — a price that changed after it was attested will still have changed on the eighth
/// attempt, so retrying only keeps a stale send alive in the outbox.</para>
///
/// <para><see cref="Message"/> is customer-neutral, plain commercial language produced by
/// <see cref="PriceAttestationService"/> or by the binding check below, and is intended to
/// be shown to the rep verbatim.</para>
/// </summary>
public sealed class PriceAttestationRequiredException : Exception
{
    public PriceAttestationRequiredException(string message) : base(message) { }

    /// <summary>
    /// true when the quote IS attested but the attestation no longer covers the exact priced
    /// content this dispatch was authorised for — i.e. the time-of-check/time-of-use window
    /// between queueing the email and sending it was used to change a price.
    /// </summary>
    public bool BindingBroken { get; init; }
}

/// <summary>
/// Constant-time comparison of two price fingerprints.
///
/// <para>Mirrors <c>FileController.DigestMatches</c> exactly: the values are compared with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// so the check leaks nothing through timing, and a recorded value that is not a well-formed
/// lowercase SHA-256 is treated as a FAILURE rather than as "nothing to check" — a column
/// that can be filled with junk to disable verification is not a control.</para>
/// </summary>
public static class PriceFingerprint
{
    public static bool Matches(string? observed, string? recorded)
    {
        var actual = observed?.Trim().ToLowerInvariant();
        var expected = recorded?.Trim().ToLowerInvariant();
        if (actual is not { Length: 64 } || !actual.All(Uri.IsHexDigit)) return false;
        if (expected is not { Length: 64 } || !expected.All(Uri.IsHexDigit)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual), Convert.FromHexString(expected));
    }
}
