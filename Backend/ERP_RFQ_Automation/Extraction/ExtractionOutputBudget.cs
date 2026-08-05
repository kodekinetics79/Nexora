using System;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// How many line items may be asked for in ONE extraction call.
///
/// PROD ROOT CAUSE (2026-08-05). Chunking used to be sized purely by INPUT size
/// (≤200 items / ≤24,000 characters). But the extraction prompt does not ask for a
/// compact echo of the input — it asks for a fully expanded, per-field-confidence JSON
/// object per line item. The OUTPUT is therefore many times larger than the input, and
/// it is the output that has the hard ceiling (Ollama <c>num_predict</c>). Measured live
/// against ollama.com/deepseek-v4-pro with a 40-line-item RFQ: num_predict=4096 and
/// num_predict=8192 both returned <c>done_reason="length"</c> with eval_count pinned at
/// the ceiling and JSON cut mid-object, so ParseJsonResponse returned null, every chunk
/// "failed", and the document dead-lettered with "All chunks failed; no data extracted."
/// A 3-item document returned perfect schema-conformant JSON — the model was never the
/// problem, the ASK was too big.
///
/// ---- Derivation of <see cref="EstimatedOutputTokensPerItem"/> ------------------------
/// Counted from the item schema that <c>OllamaLlmService.BuildExtractionInstructions()</c>
/// actually requires (keep these in step if the schema ever changes):
///   * 24 value fields (CompanyRef … BidClosingDateLine)
///   * 24 matching "&lt;Field&gt;Confidence" numbers — one per value field
///   * 4 more keys: ItemConfidence, ExtraFields, InquiryGroup, InquiryGroupConfidence
///   => 52 JSON keys per item.
/// Character cost of one item, at the ~10-character average value length the schema
/// produces (rule 3 forbids omitting fields — missing values are emitted as explicit
/// null, so the KEYS dominate and the estimate barely moves with document density):
///   keys, with quotes + colon .... 1,129 chars
///   values ......................... 352 chars
///   separators + braces ............  54 chars
///   => ~1,535 characters per item.
/// Dense JSON with PascalCase identifiers tokenizes at roughly 3.5 characters/token
/// (~3.0 pessimistic, ~4.0 optimistic), so 1,535 / 3.5 ≈ 439 tokens. Rounded UP to 450
/// so the constant errs toward smaller, safer chunks.
///
/// <see cref="EstimatedHeaderOutputTokens"/> is the same arithmetic over the 26
/// document-level keys (Rfqno … InquiryTypeConfidence, plus the "Items" wrapper), whose
/// HeaderRemarks string is the only long value: ~832 characters ≈ 240 tokens, rounded up
/// to 300.
///
/// <see cref="SafetyUtilization"/> keeps the projection at 70% of the ceiling. That head
/// room absorbs the cases the average cannot: unusually verbose ItemText /
/// ProductShortDescription values, a populated ExtraFields map, and tokenizer variance.
///
/// Resulting chunk sizes: 5 items at a 4,096-token ceiling, 12 items at 8,192.
/// </summary>
public static class ExtractionOutputBudget
{
    /// <summary>Estimated OUTPUT tokens the schema costs for one extracted line item.</summary>
    public const int EstimatedOutputTokensPerItem = 450;

    /// <summary>Estimated OUTPUT tokens for the document-level header fields emitted once per call.</summary>
    public const int EstimatedHeaderOutputTokens = 300;

    /// <summary>
    /// Fraction of the provider's output ceiling this budget is willing to project into.
    /// Deliberately well under 1.0: an over-estimate costs one extra chunk, an
    /// under-estimate costs the whole document.
    /// </summary>
    public const double SafetyUtilization = 0.70;

    /// <summary>
    /// Hard ceiling on items per chunk regardless of how generous the output budget gets —
    /// the SECOND constraint (input size, request timeout, blast radius of one failed
    /// chunk) does not disappear just because the completion budget grew.
    /// </summary>
    public const int AbsoluteMaxItemsPerChunk = 200;

    /// <summary>
    /// Largest number of line items whose projected output still fits inside
    /// <paramref name="maximumOutputTokens"/> with the safety margin applied.
    /// Never returns less than 1: a single item is the smallest indivisible request, and if
    /// even that does not fit the caller must fail that item honestly rather than loop.
    /// </summary>
    public static int MaxItemsPerChunk(int maximumOutputTokens)
    {
        var usable = (maximumOutputTokens * SafetyUtilization) - EstimatedHeaderOutputTokens;
        if (usable < EstimatedOutputTokensPerItem)
            return 1;
        var items = (int)(usable / EstimatedOutputTokensPerItem);
        return Math.Clamp(items, 1, AbsoluteMaxItemsPerChunk);
    }

    /// <summary>Projected output tokens for a chunk carrying <paramref name="itemsInChunk"/> items.</summary>
    public static int ProjectedOutputTokens(int itemsInChunk)
        => EstimatedHeaderOutputTokens + (Math.Max(0, itemsInChunk) * EstimatedOutputTokensPerItem);

    /// <summary>True when a chunk of this size is projected to fit the ceiling with margin.</summary>
    public static bool FitsBudget(int itemsInChunk, int maximumOutputTokens)
        => ProjectedOutputTokens(itemsInChunk) <= maximumOutputTokens * SafetyUtilization;
}
