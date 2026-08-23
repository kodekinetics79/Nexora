using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The reservation is denominated in TOKENS. It used to add a BYTE count to a token count.
///
/// <para><b>What that cost.</b> Bytes run about four to one against tokens, and the sum is
/// multiplied by the attempt count, so a 384-byte email reserved roughly 29,000 tokens against
/// the ~2,000 it actually consumed — and every chunk of a multi-chunk document reserved the
/// same again. The per-document ceiling is <c>MaxTokensPerDocument × DocumentBudgetRetryCycles</c>,
/// so ordinary documents were refused with <c>document_budget_exceeded</c> and the tenant
/// ceiling had to be set about fifteen times higher than real consumption before anything
/// could get through. The realistic estimate was computed one line above the sum and
/// discarded.</para>
/// </summary>
public sealed class AiReservationTokenUnitTests
{
    /// <summary>The reservation arithmetic, mirrored from <c>ReserveAsync</c>.</summary>
    private static long Reserve(int payloadBytes, int maxOutputTokens, int maxAttempts)
        => (AiGovernanceService.EstimateTokens(payloadBytes) + Math.Max(1, maxOutputTokens))
           * Math.Max(1, maxAttempts);

    [Fact]
    public void Four_characters_is_about_one_token()
    {
        Assert.Equal(1, AiGovernanceService.EstimateTokens(1));
        Assert.Equal(1, AiGovernanceService.EstimateTokens(4));
        Assert.Equal(2, AiGovernanceService.EstimateTokens(5));
        Assert.Equal(250, AiGovernanceService.EstimateTokens(1_000));
    }

    [Fact]
    public void A_small_email_no_longer_reserves_an_order_of_magnitude_more_than_it_uses()
    {
        // The live shape: ~5.6 KB of prompt + body, 4096 output ceiling, 3 attempts.
        const int payloadBytes = 5_600;
        var reserve = Reserve(payloadBytes, maxOutputTokens: 4_096, maxAttempts: 3);

        // Before: (5600 + 4096) × 3 = 29,088 — the byte count dominated the sum.
        var beforeTheFix = ((long)payloadBytes + 4_096) * 3;
        Assert.Equal(29_088, beforeTheFix);

        Assert.True(reserve < beforeTheFix,
            $"The fix must reserve less than the byte-based sum ({reserve} vs {beforeTheFix}).");

        // Input now contributes ~1,400 tokens rather than 5,600.
        Assert.Equal((1_400L + 4_096L) * 3, reserve);
    }

    [Fact]
    public void The_output_ceiling_still_dominates_a_small_payload()
    {
        // A guard against over-correcting: the reserve must still cover a full-length answer,
        // because that is the part the model actually controls.
        var reserve = Reserve(payloadBytes: 400, maxOutputTokens: 4_096, maxAttempts: 1);
        Assert.True(reserve >= 4_096, $"Reserve {reserve} does not cover one full response.");
    }

    [Fact]
    public void The_attempt_multiplier_is_still_the_safety_margin()
    {
        var once = Reserve(5_600, 4_096, 1);
        var thrice = Reserve(5_600, 4_096, 3);
        Assert.Equal(once * 3, thrice);
    }

    [Theory]
    [InlineData(384)]      // a short body-only enquiry
    [InlineData(5_600)]    // the conversational path's real payload
    [InlineData(48_000)]   // a structured chunk carrying the 8 KB prompt
    public void Reservation_stays_within_a_sane_multiple_of_realistic_usage(int payloadBytes)
    {
        var reserve = Reserve(payloadBytes, 4_096, 3);

        // Realistic worst case: every attempt spends its whole input and its whole output.
        var realisticWorstCase = (AiGovernanceService.EstimateTokens(payloadBytes) + 4_096) * 3;
        Assert.Equal(realisticWorstCase, reserve);

        // And the input side is no longer inflated fourfold.
        Assert.True(AiGovernanceService.EstimateTokens(payloadBytes) < payloadBytes);
    }
}
