using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Extraction;

public enum ExtractionOutcomeStatus
{
    /// <summary>All items accounted for and confidence acceptable.</summary>
    Ok,
    /// <summary>Persist, but flag for a human: count mismatch, low confidence, or a partial chunk failure.</summary>
    NeedsReview,
    /// <summary>Nothing usable was produced (every chunk failed / empty document).</summary>
    Failed
}

/// <summary>
/// Parsed, ready-to-extract view of ONE document. Produced by the document reader and
/// consumed by the extraction service. <see cref="LineItemRegions"/> is the authoritative
/// per-row text (one entry per detected line item) used both to chunk the LLM calls and to
/// assert count conservation. Structured sources also carry <see cref="StructuredRows"/> so
/// they can bypass the LLM entirely via the deterministic normalizer.
/// </summary>
public sealed class DocumentExtractionInput
{
    public long BusinessUnitId { get; init; }
    public string SourceDocumentName { get; init; } = "RFQ document";
    public string SourceId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Header/context text extracted once (buyer, RFQ no, dates, terms).</summary>
    public string HeaderText { get; init; } = "";

    /// <summary>One entry per detected line item. Length is the ground-truth item count.</summary>
    public IReadOnlyList<string> LineItemRegions { get; init; } = Array.Empty<string>();

    /// <summary>True when the document is a structured spreadsheet/CSV and can skip the LLM.</summary>
    public bool IsStructured { get; init; }

    /// <summary>Deterministic-path rows (spreadsheet/CSV). Required when <see cref="IsStructured"/>.</summary>
    public IReadOnlyList<RfqSpreadsheetRow>? StructuredRows { get; init; }
}

public sealed class ChunkedExtractionOutcome
{
    public ExtractionOutcomeStatus Status { get; init; }
    public LeadExtractionResult? Result { get; init; }
    public int ExpectedItemCount { get; init; }
    public int ExtractedItemCount { get; init; }
    public string? ReviewReason { get; init; }
    public List<string> Diagnostics { get; init; } = new();

    /// <summary>
    /// Multi-inquiry auto-split (see <see cref="MultiInquirySplitter"/>): when the
    /// document verifiably contains N distinct inquiries, one result per inquiry group
    /// (2..MaxGroups entries, a strict partition of <see cref="Result"/>'s items). Null
    /// when the document is a single inquiry or the grouping was ambiguous/low-confidence
    /// (fall back to single-lead behavior). <see cref="Result"/> always stays populated
    /// with the merged view for consumers that don't split.
    /// </summary>
    public List<LeadExtractionResult>? SplitResults { get; init; }
}

public interface IChunkedExtractionService
{
    /// <summary>
    /// Route a parsed document to the correct extractor: structured -> deterministic
    /// normalizer (no LLM); otherwise chunked map/reduce over the LLM.
    /// </summary>
    Task<ChunkedExtractionOutcome> ExtractAsync(DocumentExtractionInput input, CancellationToken ct = default);

    /// <summary>Chunked map/reduce extraction for unstructured documents (never truncates).</summary>
    Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(DocumentExtractionInput input, CancellationToken ct = default);

    /// <summary>
    /// Deterministic hook for structured spreadsheets/CSV: parses via
    /// <see cref="ICanonicalRfqNormalizer"/> with full per-field confidence + evidence and
    /// bypasses the LLM entirely. This is the single biggest throughput/cost lever.
    /// </summary>
    Task<ChunkedExtractionOutcome> ExtractStructuredAsync(IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName, CancellationToken ct = default);
}

/// <summary>
/// Chunked map/reduce extraction. Line items are split into bounded chunks
/// (~150–250 items / ~24k chars), each extracted independently via <see cref="ILLMService"/>,
/// then unioned in order. Item-count conservation is asserted (Σ chunk items ==
/// parsed row count) so nothing is ever silently truncated; a mismatch, a partial
/// chunk failure, or low confidence routes the document to NeedsReview instead of
/// being saved as "complete". Per-field confidence is preserved end-to-end.
/// </summary>
public sealed class ChunkedExtractionService : IChunkedExtractionService
{
    private readonly ILLMService _llm;
    private readonly ICanonicalRfqNormalizer _normalizer;
    private readonly ILogger<ChunkedExtractionService> _log;

    // Chunk bounds: cap by item count AND character budget so a chunk fits the model
    // context and stays inside the request timeout. Chunk COUNT scales with the document.
    private const int MaxItemsPerChunk = 200;
    private const int MaxChunkChars = 24_000;
    private const int HeaderContextBudget = 6_000;
    private const double MinAcceptableConfidence = 0.60;

    public ChunkedExtractionService(
        ILLMService llm,
        ICanonicalRfqNormalizer normalizer,
        ILogger<ChunkedExtractionService> log)
    {
        _llm = llm;
        _normalizer = normalizer;
        _log = log;
    }

    public Task<ChunkedExtractionOutcome> ExtractAsync(DocumentExtractionInput input, CancellationToken ct = default)
    {
        if (input.IsStructured && input.StructuredRows is { Count: > 0 })
            return ExtractStructuredAsync(input.StructuredRows, input.BusinessUnitId, input.SourceDocumentName, ct);
        return ExtractUnstructuredAsync(input, ct);
    }

    public async Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(DocumentExtractionInput input, CancellationToken ct = default)
    {
        var expected = input.LineItemRegions.Count;
        var diagnostics = new List<string>();

        if (expected == 0)
        {
            // No detected line-item rows: a single whole-document pass (header + any body).
            var single = await _llm.ExtractLeadDataAsync(
                Clip(input.HeaderText, MaxChunkChars),
                new AiCallContext(input.BusinessUnitId, AiPurposes.RfqExtraction,
                    $"extraction:{input.SourceId}:whole", "rfq-extraction-v1"), ct);
            if (single is null)
                return Failed(0, "LLM returned no result for the document.");
            var items0 = single.Items ?? new List<LeadItemData>();
            var status0 = single.OverallConfidence is < MinAcceptableConfidence
                ? ExtractionOutcomeStatus.NeedsReview
                : ExtractionOutcomeStatus.Ok;

            // Multi-inquiry auto-split — only from a clean (Ok) extraction; an uncertain
            // document is never guess-split (it stays a single NeedsReview lead).
            List<LeadExtractionResult>? split0 = null;
            if (status0 == ExtractionOutcomeStatus.Ok)
            {
                split0 = MultiInquirySplitter.TrySplitByItemGroups(single);
                if (split0 != null)
                    diagnostics.Add($"Multi-inquiry document: split into {split0.Count} inquiry group(s).");
            }

            return new ChunkedExtractionOutcome
            {
                Status = status0,
                Result = single,
                ExpectedItemCount = items0.Count,
                ExtractedItemCount = items0.Count,
                ReviewReason = status0 == ExtractionOutcomeStatus.NeedsReview ? "Overall confidence below threshold." : null,
                Diagnostics = diagnostics,
                SplitResults = split0
            };
        }

        var chunks = BuildChunks(input.LineItemRegions);
        diagnostics.Add($"Document split into {chunks.Count} chunk(s) for {expected} line item(s).");

        var headerContext = Clip(input.HeaderText, HeaderContextBudget);
        var mergedItems = new List<LeadItemData>(expected);
        LeadExtractionResult? headerSource = null;
        var failedChunks = 0;

        // MAP: extract each chunk independently. A failed chunk is recorded (its items are
        // "missing" from the union) rather than silently dropped — the count assert catches it.
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var prompt = BuildChunkText(headerContext, chunks[i]);
            LeadExtractionResult? chunkResult;
            try
            {
                chunkResult = await _llm.ExtractLeadDataAsync(prompt,
                    new AiCallContext(input.BusinessUnitId, AiPurposes.RfqExtraction,
                        $"extraction:{input.SourceId}:chunk:{i + 1}", "rfq-extraction-v1"), ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chunk {Index}/{Total} extraction threw.", i + 1, chunks.Count);
                chunkResult = null;
            }

            if (chunkResult is null)
            {
                failedChunks++;
                diagnostics.Add($"Chunk {i + 1}/{chunks.Count} failed ({chunks[i].Count} item(s) not extracted).");
                continue;
            }

            headerSource ??= chunkResult; // header fields come from the first successful chunk
            if (chunkResult.Items is { Count: > 0 })
                mergedItems.AddRange(chunkResult.Items); // REDUCE: union in order
        }

        if (headerSource is null)
            return Failed(expected, "All chunks failed; no data extracted.");

        // Count conservation: never claim "complete" unless every parsed row was extracted.
        var extracted = mergedItems.Count;
        var overall = ComputeOverallConfidence(headerSource, mergedItems);
        var merged = WithItems(headerSource, mergedItems, overall);

        string? reviewReason = null;
        if (failedChunks > 0)
            reviewReason = $"{failedChunks} chunk(s) failed to extract.";
        else if (extracted != expected)
            reviewReason = $"Item count mismatch: expected {expected}, extracted {extracted}.";
        else if (overall < MinAcceptableConfidence)
            reviewReason = $"Overall confidence {overall:F2} below threshold {MinAcceptableConfidence:F2}.";

        var status = reviewReason is null ? ExtractionOutcomeStatus.Ok : ExtractionOutcomeStatus.NeedsReview;
        if (reviewReason is not null) diagnostics.Add(reviewReason);

        // Multi-inquiry auto-split — only when the extraction is fully clean (all chunks
        // succeeded, counts conserved, confidence acceptable). A NeedsReview document is
        // never guess-split; it keeps today's single flagged lead.
        List<LeadExtractionResult>? splitResults = null;
        if (status == ExtractionOutcomeStatus.Ok)
        {
            splitResults = MultiInquirySplitter.TrySplitByItemGroups(merged);
            if (splitResults != null)
                diagnostics.Add($"Multi-inquiry document: split into {splitResults.Count} inquiry group(s).");
        }

        return new ChunkedExtractionOutcome
        {
            Status = status,
            Result = merged,
            ExpectedItemCount = expected,
            ExtractedItemCount = extracted,
            ReviewReason = reviewReason,
            Diagnostics = diagnostics,
            SplitResults = splitResults
        };
    }

    public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
        IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName, CancellationToken ct = default)
    {
        // Deterministic parse — runs in milliseconds for 10k rows, no LLM call.
        var import = _normalizer.NormalizeSpreadsheetRows(rows, businessUnitId);
        var diagnostics = new List<string>
        {
            $"Structured parse produced {import.Documents.Count} RFQ group(s) from {rows.Count} row(s)."
        };

        var allItems = import.Documents.SelectMany(d => d.LineItems).ToList();
        var expected = allItems.Count;

        if (expected == 0)
            return Task.FromResult(Failed(0, "Structured file contained no valid line items."));

        var primary = import.Documents.First();
        var items = allItems.Select(MapCanonicalItem).ToList();
        var overall = ComputeOverallConfidence(items, header: primary);
        var result = BuildStructuredResult(primary, items, overall);

        var anyNeedsReview = import.Documents.Any(d => d.ValidationStatus != ValidationStatus.Valid)
                             || allItems.Any(i => i.ValidationStatus != ValidationStatus.Valid);

        string? reviewReason = null;
        List<LeadExtractionResult>? splitResults = null;
        if (import.Documents.Count > 1)
        {
            // Multi-inquiry auto-split: only clean (fully valid, confident), fully
            // identified groups are split into per-inquiry results. Anything ambiguous
            // keeps the previous single-lead + NeedsReview behavior — never guess-split.
            if (!anyNeedsReview
                && overall >= MinAcceptableConfidence
                && MultiInquirySplitter.ShouldSplitStructured(import.Documents))
            {
                splitResults = import.Documents
                    .Select(d =>
                    {
                        var groupItems = d.LineItems.Select(MapCanonicalItem).ToList();
                        return BuildStructuredResult(d, groupItems, ComputeOverallConfidence(groupItems, d));
                    })
                    .ToList();
                diagnostics.Add($"Multi-inquiry document: split into {splitResults.Count} RFQ group(s).");
            }
            else
            {
                reviewReason = $"File contains {import.Documents.Count} distinct RFQ groups; review before splitting.";
            }
        }
        else if (anyNeedsReview)
            reviewReason = "One or more fields need review (see canonical validation issues).";
        else if (overall < MinAcceptableConfidence)
            reviewReason = $"Overall confidence {overall:F2} below threshold.";

        var status = reviewReason is null ? ExtractionOutcomeStatus.Ok : ExtractionOutcomeStatus.NeedsReview;
        if (reviewReason is not null) diagnostics.Add(reviewReason);

        return Task.FromResult(new ChunkedExtractionOutcome
        {
            Status = status,
            Result = result,
            ExpectedItemCount = expected,
            ExtractedItemCount = items.Count,
            ReviewReason = reviewReason,
            Diagnostics = diagnostics,
            SplitResults = splitResults
        });
    }

    // ---- chunking --------------------------------------------------------

    private static List<List<string>> BuildChunks(IReadOnlyList<string> regions)
    {
        var chunks = new List<List<string>>();
        var current = new List<string>();
        var currentChars = 0;

        foreach (var region in regions)
        {
            var len = region?.Length ?? 0;
            if (current.Count > 0 && (current.Count >= MaxItemsPerChunk || currentChars + len > MaxChunkChars))
            {
                chunks.Add(current);
                current = new List<string>();
                currentChars = 0;
            }
            current.Add(region ?? "");
            currentChars += len;
        }
        if (current.Count > 0)
            chunks.Add(current);
        return chunks;
    }

    private static string BuildChunkText(string headerContext, List<string> regions)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(headerContext))
            sb.Append("[DOCUMENT HEADER / CONTEXT]\n").Append(headerContext).Append("\n\n");
        sb.Append("[LINE ITEMS — extract EVERY item below, do not skip any]\n");
        for (var i = 0; i < regions.Count; i++)
            sb.Append(regions[i]).Append('\n');
        return sb.ToString();
    }

    private static string Clip(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max));

    // ---- confidence + merge ---------------------------------------------

    private static double ComputeOverallConfidence(LeadExtractionResult header, List<LeadItemData> items)
    {
        var headerConf = new[]
        {
            header.RfqnoConfidence, header.BuyersNameConfidence, header.RecDateConfidence,
            header.BidClosingDateConfidence
        }.Where(c => c.HasValue).Select(c => c!.Value).DefaultIfEmpty(0).Average();
        var itemConf = items.Select(i => i.ItemConfidence ?? 0).DefaultIfEmpty(0).Average();
        return items.Count > 0 ? (headerConf * 0.4) + (itemConf * 0.6) : headerConf;
    }

    private static double ComputeOverallConfidence(List<LeadItemData> items, CanonicalRfqDocument header)
    {
        var headerConf = new[]
        {
            (double)header.RfqNo.Confidence, (double)header.BuyerName.Confidence,
            (double)header.ReceivedDate.Confidence, (double)header.BidClosingDate.Confidence
        }.Average();
        var itemConf = items.Select(i => i.ItemConfidence ?? 0).DefaultIfEmpty(0).Average();
        return items.Count > 0 ? (headerConf * 0.4) + (itemConf * 0.6) : headerConf;
    }

    private static LeadExtractionResult WithItems(LeadExtractionResult header, List<LeadItemData> items, double overall)
        => new(
            header.Rfqno, header.RfqnoConfidence,
            header.BuyersName, header.BuyersNameConfidence,
            header.RecDate, header.RecDateConfidence,
            header.BidClosingDate, header.BidClosingDateConfidence,
            header.BiddingDecision, header.BiddingDecisionConfidence,
            header.AcknowledgmentDate, header.AcknowledgmentDateConfidence,
            header.SubDate, header.SubDateConfidence,
            header.HeaderRemarks, header.HeaderRemarksConfidence,
            header.OpportunityNo, header.OpportunityNoConfidence,
            header.Rfqtype, header.RfqtypeConfidence,
            header.DurationAgreement, header.DurationAgreementConfidence,
            overall,
            items);

    private static LeadExtractionResult BuildStructuredResult(CanonicalRfqDocument doc, List<LeadItemData> items, double overall)
        => new(
            doc.RfqNo.Value, (double)doc.RfqNo.Confidence,
            doc.BuyerName.Value, (double)doc.BuyerName.Confidence,
            FormatDate(doc.ReceivedDate.Value), (double)doc.ReceivedDate.Confidence,
            FormatDate(doc.BidClosingDate.Value), (double)doc.BidClosingDate.Confidence,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            null, 0,
            overall,
            items);

    private static LeadItemData MapCanonicalItem(CanonicalRfqLineItem line)
        => new(
            CompanyRef: null, CompanyRefConfidence: 0,
            CustomerAccountPortalId: null, CustomerAccountPortalIdConfidence: 0,
            CustomerRfqno: null, CustomerRfqnoConfidence: 0,
            ItemMaterialCode: null, ItemMaterialCodeConfidence: 0,
            CommodityProduct: null, CommodityProductConfidence: 0,
            BuyerName: null, BuyerNameConfidence: 0,
            LineItemNo: line.LineItemNo.Value, LineItemNoConfidence: (double)line.LineItemNo.Confidence,
            ProductShortName: line.ProductName.Value, ProductShortNameConfidence: (double)line.ProductName.Confidence,
            Alternative: null, AlternativeConfidence: 0,
            ProductShortDescription: null, ProductShortDescriptionConfidence: 0,
            Currency: line.Currency.Value, CurrencyConfidence: (double)line.Currency.Confidence,
            UnitOfMeasure: null, UnitOfMeasureConfidence: 0,
            UnitPrice: line.UnitPrice.Value == 0 && line.UnitPrice.Confidence == 0 ? null : line.UnitPrice.Value,
            UnitPriceConfidence: (double)line.UnitPrice.Confidence,
            Quantity: line.Quantity.Value, QuantityConfidence: (double)line.Quantity.Confidence,
            StorageLocation: null, StorageLocationConfidence: 0,
            ManufacturerName: line.ManufacturerName.Value, ManufacturerNameConfidence: (double)line.ManufacturerName.Confidence,
            ManufacturerPartNumber: line.ManufacturerPartNumber.Value, ManufacturerPartNumberConfidence: (double)line.ManufacturerPartNumber.Confidence,
            AlternateProductName: null, AlternateProductNameConfidence: 0,
            AlternatePartNumber: null, AlternatePartNumberConfidence: 0,
            ItemText: null, ItemTextConfidence: 0,
            MaterialPotext: null, MaterialPotextConfidence: 0,
            LeadTime: line.LeadTimeDays.Value == 0 && line.LeadTimeDays.Confidence == 0
                ? null : line.LeadTimeDays.Value.ToString(CultureInfo.InvariantCulture),
            LeadTimeConfidence: (double)line.LeadTimeDays.Confidence,
            ReceivedDate: null, ReceivedDateConfidence: 0,
            BidClosingDateLine: null, BidClosingDateLineConfidence: 0,
            ItemConfidence: AverageConfidence(line));

    private static double AverageConfidence(CanonicalRfqLineItem line)
        => new[]
        {
            (double)line.ProductName.Confidence, (double)line.Quantity.Confidence,
            (double)line.UnitPrice.Confidence, (double)line.Currency.Confidence,
            (double)line.ManufacturerName.Confidence, (double)line.ManufacturerPartNumber.Confidence,
            (double)line.LeadTimeDays.Confidence
        }.Average();

    private static string? FormatDate(DateTime? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ChunkedExtractionOutcome Failed(int expected, string reason)
        => new()
        {
            Status = ExtractionOutcomeStatus.Failed,
            Result = null,
            ExpectedItemCount = expected,
            ExtractedItemCount = 0,
            ReviewReason = reason,
            Diagnostics = new List<string> { reason }
        };
}
