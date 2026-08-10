using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.SupplierQuotes;

public interface ISupplierQuoteStore
{
    long? ScopedTenantId { get; }
    Task<SupplierQuoteAnchor?> ResolveAnchorAsync(long businessUnitId, long supplierId,
        long solicitationId, long sourcingCaseId, CancellationToken cancellationToken);
    Task<SupplierQuoteRevision?> FindRevisionByIdempotencyKeyAsync(long businessUnitId,
        string idempotencyKey, CancellationToken cancellationToken);
    Task<SupplierQuote?> FindCanonicalAsync(long businessUnitId, long supplierId,
        string supplierQuoteReference, CancellationToken cancellationToken);
    Task<SupplierQuoteCaptureResult> PersistRevisionAsync(SupplierQuote quote,
        SupplierQuoteRevision revision, bool isNewQuote, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<SupplierQuoteInboxRow>> SearchInboxAsync(long businessUnitId,
        string? status, int limit, CancellationToken cancellationToken);
    Task<SupplierQuoteDetail?> GetDetailAsync(long businessUnitId, long supplierQuoteId,
        CancellationToken cancellationToken);
    Task ReviewAsync(ReviewSupplierQuoteFieldCommand command, CancellationToken cancellationToken);
}

public sealed class EfSupplierQuoteStore(ErpRfqAutomationContext context) : ISupplierQuoteStore
{
    public long? ScopedTenantId => context.ScopedTenantId;

    public async Task<SupplierQuoteAnchor?> ResolveAnchorAsync(long businessUnitId, long supplierId,
        long solicitationId, long sourcingCaseId, CancellationToken cancellationToken)
    {
        var supplierExists = await context.Suppliers.AsNoTracking().AnyAsync(x =>
            x.Id == supplierId && x.Buid == businessUnitId && x.IsActive == true, cancellationToken);
        if (!supplierExists) return null;

        var solicitation = await context.Set<SupplierSolicitation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.Id == solicitationId && x.SupplierId == supplierId
            && x.SourcingCaseId == sourcingCaseId,
            cancellationToken);
        if (solicitation is null) return null;

        var sourcingCase = await context.Set<SourcingCase>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.Id == sourcingCaseId && x.RfqId == solicitation.RfqId,
            cancellationToken);
        if (sourcingCase is null) return null;
        if (!string.Equals(solicitation.NexoraSerial, sourcingCase.NexoraSerial, StringComparison.Ordinal))
            return null;

        var demandLines = await context.Set<CommercialDemandLine>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == solicitation.RfqId &&
                x.NexoraSerial == sourcingCase.NexoraSerial)
            .ToDictionaryAsync(x => x.RfqItemId, x => x.Id, cancellationToken);
        return new SupplierQuoteAnchor(supplierId, solicitation.Id, sourcingCase.Id,
            solicitation.RfqId, sourcingCase.NexoraSerial, demandLines);
    }

    public Task<SupplierQuoteRevision?> FindRevisionByIdempotencyKeyAsync(long businessUnitId,
        string idempotencyKey, CancellationToken cancellationToken) =>
        context.Set<SupplierQuoteRevision>().AsNoTracking().Include(x => x.Evidence).SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<SupplierQuote?> FindCanonicalAsync(long businessUnitId, long supplierId,
        string supplierQuoteReference, CancellationToken cancellationToken) =>
        context.Set<SupplierQuote>().SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId &&
            x.SupplierId == supplierId && x.SupplierQuoteReference == supplierQuoteReference, cancellationToken);

    public async Task<SupplierQuoteCaptureResult> PersistRevisionAsync(SupplierQuote quote,
        SupplierQuoteRevision revision, bool isNewQuote, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            if (isNewQuote && context.Entry(quote).State == EntityState.Detached) context.Add(quote);
            if (!quote.Revisions.Contains(revision)) quote.Revisions.Add(revision);
            await context.SaveChangesAsync(cancellationToken);
            return new SupplierQuoteCaptureResult(quote.Id, revision.Id, revision.RevisionNumber,
                quote.InboxStatus, revision.Evidence.Count(x => x.ReviewRequired), false);
        }
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (isNewQuote && context.Entry(quote).State == EntityState.Detached) context.Add(quote);
            if (!quote.Revisions.Contains(revision)) quote.Revisions.Add(revision);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SupplierQuoteCaptureResult(quote.Id, revision.Id, revision.RevisionNumber,
                quote.InboxStatus, revision.Evidence.Count(x => x.ReviewRequired), false);
        });
    }

    public async Task<IReadOnlyCollection<SupplierQuoteInboxRow>> SearchInboxAsync(long businessUnitId,
        string? status, int limit, CancellationToken cancellationToken)
    {
        var query = context.Set<SupplierQuote>().AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.InboxStatus == status);
        var rows = await query.OrderByDescending(x => x.UpdatedOn).Take(limit)
            .Join(context.Suppliers.AsNoTracking().Where(x => x.Buid == businessUnitId),
                quote => quote.SupplierId, supplier => supplier.Id, (quote, supplier) => new { quote, supplier })
            .Select(x => new
            {
                Quote = x.quote,
                SupplierName = x.supplier.Name,
                RevisionId = context.Set<SupplierQuoteRevision>()
                    .Where(r => r.BusinessUnitId == businessUnitId && r.SupplierQuoteId == x.quote.Id &&
                        r.RevisionNumber == x.quote.CurrentRevisionNumber)
                    .Select(r => r.Id).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
        var revisionIds = rows.Select(x => x.RevisionId).Where(x => x > 0).ToArray();
        var evidence = await context.Set<SupplierQuoteFieldEvidence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && revisionIds.Contains(x.SupplierQuoteRevisionId) &&
                x.ReviewRequired).ToArrayAsync(cancellationToken);
        var evidenceIds = evidence.Select(x => x.Id).ToArray();
        var latestByEvidence = (await context.Set<SupplierQuoteReviewDecision>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && evidenceIds.Contains(x.SupplierQuoteFieldEvidenceId))
                .OrderByDescending(x => x.ReviewedOn).ThenByDescending(x => x.Id)
                .ToArrayAsync(cancellationToken))
            .GroupBy(x => x.SupplierQuoteFieldEvidenceId).ToDictionary(x => x.Key, x => x.First());
        return rows.Select(row => new SupplierQuoteInboxRow(row.Quote.Id, row.Quote.SupplierId,
            row.SupplierName, row.Quote.SupplierQuoteReference, row.Quote.NexoraSerial,
            row.Quote.SourcingCaseId, row.Quote.CurrentRevisionNumber, row.Quote.InboxStatus,
            row.Quote.UpdatedOn, Math.Max(row.Quote.InboxStatus == SupplierQuoteInboxStatuses.ReviewRequired ? 1 : 0,
                evidence.Count(item => item.SupplierQuoteRevisionId == row.RevisionId &&
                    (!latestByEvidence.TryGetValue(item.Id, out var latest) ||
                     latest.Status is not (SupplierQuoteReviewStatuses.Accepted or SupplierQuoteReviewStatuses.Corrected))))))
            .ToArray();
    }

    public async Task<SupplierQuoteDetail?> GetDetailAsync(long businessUnitId, long supplierQuoteId,
        CancellationToken cancellationToken)
    {
        var quote = await context.Set<SupplierQuote>().AsNoTracking()
            .Include(x => x.Revisions).ThenInclude(x => x.Lines)
            .Include(x => x.Revisions).ThenInclude(x => x.Evidence)
            .Include(x => x.Revisions).ThenInclude(x => x.ReviewDecisions)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == supplierQuoteId,
                cancellationToken);
        if (quote is null) return null;
        var supplierName = await context.Suppliers.AsNoTracking().Where(x => x.Buid == businessUnitId &&
            x.Id == quote.SupplierId).Select(x => x.Name).SingleAsync(cancellationToken);
        return ToDetail(quote, supplierName);
    }

    public async Task ReviewAsync(ReviewSupplierQuoteFieldCommand command, CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var quote = await context.Set<SupplierQuote>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SupplierQuoteId, cancellationToken)
                ?? throw new SupplierQuoteNotFoundException("The Supplier Quote was not found in this tenant.");
            var revision = await context.Set<SupplierQuoteRevision>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.RevisionId &&
                x.SupplierQuoteId == quote.Id, cancellationToken)
                ?? throw new SupplierQuoteNotFoundException("The Supplier Quote revision was not found in this tenant.");
            var evidence = await context.Set<SupplierQuoteFieldEvidence>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.EvidenceId &&
                x.SupplierQuoteRevisionId == revision.Id, cancellationToken)
                ?? throw new SupplierQuoteNotFoundException("The field evidence was not found in this revision.");
            if (command.Status == SupplierQuoteReviewStatuses.Corrected)
                ValidateCorrection(evidence.FieldName, command.CorrectedValue);

            context.Add(new SupplierQuoteReviewDecision
            {
                BusinessUnitId = command.BusinessUnitId,
                SupplierQuoteRevisionId = revision.Id,
                SupplierQuoteFieldEvidenceId = evidence.Id,
                Status = command.Status,
                CorrectedValue = command.CorrectedValue,
                Reason = command.Reason,
                ReviewedBy = command.Actor,
                ReviewedOn = DateTime.UtcNow,
                CorrelationId = command.CorrelationId
            });
            await context.SaveChangesAsync(cancellationToken);

            var requiredEvidenceIds = await context.Set<SupplierQuoteFieldEvidence>()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId &&
                    x.SupplierQuoteRevisionId == revision.Id && x.ReviewRequired)
                .Select(x => x.Id).ToArrayAsync(cancellationToken);
            // EVERY field on the revision, not only the ones flagged for review. Two separate
            // questions are being answered below and they need different populations:
            //
            //   * "is anything still unreviewed?" — the review-required subset, unchanged;
            //   * "has anything been corrected since the offer was projected?" — every field,
            //     because a correction moves the money whether or not the field was ever flagged.
            //
            // Both used to be answered from the review-required subset alone. A manually captured
            // Supplier Quote flags nothing (confidence is 1 by construction), so its correction set
            // was always empty, and the guard that catches a correction to an ALREADY-AWARDED offer
            // never ran for any of them. ProjectAsync has always computed this over all decisions
            // on the revision, so the two halves of one control disagreed: the projection refused
            // while the inbox went on reporting READY_FOR_COMPARISON.
            var revisionEvidence = await context.Set<SupplierQuoteFieldEvidence>().AsNoTracking()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId &&
                    x.SupplierQuoteRevisionId == revision.Id)
                .Select(x => new { x.Id, x.FieldName }).ToArrayAsync(cancellationToken);
            var revisionEvidenceIds = revisionEvidence.Select(x => x.Id).ToArray();
            var latestDecisions = await context.Set<SupplierQuoteReviewDecision>().AsNoTracking()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId &&
                    revisionEvidenceIds.Contains(x.SupplierQuoteFieldEvidenceId))
                .OrderByDescending(x => x.ReviewedOn).ThenByDescending(x => x.Id)
                .ToArrayAsync(cancellationToken);
            var latestByEvidence = latestDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
                .ToDictionary(x => x.Key, x => x.First());
            var unresolved = requiredEvidenceIds.Any(id =>
                !latestByEvidence.TryGetValue(id, out var latest) ||
                latest.Status is not (SupplierQuoteReviewStatuses.Accepted or SupplierQuoteReviewStatuses.Corrected));
            // Alternate-authorisation evidence is excluded because approving an alternate is a
            // commercial permission, not a change to the numbers the projection is built from.
            var projectionEvidenceIds = revisionEvidence.Where(x =>
                    NormalizeEvidenceField(x.FieldName) is not ("ALTERNATEAUTHORIZATION" or
                        "ALTERNATEAPPROVAL" or "APPROVEDALTERNATE"))
                .Select(x => x.Id).ToHashSet();
            var latestCorrection = latestByEvidence.Values
                .Where(x => x.Status == SupplierQuoteReviewStatuses.Corrected &&
                    projectionEvidenceIds.Contains(x.SupplierQuoteFieldEvidenceId))
                .Select(x => (DateTime?)x.ReviewedOn).Max();
            if (latestCorrection.HasValue)
            {
                var projected = await context.SupplierQuotedItems.AsNoTracking().Where(x =>
                        x.BusinessUnitId == command.BusinessUnitId && x.SourceSupplierQuoteRevisionId == revision.Id)
                    .Select(x => new { x.Id, ProjectedOn = x.ModifiedDate ?? x.CreatedDate })
                    .ToArrayAsync(cancellationToken);
                var projectedIds = projected.Select(x => x.Id).ToArray();
                unresolved |= projected.Any(x => latestCorrection > x.ProjectedOn) &&
                    await context.Set<SourcingAward>().AsNoTracking().AnyAsync(x =>
                        x.BusinessUnitId == command.BusinessUnitId && x.SupplierQuotedItemId.HasValue &&
                        projectedIds.Contains(x.SupplierQuotedItemId.Value) &&
                        x.Status != "CANCELLED" && x.Status != "REJECTED", cancellationToken);
            }
            quote.InboxStatus = unresolved ? SupplierQuoteInboxStatuses.ReviewRequired
                : SupplierQuoteInboxStatuses.ReadyForComparison;
            quote.UpdatedOn = DateTime.UtcNow;
            quote.UpdatedBy = command.Actor;
            quote.Version++;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static SupplierQuoteDetail ToDetail(SupplierQuote quote, string supplierName)
    {
        var revisions = quote.Revisions.OrderByDescending(x => x.RevisionNumber).Select(revision =>
        {
            var latest = revision.ReviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ReviewedOn)
                    .ThenByDescending(y => y.Id).First());
            return new SupplierQuoteRevisionDetail(revision.Id, revision.RevisionNumber,
                revision.CaptureChannel, revision.CurrencyId, revision.ValidUntil, revision.RequiresReview,
                revision.CapturedOn,
                revision.Lines.OrderBy(x => x.LineNumber).Select(x => new SupplierQuoteLineDetail(x.Id,
                    x.LineNumber, x.RfqItemId, x.PartNumber, x.Description, x.Quantity,
                    x.AvailableQuantity, x.UnitPrice, x.LeadTimeDays)).ToArray(),
                revision.Evidence.Select(x =>
                {
                    latest.TryGetValue(x.Id, out var decision);
                    return new SupplierQuoteEvidenceDetail(x.Id, x.SupplierQuoteLineId, x.FieldName,
                        x.OriginalValue, x.NormalizedValue, x.Confidence, x.Method, x.Critical,
                        x.ReviewRequired, decision?.Status, decision?.CorrectedValue);
                }).ToArray(),
                revision.Incoterms, revision.FreightAmount, revision.TaxAmount,
                revision.DutyAmount, revision.OtherAmount, revision.DiscountAmount);
        }).ToArray();
        return new SupplierQuoteDetail(quote.Id, quote.SupplierId, supplierName,
            quote.SupplierSolicitationId, quote.SourcingCaseId, quote.RfqId, quote.NexoraSerial,
            quote.SupplierQuoteReference, quote.CurrentRevisionNumber, quote.InboxStatus, quote.Version, revisions);
    }

    /// <summary>
    /// Validates a reviewer's correction against the SHAPE of the field being corrected.
    ///
    /// <para>The round-charge fields are named here rather than left to the catch-all. They used to
    /// fall through to "any string up to 4,000 printable characters", so a reviewer could type
    /// "approx 1,500 SAR" into FreightAmount, be told the correction was accepted, and leave a
    /// value the projection could never parse into money. Field names are normalized the same way
    /// the evidence comparison normalizes them, so "Freight Amount" and "freight_amount" reach the
    /// decimal rule rather than the catch-all.</para>
    ///
    /// <para>Non-negative rather than positive: correcting a wrongly captured freight charge down
    /// to zero is a legitimate correction, whereas a negative charge would reduce landed cost and
    /// therefore the customer price.</para>
    /// </summary>
    private static void ValidateCorrection(string fieldName, string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new SupplierQuoteValidationException("A corrected value is required.");
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var valid = NormalizeEvidenceField(fieldName) switch
        {
            "CURRENCYID" => long.TryParse(normalized, System.Globalization.NumberStyles.Integer, culture, out var id) && id > 0,
            "VALIDUNTIL" => DateTime.TryParse(normalized, culture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var date) && date > DateTime.UtcNow,
            "QUANTITY" or "UNITPRICE" => decimal.TryParse(normalized,
                System.Globalization.NumberStyles.Number, culture, out var positive) && positive > 0m,
            "AVAILABLEQUANTITY" => decimal.TryParse(normalized,
                System.Globalization.NumberStyles.Number, culture, out var available) && available >= 0m,
            "FREIGHTAMOUNT" or "TAXAMOUNT" or "DUTYAMOUNT" or "OTHERAMOUNT" or "DISCOUNTAMOUNT" =>
                decimal.TryParse(normalized, System.Globalization.NumberStyles.Number, culture,
                    out var charge) && charge >= 0m && charge <= MaxRoundCharge,
            "INCOTERMS" => Procurement.Incoterms.Codes.Contains(normalized.ToUpperInvariant()),
            "LEADTIMEDAYS" => int.TryParse(normalized,
                System.Globalization.NumberStyles.Integer, culture, out var days) && days >= 0,
            _ => normalized.Length <= 4000 && !normalized.Any(char.IsControl)
        };
        if (!valid) throw new SupplierQuoteValidationException($"The corrected {fieldName} value is invalid.");
    }

    /// <summary>
    /// Ceiling for a corrected round charge, matching the numeric(18,4) columns the value lands in.
    /// Without it a typed correction can exceed the column's range and fail as a database error
    /// deep inside the projection transaction rather than as a message the reviewer can act on.
    /// </summary>
    private const decimal MaxRoundCharge = 99_999_999_999_999m;

    private static string NormalizeEvidenceField(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant).ToArray());
}

public sealed class SupplierQuoteInboxService
{
    private const decimal CriticalConfidenceThreshold = 0.85m;
    internal const int MaxLinesPerQuote = 1000;
    internal const int MaxEvidencePerQuote = 5000;
    private static readonly HashSet<string> Channels = new(StringComparer.OrdinalIgnoreCase)
    {
        SupplierQuoteCaptureChannels.Manual, SupplierQuoteCaptureChannels.Offline,
        SupplierQuoteCaptureChannels.Email, SupplierQuoteCaptureChannels.Upload,
        SupplierQuoteCaptureChannels.Portal, SupplierQuoteCaptureChannels.Api
    };
    private static readonly HashSet<string> ReviewStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        SupplierQuoteReviewStatuses.Accepted, SupplierQuoteReviewStatuses.Corrected,
        SupplierQuoteReviewStatuses.Rejected
    };

    private readonly ISupplierQuoteStore _store;
    public SupplierQuoteInboxService(ErpRfqAutomationContext context) : this(new EfSupplierQuoteStore(context)) { }
    public SupplierQuoteInboxService(ISupplierQuoteStore store) => _store = store;

    public async Task<SupplierQuoteCaptureResult> CaptureAsync(CaptureSupplierQuoteCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(command.BusinessUnitId);
        Validate(command);
        var reference = Required(command.SupplierQuoteReference, 160, nameof(command.SupplierQuoteReference));
        var idempotencyKey = Required(command.IdempotencyKey, 160, nameof(command.IdempotencyKey));
        var requestHash = Hash(command with { Actor = string.Empty, CorrelationId = string.Empty });
        var replay = await _store.FindRevisionByIdempotencyKeyAsync(command.BusinessUnitId,
            idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (!FixedEquals(replay.RequestHash, requestHash))
                throw new SupplierQuoteConflictException(
                    "The idempotency key was already used for a different Supplier Quote response.");
            return new SupplierQuoteCaptureResult(replay.SupplierQuoteId, replay.Id,
                replay.RevisionNumber, replay.RequiresReview ? SupplierQuoteInboxStatuses.ReviewRequired
                    : SupplierQuoteInboxStatuses.ReadyForComparison,
                replay.Evidence.Count(x => x.ReviewRequired), true);
        }

        var anchor = await _store.ResolveAnchorAsync(command.BusinessUnitId, command.SupplierId,
            command.SupplierSolicitationId, command.SourcingCaseId, cancellationToken)
            ?? throw new SupplierQuoteNotFoundException(
                "No tenant-safe Supplier, Supplier RFQ, and Sourcing Case match was found.");
        if (!string.Equals(anchor.NexoraSerial, command.NexoraSerial.Trim(), StringComparison.Ordinal))
            throw new SupplierQuoteValidationException("The Nexora Serial does not match the Sourcing Case.");
        foreach (var line in command.Lines)
        {
            if (!anchor.DemandLineByRfqItem.TryGetValue(line.RfqItemId, out var demandLineId) ||
                demandLineId != line.CommercialDemandLineId)
                throw new SupplierQuoteValidationException(
                    $"Line {line.LineNumber} does not match a demand line in the Supplier RFQ.");
        }

        var quote = await _store.FindCanonicalAsync(command.BusinessUnitId, command.SupplierId,
            reference, cancellationToken);
        var isNew = quote is null;
        if (isNew && command.RevisionNumber != 1)
            throw new SupplierQuoteValidationException("The first captured Supplier Quote revision must be Revision 1.");
        if (!isNew)
        {
            if (quote!.SupplierSolicitationId != anchor.SupplierSolicitationId ||
                quote.SourcingCaseId != anchor.SourcingCaseId || quote.NexoraSerial != anchor.NexoraSerial)
                throw new SupplierQuoteConflictException(
                    "This Supplier Quote reference is already linked to a different commercial journey.");
            if (command.RevisionNumber != quote.CurrentRevisionNumber + 1)
                throw new SupplierQuoteConflictException(
                    $"Revision {quote.CurrentRevisionNumber + 1} is required; prior revisions are immutable.");
        }

        var now = DateTime.UtcNow;
        quote ??= new SupplierQuote
        {
            BusinessUnitId = command.BusinessUnitId,
            SupplierId = command.SupplierId,
            SupplierSolicitationId = anchor.SupplierSolicitationId,
            SourcingCaseId = anchor.SourcingCaseId,
            RfqId = anchor.RfqId,
            NexoraSerial = anchor.NexoraSerial,
            SupplierQuoteReference = reference,
            CreatedOn = now,
            CreatedBy = command.Actor
        };
        var revision = BuildRevision(command, idempotencyKey, requestHash, now);
        quote.CurrentRevisionNumber = revision.RevisionNumber;
        quote.InboxStatus = revision.RequiresReview ? SupplierQuoteInboxStatuses.ReviewRequired
            : SupplierQuoteInboxStatuses.ReadyForComparison;
        quote.UpdatedOn = now;
        quote.UpdatedBy = command.Actor;
        if (!isNew) quote.Version++;
        return await _store.PersistRevisionAsync(quote, revision, isNew, cancellationToken);
    }

    public Task<IReadOnlyCollection<SupplierQuoteInboxRow>> SearchAsync(long businessUnitId,
        string? status, int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        if (limit is < 1 or > 200) throw new SupplierQuoteValidationException("Limit must be between 1 and 200.");
        if (!string.IsNullOrWhiteSpace(status) && status is not SupplierQuoteInboxStatuses.ReviewRequired &&
            status is not SupplierQuoteInboxStatuses.ReadyForComparison)
            throw new SupplierQuoteValidationException("Inbox status is invalid.");
        return _store.SearchInboxAsync(businessUnitId, status, limit, cancellationToken);
    }

    public async Task<SupplierQuoteDetail> GetAsync(long businessUnitId, long supplierQuoteId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        return await _store.GetDetailAsync(businessUnitId, supplierQuoteId, cancellationToken)
            ?? throw new SupplierQuoteNotFoundException("The Supplier Quote was not found in this tenant.");
    }

    public Task ReviewAsync(ReviewSupplierQuoteFieldCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(command.BusinessUnitId);
        if (!ReviewStatuses.Contains(command.Status))
            throw new SupplierQuoteValidationException("Review status must be ACCEPTED, CORRECTED, or REJECTED.");
        if (command.Status.Equals(SupplierQuoteReviewStatuses.Corrected, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(command.CorrectedValue))
            throw new SupplierQuoteValidationException("A corrected value is required for CORRECTED review decisions.");
        Required(command.Reason, 1000, nameof(command.Reason));
        return _store.ReviewAsync(command with { Status = command.Status.ToUpperInvariant() }, cancellationToken);
    }

    private SupplierQuoteRevision BuildRevision(CaptureSupplierQuoteCommand command, string idempotencyKey,
        string requestHash, DateTime now)
    {
        var manual = command.CaptureChannel.Equals(SupplierQuoteCaptureChannels.Manual,
                         StringComparison.OrdinalIgnoreCase) ||
                     command.CaptureChannel.Equals(SupplierQuoteCaptureChannels.Offline,
                         StringComparison.OrdinalIgnoreCase);
        var revision = new SupplierQuoteRevision
        {
            BusinessUnitId = command.BusinessUnitId,
            RevisionNumber = command.RevisionNumber,
            CaptureChannel = command.CaptureChannel.ToUpperInvariant(),
            SourceDocumentId = command.SourceDocumentId,
            SourceIdentity = Required(command.SourceIdentity, 500, nameof(command.SourceIdentity)),
            SourceSha256 = NormalizeSha256(command.SourceSha256),
            CurrencyId = command.CurrencyId,
            ValidUntil = command.ValidUntil,
            Incoterms = Trim(command.Incoterms, 40),
            FreightAmount = command.FreightAmount,
            TaxAmount = command.TaxAmount,
            DutyAmount = command.DutyAmount,
            OtherAmount = command.OtherAmount,
            DiscountAmount = command.DiscountAmount,
            PaymentTerms = Trim(command.PaymentTerms, 500),
            Notes = Trim(command.Notes, 4000),
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CapturedOn = now,
            CapturedBy = Required(command.Actor, 255, nameof(command.Actor)),
            CorrelationId = Required(command.CorrelationId, 160, nameof(command.CorrelationId))
        };
        foreach (var source in command.Evidence)
            revision.Evidence.Add(ToEvidence(command.BusinessUnitId, source, manual, now));
        foreach (var sourceLine in command.Lines.OrderBy(x => x.LineNumber))
        {
            var line = new SupplierQuoteLine
            {
                BusinessUnitId = command.BusinessUnitId,
                LineNumber = sourceLine.LineNumber,
                RfqItemId = sourceLine.RfqItemId,
                CommercialDemandLineId = sourceLine.CommercialDemandLineId,
                PartNumber = Trim(sourceLine.PartNumber, 255),
                Manufacturer = Trim(sourceLine.Manufacturer, 255),
                SupplierPartNumber = Trim(sourceLine.SupplierPartNumber, 255),
                Description = Required(sourceLine.Description, 1000, nameof(sourceLine.Description)),
                Quantity = sourceLine.Quantity,
                AvailableQuantity = sourceLine.AvailableQuantity,
                UnitOfMeasure = Required(sourceLine.UnitOfMeasure, 80, nameof(sourceLine.UnitOfMeasure)),
                UnitPrice = sourceLine.UnitPrice,
                MinimumOrderQuantity = sourceLine.MinimumOrderQuantity,
                LeadTimeDays = sourceLine.LeadTimeDays,
                AvailabilityType = Trim(sourceLine.AvailabilityType, 80),
                OriginCountry = Trim(sourceLine.OriginCountry, 120),
                Warranty = Trim(sourceLine.Warranty, 500),
                IsAlternate = sourceLine.IsAlternate,
                Exceptions = Trim(sourceLine.Exceptions, 2000)
            };
            foreach (var evidence in sourceLine.Evidence)
            {
                var row = ToEvidence(command.BusinessUnitId, evidence, manual, now);
                line.Evidence.Add(row);
                revision.Evidence.Add(row);
            }
            revision.Lines.Add(line);
        }
        if (manual) AddManualEvidence(revision, command, now);
        else AddMissingCriticalEvidence(revision, command, now);
        AddRoundChargeEvidence(revision, command, manual, now);
        revision.RequiresReview = revision.Evidence.Any(x => x.ReviewRequired);
        return revision;
    }

    private static SupplierQuoteFieldEvidence ToEvidence(long businessUnitId,
        CaptureSupplierQuoteEvidence source, bool manual, DateTime now)
    {
        if (source.Confidence is < 0 or > 1)
            throw new SupplierQuoteValidationException("Evidence confidence must be between 0 and 1.");
        var critical = source.Critical || IsCritical(source.FieldName);
        return new SupplierQuoteFieldEvidence
        {
            BusinessUnitId = businessUnitId,
            FieldName = Required(source.FieldName, 120, nameof(source.FieldName)),
            OriginalValue = Trim(source.OriginalValue, 4000),
            NormalizedValue = Trim(source.NormalizedValue, 4000),
            Confidence = manual ? 1m : source.Confidence,
            Method = manual ? "MANUAL_ENTRY" : Required(source.Method, 80, nameof(source.Method)),
            ModelOrRuleVersion = Trim(source.ModelOrRuleVersion, 160),
            SourcePage = source.SourcePage,
            SourceRegion = Trim(source.SourceRegion, 500),
            Critical = critical,
            ReviewRequired = !manual && critical && source.Confidence < CriticalConfidenceThreshold,
            CreatedOn = now
        };
    }

    private static void AddManualEvidence(SupplierQuoteRevision revision,
        CaptureSupplierQuoteCommand command, DateTime now)
    {
        if (revision.Evidence.All(x => !x.FieldName.Equals("CurrencyId", StringComparison.OrdinalIgnoreCase)))
            revision.Evidence.Add(Manual(command.BusinessUnitId, "CurrencyId", command.CurrencyId.ToString(), true, now));
        if (command.ValidUntil.HasValue && revision.Evidence.All(x =>
                !x.FieldName.Equals("ValidUntil", StringComparison.OrdinalIgnoreCase)))
            revision.Evidence.Add(Manual(command.BusinessUnitId, "ValidUntil",
                command.ValidUntil.Value.ToString("O"), true, now));
        foreach (var line in revision.Lines)
        {
            if (line.Evidence.All(x => !x.FieldName.Equals("Quantity", StringComparison.OrdinalIgnoreCase)))
                AddLineEvidence(revision, line,
                    Manual(command.BusinessUnitId, "Quantity", line.Quantity.ToString(), true, now));
            if (line.Evidence.All(x => !x.FieldName.Equals("UnitPrice", StringComparison.OrdinalIgnoreCase)))
                AddLineEvidence(revision, line,
                    Manual(command.BusinessUnitId, "UnitPrice", line.UnitPrice.ToString(), true, now));
            if (line.AvailableQuantity.HasValue && line.Evidence.All(x =>
                    !x.FieldName.Equals("AvailableQuantity", StringComparison.OrdinalIgnoreCase)))
                AddLineEvidence(revision, line, Manual(command.BusinessUnitId, "AvailableQuantity",
                    line.AvailableQuantity.Value.ToString(), true, now));
            if (line.LeadTimeDays.HasValue && line.Evidence.All(x =>
                    !x.FieldName.Equals("LeadTimeDays", StringComparison.OrdinalIgnoreCase)))
                AddLineEvidence(revision, line, Manual(command.BusinessUnitId, "LeadTimeDays",
                    line.LeadTimeDays.Value.ToString(), true, now));
        }
    }

    private static void AddMissingCriticalEvidence(SupplierQuoteRevision revision,
        CaptureSupplierQuoteCommand command, DateTime now)
    {
        AddMissingHeader("CurrencyId", command.CurrencyId.ToString());
        AddMissingHeader("ValidUntil", command.ValidUntil?.ToString("O"));
        foreach (var line in revision.Lines)
        {
            AddMissingLine(line, "Quantity", line.Quantity.ToString());
            AddMissingLine(line, "UnitPrice", line.UnitPrice.ToString());
            AddMissingLine(line, "AvailableQuantity", line.AvailableQuantity?.ToString());
            AddMissingLine(line, "LeadTimeDays", line.LeadTimeDays?.ToString());
        }
        return;

        void AddMissingHeader(string field, string? normalized)
        {
            if (revision.Evidence.Any(x => x.SupplierQuoteLineId is null &&
                                           x.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase))) return;
            revision.Evidence.Add(Missing(command.BusinessUnitId, field, normalized, now));
        }

        void AddMissingLine(SupplierQuoteLine line, string field, string? normalized)
        {
            if (line.Evidence.Any(x => x.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase))) return;
            AddLineEvidence(revision, line, Missing(command.BusinessUnitId, field, normalized, now));
        }
    }

    /// <summary>
    /// Puts one evidence row on the revision for each round-level charge, so the charge block is
    /// something a reviewer can see and correct.
    ///
    /// <para>Without this there was no evidence row for FreightAmount on any capture the platform
    /// produces itself, so <c>ReviewAsync</c> could only ever answer "the field evidence was not
    /// found in this revision". The correction path for the charge that most often goes missing
    /// existed only for quotes whose extractor happened to emit a FreightAmount fact.</para>
    ///
    /// <para>Deliberately NOT critical and NOT review-required. A zero charge is usually the truth
    /// — a domestic delivered-price quote really does carry no freight and no duty — and forcing
    /// every capture into REVIEW_REQUIRED over a legitimate zero trains reviewers to clear the
    /// queue without reading it. The Incoterm warning on the offer is what points at a zero that
    /// should not be zero; this row is what lets someone fix it.</para>
    /// </summary>
    private static void AddRoundChargeEvidence(SupplierQuoteRevision revision,
        CaptureSupplierQuoteCommand command, bool manual, DateTime now)
    {
        Add("FreightAmount", command.FreightAmount);
        Add("TaxAmount", command.TaxAmount);
        Add("DutyAmount", command.DutyAmount);
        Add("OtherAmount", command.OtherAmount);
        Add("DiscountAmount", command.DiscountAmount);
        if (!string.IsNullOrWhiteSpace(revision.Incoterms)) AddText("Incoterms", revision.Incoterms);
        return;

        void Add(string field, decimal amount) => AddText(field,
            amount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        void AddText(string field, string value)
        {
            if (revision.Evidence.Any(x => x.SupplierQuoteLineId is null &&
                    NormalizeField(x.FieldName) == NormalizeField(field))) return;
            revision.Evidence.Add(new SupplierQuoteFieldEvidence
            {
                BusinessUnitId = command.BusinessUnitId,
                FieldName = field,
                OriginalValue = value,
                NormalizedValue = value,
                Confidence = 1m,
                Method = manual ? "MANUAL_ENTRY" : "HEADER_CAPTURE",
                Critical = false,
                ReviewRequired = false,
                CreatedOn = now
            });
        }
    }

    private static string NormalizeField(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant).ToArray());

    private static void AddLineEvidence(SupplierQuoteRevision revision, SupplierQuoteLine line,
        SupplierQuoteFieldEvidence evidence)
    {
        line.Evidence.Add(evidence);
        revision.Evidence.Add(evidence);
    }

    private static SupplierQuoteFieldEvidence Manual(long businessUnitId, string name, string value,
        bool critical, DateTime now) => new()
    {
        BusinessUnitId = businessUnitId, FieldName = name, OriginalValue = value,
        NormalizedValue = value, Confidence = 1, Method = "MANUAL_ENTRY", Critical = critical,
        ReviewRequired = false, CreatedOn = now
    };

    private static SupplierQuoteFieldEvidence Missing(long businessUnitId, string name,
        string? normalizedValue, DateTime now) => new()
    {
        BusinessUnitId = businessUnitId, FieldName = name, NormalizedValue = normalizedValue,
        Confidence = 0, Method = "MISSING_EVIDENCE", Critical = true, ReviewRequired = true,
        CreatedOn = now
    };

    private static void Validate(CaptureSupplierQuoteCommand command)
    {
        if (command.BusinessUnitId <= 0 || command.SupplierId <= 0 || command.SupplierSolicitationId <= 0 ||
            command.SourcingCaseId <= 0 || command.CurrencyId <= 0)
            throw new SupplierQuoteValidationException("Tenant, Supplier, Supplier RFQ, Sourcing Case, and currency are required.");
        if (command.RevisionNumber <= 0) throw new SupplierQuoteValidationException("Revision number must be positive.");
        if (!Channels.Contains(command.CaptureChannel)) throw new SupplierQuoteValidationException("Capture channel is invalid.");
        if (string.IsNullOrWhiteSpace(command.NexoraSerial)) throw new SupplierQuoteValidationException("Nexora Serial is required.");
        if (command.FreightAmount < 0 || command.TaxAmount < 0 || command.DutyAmount < 0 ||
            command.OtherAmount < 0 || command.DiscountAmount < 0)
            throw new SupplierQuoteValidationException(
                "Freight, tax, duty, other charges, and discount cannot be negative.");
        if (command.Lines.Count is 0 or > MaxLinesPerQuote)
            throw new SupplierQuoteValidationException(
                $"A Supplier Quote requires between 1 and {MaxLinesPerQuote} lines.");
        if (command.Evidence.Count + command.Lines.Sum(x => x.Evidence.Count) > MaxEvidencePerQuote)
            throw new SupplierQuoteValidationException(
                $"A Supplier Quote cannot contain more than {MaxEvidencePerQuote} evidence facts.");
        if (command.Lines.Select(x => x.LineNumber).Distinct().Count() != command.Lines.Count)
            throw new SupplierQuoteValidationException("Supplier Quote line numbers must be unique.");
        foreach (var line in command.Lines)
            if (line.LineNumber <= 0 || line.RfqItemId <= 0 || line.CommercialDemandLineId <= 0 ||
                line.Quantity <= 0 || line.UnitPrice < 0 || line.AvailableQuantity is < 0 ||
                line.MinimumOrderQuantity is <= 0 || line.LeadTimeDays is < 0)
                throw new SupplierQuoteValidationException($"Supplier Quote line {line.LineNumber} contains invalid values.");
        // A discount larger than the round it discounts produces a negative landed cost, and the
        // customer price is landed / (1 - margin) — so it would arrive as a negative price rather
        // than as an error anybody could see.
        if (command.DiscountAmount > command.Lines.Sum(x => x.UnitPrice * x.Quantity) +
                command.FreightAmount + command.DutyAmount + command.OtherAmount)
            throw new SupplierQuoteValidationException(
                "The Supplier Quote discount exceeds the value of the round it discounts.");
    }

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || (_store.ScopedTenantId.HasValue && _store.ScopedTenantId != businessUnitId))
            throw new UnauthorizedAccessException("The Supplier Quote tenant does not match the authenticated tenant.");
    }

    private static bool IsCritical(string fieldName) => fieldName.Trim().ToUpperInvariant() is
        "UNITPRICE" or "CURRENCYID" or "QUANTITY" or "AVAILABLEQUANTITY" or "AVAILABILITYTYPE" or
        "VALIDUNTIL" or "LEADTIMEDAYS";
    private static string NormalizeSha256(string value)
    {
        var normalized = Required(value, 64, nameof(value)).ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(x => !Uri.IsHexDigit(x)))
            throw new SupplierQuoteValidationException("Source SHA-256 must contain exactly 64 hexadecimal characters.");
        return normalized;
    }
    private static string Required(string? value, int maxLength, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new SupplierQuoteValidationException($"{name} is required and must not exceed {maxLength} printable characters.");
        return normalized;
    }
    private static string? Trim(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new SupplierQuoteValidationException($"A text value exceeds {maxLength} printable characters.");
        return normalized;
    }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static bool FixedEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
}
