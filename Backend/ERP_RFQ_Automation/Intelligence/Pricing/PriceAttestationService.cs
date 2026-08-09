using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>One quote line as it stands right now, or as it stood at confirmation time.</summary>
public sealed record PriceAttestationLineSnapshot(
    long QuoteItemId,
    long? RfqItemId,
    string? ItemDescription,
    decimal Quantity,
    decimal UnitPrice);

/// <summary>
/// Whether a quote may be sent, and why not when it may not. <see cref="Reason"/> is
/// customer-neutral, plain commercial language intended to be shown to the rep verbatim.
/// </summary>
public sealed class PriceAttestationState
{
    public required bool Satisfied { get; init; }

    /// <summary>Null when satisfied; otherwise the reason the send is refused.</summary>
    public string? Reason { get; init; }

    /// <summary>The most recent attestation on this quote, valid or not (null when none exists).</summary>
    public QuotePriceAttestation? Latest { get; init; }

    /// <summary>The quote's prices as they stand now — what a fresh confirmation would cover.</summary>
    public required IReadOnlyList<PriceAttestationLineSnapshot> CurrentLines { get; init; }

    public required string CurrentFingerprint { get; init; }
}

public interface IPriceAttestationService
{
    /// <summary>
    /// Decides whether the quote's CURRENT prices are covered by a recorded confirmation.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Quote not found in this business unit.</exception>
    Task<PriceAttestationState> EvaluateAsync(long quoteId, long businessUnitId, CancellationToken ct);

    /// <summary>
    /// Records the rep's confirmation over the quote's current prices and returns it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Quote not found in this business unit.</exception>
    /// <exception cref="ArgumentException">Unknown source, or a missing/over-long reference.</exception>
    Task<QuotePriceAttestation> AttestAsync(
        long quoteId, long businessUnitId, string source, string sourceReference,
        long? confirmedByUserId, string confirmedBy, CancellationToken ct);
}

/// <summary>
/// R5 price-provenance gate. Reads and writes the attestation that
/// <see cref="Services.QuoteService.SendQuoteEmailAsync"/> requires before any quote email
/// is queued.
///
/// Validity is decided by COMPARING PRICES, not timestamps. The fingerprint covers the
/// quote currency and every (quote item id, unit price) pair, so all three of these
/// invalidate a confirmation and force the rep to confirm again:
///   * a unit price is edited,
///   * a line is added (a price nobody confirmed) or removed,
///   * the quote currency is changed (the same number, different money).
/// Quantity is recorded for the audit trail but deliberately does NOT invalidate — R5
/// governs where the PRICE came from, and a quantity correction is not a repricing.
///
/// Every query filters BusinessUnitId explicitly. This service runs from the send path,
/// which is also reachable from the below-floor approval tool, where the ambient tenant
/// context can be absent and the global query filter degrades to a no-op.
/// </summary>
public sealed class PriceAttestationService : IPriceAttestationService
{
    private const int MaxReferenceLength = 200;

    private readonly ErpRfqAutomationContext _db;

    public PriceAttestationService(ErpRfqAutomationContext db) => _db = db;

    public async Task<PriceAttestationState> EvaluateAsync(long quoteId, long businessUnitId, CancellationToken ct)
    {
        var (currencyId, lines) = await LoadCurrentAsync(quoteId, businessUnitId, ct);
        var fingerprint = ComputeFingerprint(currencyId, lines);

        var latest = await _db.QuotePriceAttestations.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.BusinessUnitId == businessUnitId && a.QuoteId == quoteId)
            .OrderByDescending(a => a.ConfirmedOn).ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
        {
            return new PriceAttestationState
            {
                Satisfied = false,
                Reason = "The price source has not been confirmed for this quote. Confirm where the " +
                         "prices came from — your sales manager, or a supplier quote — before sending it.",
                CurrentLines = lines,
                CurrentFingerprint = fingerprint
            };
        }

        if (!string.Equals(latest.LineFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new PriceAttestationState
            {
                Satisfied = false,
                Reason = $"The prices on this quote changed after {latest.ConfirmedBy} confirmed them on " +
                         $"{latest.ConfirmedOn:dd MMM yyyy HH:mm} UTC. Confirm the price source again before sending it.",
                Latest = latest,
                CurrentLines = lines,
                CurrentFingerprint = fingerprint
            };
        }

        return new PriceAttestationState
        {
            Satisfied = true,
            Latest = latest,
            CurrentLines = lines,
            CurrentFingerprint = fingerprint
        };
    }

    public async Task<QuotePriceAttestation> AttestAsync(
        long quoteId, long businessUnitId, string source, string sourceReference,
        long? confirmedByUserId, string confirmedBy, CancellationToken ct)
    {
        var normalisedSource = (source ?? string.Empty).Trim().ToUpperInvariant();
        if (!PriceAttestationSources.IsKnown(normalisedSource))
            throw new ArgumentException(
                "Choose where the price came from: your sales manager, or a supplier quote.", nameof(source));

        var reference = (sourceReference ?? string.Empty).Trim();
        if (reference.Length == 0)
            throw new ArgumentException(
                normalisedSource == PriceAttestationSources.SupplierQuote
                    ? "Enter the supplier quote reference the price was taken from."
                    : "Enter the name of the sales manager who gave you the price.",
                nameof(sourceReference));
        if (reference.Length > MaxReferenceLength)
            throw new ArgumentException(
                $"The price source reference cannot be longer than {MaxReferenceLength} characters.",
                nameof(sourceReference));

        var (currencyId, lines) = await LoadCurrentAsync(quoteId, businessUnitId, ct);

        var attestation = new QuotePriceAttestation
        {
            BusinessUnitId = businessUnitId,
            QuoteId = quoteId,
            Source = normalisedSource,
            SourceReference = reference,
            LineFingerprint = ComputeFingerprint(currencyId, lines),
            CurrencyId = currencyId,
            LineCount = lines.Count,
            ConfirmedByUserId = confirmedByUserId,
            ConfirmedBy = string.IsNullOrWhiteSpace(confirmedBy) ? "authenticated-user" : confirmedBy.Trim(),
            ConfirmedOn = DateTime.UtcNow,
            Lines = lines.Select(l => new QuotePriceAttestationLine
            {
                BusinessUnitId = businessUnitId,
                QuoteItemId = l.QuoteItemId,
                RfqItemId = l.RfqItemId,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };

        _db.QuotePriceAttestations.Add(attestation);
        await _db.SaveChangesAsync(ct);
        return attestation;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>The quote's currency and current lines, ordered deterministically.</summary>
    private async Task<(long? CurrencyId, IReadOnlyList<PriceAttestationLineSnapshot> Lines)> LoadCurrentAsync(
        long quoteId, long businessUnitId, CancellationToken ct)
    {
        var quote = await _db.Quotes.AsNoTracking().IgnoreQueryFilters()
            .Where(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId)
            .Select(q => new { q.CurrencyId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Quote not found");

        var lines = await _db.QuoteItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.QuoteId == quoteId)
            .OrderBy(i => i.Id)
            .Select(i => new PriceAttestationLineSnapshot(
                i.Id, i.RfqitemId, i.ItemDescription, i.Quantity, i.UnitPrice))
            .ToListAsync(ct);

        return (quote.CurrencyId, lines);
    }

    /// <summary>
    /// Stable digest of "what money this quote asks for": the currency plus every
    /// (line, unit price) pair, ordered by line id. Prices are rendered at the column's own
    /// scale (decimal(18,6)) so a trailing-zero difference is not mistaken for a repricing.
    /// </summary>
    public static string ComputeFingerprint(long? currencyId, IEnumerable<PriceAttestationLineSnapshot> lines)
    {
        var canonicalLines = string.Join(";", lines
            .OrderBy(l => l.QuoteItemId)
            .Select(l => string.Concat(
                l.QuoteItemId.ToString(CultureInfo.InvariantCulture),
                ":",
                l.UnitPrice.ToString("F6", CultureInfo.InvariantCulture))));

        var payload = string.Concat(
            "v1|currency:", currencyId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            "|lines:", canonicalLines);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
