using System.Collections.Concurrent;
using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Templates;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Services.QuoteTerms;

/// <summary>
/// The buyer's commercial terms that decide a quote's defaults and its send warnings.
///
/// <para>No quote column holds these yet (Incoterm, payment, agreement need a migration). They are
/// read on demand from the retained RFQ document — the same verified bytes the Decide terms panel
/// reads — so every lead already on file has them, and the quote document can print them from
/// here (<see cref="IBuyerQuoteTermsService.ForQuoteAsync"/>).</para>
/// </summary>
public sealed record BuyerQuoteTerms(
    BuyerValidityRule? Validity,
    IReadOnlyList<string> AllowedCurrencies,
    string? CurrencySentence,
    string? DeliveryTerms,
    string? DeliverTo,
    string? Agreement,
    string? Payment,
    string? SourceFileName)
{
    public static readonly BuyerQuoteTerms None = new(null, Array.Empty<string>(), null, null, null, null, null, null);

    public bool IsEmpty => Validity is null && AllowedCurrencies.Count == 0 && DeliveryTerms is null
        && DeliverTo is null && Agreement is null && Payment is null;
}

public interface IBuyerQuoteTermsService
{
    /// <summary>The terms stated in the lead's RFQ documents, newest document first. Never throws for
    /// an unreadable document: it contributes nothing and the quote keeps the ordinary defaults.</summary>
    Task<BuyerQuoteTerms> ForLeadAsync(long businessUnitId, long leadId, CancellationToken ct = default);

    /// <summary>The terms for the quote's RFQ lead, with the RFQ's own delivery point as the fallback
    /// "deliver to". <see cref="BuyerQuoteTerms.None"/> for a quote with no lead.</summary>
    Task<BuyerQuoteTerms> ForQuoteAsync(long businessUnitId, long quoteId, CancellationToken ct = default);
}

public sealed class BuyerQuoteTermsService : IBuyerQuoteTermsService
{
    private const int CacheCap = 500;
    private const int PdfPagesRead = 15;
    private static readonly ConcurrentDictionary<string, BuyerQuoteTerms> ByContentHash = new();

    private readonly ErpRfqAutomationContext _context;
    private readonly IEvidenceObjectStorage _storage;
    private readonly ILogger<BuyerQuoteTermsService>? _logger;

    public BuyerQuoteTermsService(ErpRfqAutomationContext context, IEvidenceObjectStorage storage,
        ILogger<BuyerQuoteTermsService>? logger = null)
    {
        _context = context;
        _storage = storage;
        _logger = logger;
    }

    public async Task<BuyerQuoteTerms> ForQuoteAsync(long businessUnitId, long quoteId, CancellationToken ct = default)
    {
        var rfq = await _context.Quotes.AsNoTracking()
            .Where(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId && q.Rfq != null)
            .Select(q => new { q.Rfq!.LeadId, q.Rfq.DeliveryLocation })
            .SingleOrDefaultAsync(ct);
        if (rfq is null) return BuyerQuoteTerms.None;
        var terms = rfq.LeadId is long leadId ? await ForLeadAsync(businessUnitId, leadId, ct) : BuyerQuoteTerms.None;
        return terms.DeliverTo is null && !string.IsNullOrWhiteSpace(rfq.DeliveryLocation)
            ? terms with { DeliverTo = rfq.DeliveryLocation.Trim() }
            : terms;
    }

    public async Task<BuyerQuoteTerms> ForLeadAsync(long businessUnitId, long leadId, CancellationToken ct = default)
    {
        try
        {
            var documentIds = await _context.Set<LeadIngestionOccurrence>().AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId && o.LeadId == leadId && o.SourceDocumentId != null)
                .OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id)
                .Select(o => o.SourceDocumentId!.Value)
                .ToListAsync(ct);
            var linked = await _context.Set<LeadOccurrenceDocument>().AsNoTracking()
                .Where(d => d.BusinessUnitId == businessUnitId && d.Occurrence.LeadId == leadId)
                .OrderByDescending(d => d.LinkedAtUtc).ThenByDescending(d => d.Id)
                .Select(d => d.SourceDocumentId)
                .ToListAsync(ct);
            var ordered = documentIds.Concat(linked).Distinct().Take(6).ToList();

            var merged = BuyerQuoteTerms.None;
            foreach (var documentId in ordered)
            {
                var found = await ReadDocumentAsync(businessUnitId, documentId, ct);
                merged = Merge(merged, found);
            }
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Buyer terms could not be read for lead {LeadId}.", leadId);
            return BuyerQuoteTerms.None;
        }
    }

    /// <summary>Newer document first: a field already found is kept, a missing one is filled.</summary>
    internal static BuyerQuoteTerms Merge(BuyerQuoteTerms newer, BuyerQuoteTerms older) => new(
        newer.Validity ?? older.Validity,
        newer.AllowedCurrencies.Count > 0 ? newer.AllowedCurrencies : older.AllowedCurrencies,
        newer.AllowedCurrencies.Count > 0 ? newer.CurrencySentence : older.CurrencySentence,
        newer.DeliveryTerms ?? older.DeliveryTerms,
        newer.DeliverTo ?? older.DeliverTo,
        newer.Agreement ?? older.Agreement,
        newer.Payment ?? older.Payment,
        newer.SourceFileName ?? older.SourceFileName);

    private async Task<BuyerQuoteTerms> ReadDocumentAsync(long businessUnitId, long sourceDocumentId, CancellationToken ct)
    {
        var document = await _context.Set<SourceDocument>().AsNoTracking()
            .SingleOrDefaultAsync(d => d.BusinessUnitId == businessUnitId && d.Id == sourceDocumentId, ct);
        if (document is null || document.PurgeState != EvidencePurgeState.Present
            || document.SecurityStatus != DocumentSecurityStatus.Cleared || !document.ExtractionJobId.HasValue)
            return BuyerQuoteTerms.None;
        var kind = KindOf(document.OriginalFileName, document.DetectedMimeType);
        if (kind is null) return BuyerQuoteTerms.None;
        if (ByContentHash.TryGetValue(document.ContentHash, out var cached)) return cached;

        var storagePath = await _context.Set<ExtractionJob>().AsNoTracking()
            .Where(j => j.BusinessUnitId == businessUnitId && j.Id == document.ExtractionJobId.Value
                && j.ContentHash == document.ContentHash)
            .Select(j => j.StoragePath)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(storagePath)) return BuyerQuoteTerms.None;

        BuyerQuoteTerms terms;
        try
        {
            byte[] bytes;
            await using (var stream = await _storage.OpenVerifiedReadAsync(storagePath, document.ContentHash, ct))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }
            terms = Read(kind, bytes, document.OriginalFileName, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Buyer terms could not be read from source document {SourceDocumentId}.", sourceDocumentId);
            terms = BuyerQuoteTerms.None;
        }
        if (ByContentHash.Count >= CacheCap) ByContentHash.Clear();
        ByContentHash[document.ContentHash] = terms;
        return terms;
    }

    private static string? KindOf(string? fileName, string? mime)
    {
        var name = (fileName ?? string.Empty).ToLowerInvariant();
        var type = (mime ?? string.Empty).ToLowerInvariant();
        if (name.EndsWith(".docx") || type.Contains("wordprocessingml")) return "docx";
        if (name.EndsWith(".doc") || type.Contains("msword")) return "doc";
        if (name.EndsWith(".pdf") || type.Contains("pdf")) return "pdf";
        return null;
    }

    /// <summary>The terms one document states. Pure over the bytes.</summary>
    internal static BuyerQuoteTerms Read(string kind, byte[] bytes, string? fileName, ILogger? logger = null)
    {
        string text;
        IReadOnlyList<BuyerTerms.Term> tableTerms = Array.Empty<BuyerTerms.Term>();
        // An SAP Ariba event print saved as ".doc" is HTML ("<!-- class: ariba.sourcing.rfxui…"),
        // which is how every SEC RFP arrives. The Word reader sees nothing in it.
        if (LooksLikeHtml(bytes)) kind = "html";
        switch (kind)
        {
            case "docx":
                var grids = DocxTableParser.ReadLeadingGrids(bytes, BuyerTerms.LeadingRows);
                tableTerms = BuyerTerms.Read(grids, fileName);
                text = string.Join("\n", grids.SelectMany(grid => grid)
                    .Select(row => string.Join(" ", row.Where(c => !string.IsNullOrWhiteSpace(c)))));
                break;
            case "doc":
                text = WordBinaryTextExtractor.Extract(bytes, logger);
                break;
            case "html":
                text = HtmlText(Encoding.UTF8.GetString(bytes));
                break;
            case "pdf":
                var builder = new StringBuilder();
                using (var pdf = PdfDocument.Open(bytes))
                    foreach (var page in pdf.GetPages().Take(PdfPagesRead))
                        builder.AppendLine(page.Text);
                text = builder.ToString();
                break;
            default:
                return BuyerQuoteTerms.None;
        }
        return FromTermsAndText(tableTerms, text, fileName);
    }

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return head.StartsWith("<!--", StringComparison.Ordinal) && head.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Visible text of an HTML print, one line per row, cell or paragraph.</summary>
    internal static string HtmlText(string html)
    {
        var withoutScripts = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var lines = System.Text.RegularExpressions.Regex.Replace(withoutScripts, @"<\s*(br|/p|/div|/tr|/li|/h\d|/td|/th)[^>]*>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(lines, "<[^>]+>", " "));
        return string.Join("\n", text.Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"[ \t\r\u00A0]+", " ").Trim())
            .Where(line => line.Length > 0));
    }

    /// <summary>Combine the Word reader's terms with the document's running text. Pure.</summary>
    internal static BuyerQuoteTerms FromTermsAndText(IReadOnlyList<BuyerTerms.Term> terms, string? text, string? fileName)
    {
        BuyerTerms.Term? Term(string key) => terms.FirstOrDefault(t => t.Key == key);

        var validityTerm = Term("validity");
        var validity = validityTerm is null ? null : BuyerQuoteTermRules.ParseValidityTerm(validityTerm.Value, validityTerm.Quote);
        validity ??= BuyerQuoteTermRules.ParseValidity(text);

        IReadOnlyList<string> currencies = Array.Empty<string>();
        string? currencySentence = null;
        if (Term("quote_currency") is { } currency)
        {
            currencies = BuyerQuoteTermRules.ParseAllowedCurrencies(currency.Value);
            currencySentence = currency.Quote;
        }
        if (currencies.Count == 0 && BuyerQuoteTermRules.ParseCurrencyRule(text) is { } rule)
        {
            currencies = rule.Codes;
            currencySentence = rule.Sentence;
        }

        return new BuyerQuoteTerms(validity, currencies, currencySentence,
            Term("delivery_terms")?.Value, Term("deliver_to")?.Value, Term("agreement")?.Value,
            Term("payment")?.Value, fileName);
    }
}
