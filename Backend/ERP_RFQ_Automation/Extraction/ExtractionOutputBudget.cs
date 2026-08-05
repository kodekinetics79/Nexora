using System;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// How many line items may be asked for in ONE extraction call.
///
/// PROD ROOT CAUSE (2026-08-05). Chunking used to be sized purely by INPUT size
/// (≤200 items / ≤24,000 characters). But the extraction prompt does not ask for a
/// compact echo of the input — it asks for a fully expanded, 28-key JSON
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
///   * 4 more keys: ItemConfidence, ExtraFields, InquiryGroup, InquiryGroupConfidence
///   => 28 JSON keys per item.
/// Character cost of one item, at the ~10-character average value length the schema
/// produces (rule 3 forbids omitting fields — missing values are emitted as explicit
/// null, so the KEYS still dominate and the estimate moves only modestly with density):
///   keys, with quotes + colon ...... 480 chars (396 of key name + 28 × 3)
///   values ......................... 256 chars (24 values ≈ 10 chars, 4 short tails)
///   separators + braces .............. 30 chars
///   => ~766 characters per item.
/// Dense JSON with PascalCase identifiers tokenizes at roughly 3.5 characters/token
/// (~3.0 pessimistic, ~4.0 optimistic), so 766 / 3.5 ≈ 219 tokens. Rounded UP to 225
/// so the constant errs toward smaller, safer chunks. Tokenizer variance is absorbed by
/// <see cref="SafetyUtilization"/>, exactly as it was before.
///
/// WHY 225 AND NOT 450 (2026-08-05). The item schema used to demand 24 additional
/// "&lt;Field&gt;Confidence" numbers — one per value field, 52 keys per item, ~1,535
/// characters ≈ 439 tokens, rounded to 450. Those numbers were never persisted: the
/// LeadItem row has ONE confidence column (<c>Aiconfidence</c>) and
/// <c>ExtractionWorker.BuildLead</c> reads exactly one value, <c>ItemConfidence</c>.
/// Roughly half of every item's output budget was therefore spent generating numbers that
/// were parsed and dropped — and that spend is what forced chunks down to 11 items and
/// produced the OutputTruncated / "All chunks failed" failures this file exists to fight.
/// Removing them (prompt version rfq-extraction-v2) roughly DOUBLES the items per chunk:
/// 5 -> 10 at a 4,096-token ceiling, 11 -> 23 at 8,192. Header-level confidences are
/// untouched — they are paid once per document, and OverallConfidence gates ingestion.
/// The one honest caveat: with the key overhead gone, values are now 33% of the character
/// budget instead of 23%, so a chunk of unusually verbose ItemText /
/// ProductShortDescription lines drifts from the average a little faster than before. The
/// 30% head room below is what absorbs that, and truncation is still corrected by
/// re-splitting rather than by a replay.
///
/// <see cref="EstimatedHeaderOutputTokens"/> is the same arithmetic over the document-level
/// keys. It was 300 for the original 26 keys (Rfqno … InquiryTypeConfidence, plus the
/// "Items" wrapper), whose HeaderRemarks string is the only long value: ~832 characters
/// ≈ 240 tokens, rounded up to 300.
///
/// CLIENT ORGANISATION IDENTITY added 13 more header keys (CustomerCompanyName …
/// SupplierAccountRefOnDocumentConfidence — see OllamaLlmService.BuildExtractionInstructions).
/// Same arithmetic, same 3.5 characters/token:
///   keys, with quotes + colon .... ~505 chars (13 keys averaging ~36 characters)
///   values ....................... ~275 chars (7 string values, 6 confidence numbers;
///                                  CustomerCompanyEvidence alone is capped at 120)
///   separators ...................  ~26 chars
///   => ~806 characters ≈ 230 tokens.
/// 300 + 230 = 530, rounded UP to 550 so the constant errs toward smaller, safer chunks.
///
/// COST OF THAT CHOICE, stated plainly: the header is now worth two line items rather
/// than one (550 / 225), which is exactly why the client fields are HEADER fields. Making
/// any of them per-item would cost ~225 tokens PER LINE and give the budget straight back.
///
/// <see cref="SafetyUtilization"/> keeps the projection at 70% of the ceiling. That head
/// room absorbs the cases the average cannot: unusually verbose ItemText /
/// ProductShortDescription values, a populated ExtraFields map, and tokenizer variance.
///
/// Resulting chunk sizes: 10 items at a 4,096-token ceiling, 23 items at 8,192.
/// </summary>
public static class ExtractionOutputBudget
{
    /// <summary>Estimated OUTPUT tokens the schema costs for one extracted line item.</summary>
    public const int EstimatedOutputTokensPerItem = 225;

    /// <summary>Estimated OUTPUT tokens for the document-level header fields emitted once per call.</summary>
    public const int EstimatedHeaderOutputTokens = 550;

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
