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
/// CUSTOMER DELIVERY AND AGREEMENT TERMS add 6 more header keys (three values and three
/// confidences). Their keys, values and separators cost about 240 characters, or 69 tokens
/// at the same 3.5 characters/token. 550 + 69 = 619, rounded UP to 650.
///
/// COST OF THAT CHOICE, stated plainly: the header is now worth about three line items
/// (650 / 225), which is exactly why these fields are HEADER fields. Making
/// any of them per-item would cost ~225 tokens PER LINE and give the budget straight back.
///
/// <see cref="SafetyUtilization"/> keeps the projection at 70% of the ceiling. That head
/// room absorbs the cases the average cannot: unusually verbose ItemText /
/// ProductShortDescription values, a populated ExtraFields map, and tokenizer variance.
///
/// RE-DERIVED FROM THE LEDGER (2026-09-28). The 225-token arithmetic above assumed ~10
/// characters per value. Real bid lines are specification-heavy: the AiRequests ledger for
/// the Aramco and Marafiq PDFs shows 1,962 output tokens for 3 items plus the header and
/// 1,101–1,184 tokens for 1 item plus the header, i.e. ~440–530 tokens per item. With 225 the
/// planner packed 3 items into a 2,048-token call that needed ~2,000, and 47 of 48 calls on one
/// document were cut off. <see cref="EstimatedOutputTokensPerItem"/> is now 450 — the measured
/// cost before rule 17 of rfq-extraction-v8 (omit null keys) lowered it, so the estimate errs
/// toward smaller, safer chunks exactly as the original derivation intended.
///
/// Resulting chunk sizes: 4 items at a 4,096-token ceiling, 11 items at 8,192.
///
/// INPUT is the second budget. A call must also fit the provider's context window: instructions
/// + document + the whole output ceiling. <see cref="MaxDocumentCharacters"/> turns the
/// client's <c>MaxDocumentInputTokens</c> into characters at
/// <see cref="DocumentCharactersPerToken"/>, pessimistic on purpose because part numbers and
/// material codes tokenize digit by digit.
/// </summary>
public static class ExtractionOutputBudget
{
    /// <summary>Estimated OUTPUT tokens the schema costs for one extracted line item.</summary>
    public const int EstimatedOutputTokensPerItem = 450;

    /// <summary>Estimated OUTPUT tokens for the document-level header fields emitted once per call.</summary>
    public const int EstimatedHeaderOutputTokens = 650;

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

    /// <summary>
    /// Characters of document text per token, pessimistic. Prose runs ~4; SAP prints full of
    /// material codes and part numbers run far lower, because digits tokenize one by one.
    /// </summary>
    public const double DocumentCharactersPerToken = 2.5;

    /// <summary>
    /// Most characters of document text (header context + regions) one call may carry. The
    /// smaller of <paramref name="characterCeiling"/> and what
    /// <paramref name="maximumDocumentInputTokens"/> allows; the ceiling alone when the client
    /// knows no context window. Never below 2,000, so one ordinary page always fits.
    /// </summary>
    public static int MaxDocumentCharacters(int? maximumDocumentInputTokens, int characterCeiling)
    {
        if (maximumDocumentInputTokens is not { } tokens || tokens <= 0) return characterCeiling;
        var fromWindow = (int)Math.Floor(tokens * DocumentCharactersPerToken);
        return Math.Max(2_000, Math.Min(characterCeiling, fromWindow));
    }

    /// <summary>Projected output tokens for a chunk carrying <paramref name="itemsInChunk"/> items.</summary>
    public static int ProjectedOutputTokens(int itemsInChunk)
        => EstimatedHeaderOutputTokens + (Math.Max(0, itemsInChunk) * EstimatedOutputTokensPerItem);

    /// <summary>True when a chunk of this size is projected to fit the ceiling with margin.</summary>
    public static bool FitsBudget(int itemsInChunk, int maximumOutputTokens)
        => ProjectedOutputTokens(itemsInChunk) <= maximumOutputTokens * SafetyUtilization;
}
