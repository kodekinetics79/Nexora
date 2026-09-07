namespace ERP_RFQ_Automation.AI;

/// <summary>
/// The allowance an operator sets, in the unit they can actually reason about.
///
/// <para><b>Why documents, not tokens.</b> The ledger enforces a TOKEN ceiling, because tokens are
/// the only unit available at the moment of enforcement — estimated before the call, actual after,
/// no lookup required. But nobody sells tokens. A construction buyer signs for "500 RFQs a month";
/// an operator asked for "20,000,000 tokens" has no way to tell whether that is generous or about
/// to cut the customer off mid-quarter, so they type a number that looks big and move on. That is
/// how a tenant ends up either uncapped or capped at something meaningless.</para>
///
/// <para>So the console asks for documents and converts here — one conversion, on the server, used
/// by both the screen and anything that quotes an allowance, because a console that converts with
/// its own constant will drift from the ledger the first time either is edited.</para>
/// </summary>
public static class AiAllowance
{
    /// <summary>
    /// Tokens a governed document is budgeted at.
    ///
    /// <para>Derived from the measured shape of a real extraction, not guessed: roughly a
    /// 2,000-token prompt, plus the document text, plus about 225 output tokens per line item,
    /// with the header re-sent once per chunk. Across 147 genuine Aramco bid lists the average was
    /// 4.8 line items per document, which puts a typical document near 4,400 tokens — but a
    /// ceiling has to survive the tail, and the largest of those documents carries 90 lines
    /// (~47,000 tokens). 12,000 sits deliberately above the average so an allowance is not
    /// exhausted by a handful of long bid lists, and well below the worst case so the number still
    /// means something.</para>
    ///
    /// <para><b>It is a budgeting estimate and nothing else.</b> Enforcement always uses the real
    /// token count the ledger reserves and settles; this figure never decides whether a single
    /// call is allowed.</para>
    /// </summary>
    public const long TokensPerDocument = 12_000;

    /// <summary>
    /// The token ceiling for a monthly document allowance, or null for "no ceiling" — which the
    /// caller must have chosen deliberately, never inferred from an omitted field.
    /// </summary>
    public static long? TokensForDocuments(int? documentsPerMonth) =>
        documentsPerMonth is > 0 ? documentsPerMonth.Value * TokensPerDocument : null;

    /// <summary>
    /// The documents a token ceiling represents, for showing an existing tenant's limit back in the
    /// unit it was set in. Rounds DOWN: an allowance that reads as more documents than it can
    /// actually pay for is the one direction that produces a surprise.
    /// </summary>
    public static int? DocumentsForTokens(long? monthlyHardTokenLimit) =>
        monthlyHardTokenLimit is > 0
            ? (int)(monthlyHardTokenLimit.Value / TokensPerDocument)
            : null;

    /// <summary>
    /// Presets the console offers. Small enough to pick from, spaced so the next one up is a real
    /// step rather than a nudge.
    /// </summary>
    public static readonly int[] DocumentPresets = [100, 500, 2_000, 10_000];
}
