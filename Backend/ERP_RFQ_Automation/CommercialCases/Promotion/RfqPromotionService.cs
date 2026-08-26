using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Promotion;

/// <summary>
/// The only lead-origin RFQ creation service. It consumes a committed, current-revision
/// participation decision and copies only its Bid lines.
/// </summary>
public sealed class RfqPromotionService : IRfqPromotionService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly IEvidenceObjectStorage _evidenceStorage;

    public RfqPromotionService(ErpRfqAutomationContext db, IEvidenceObjectStorage evidenceStorage)
    {
        _db = db;
        _evidenceStorage = evidenceStorage;
    }

    public async Task<RfqPromotionResult> PromoteAsync(
        long businessUnitId, long leadId, PromoteLeadToRfqCommand command, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(command));

        var requestHash = Hash(new
        {
            businessUnitId,
            leadId,
            command.ExpectedLeadRevisionId,
            command.ExpectedDecisionVersion,
            command.ExpectedParticipationVersion,
            command.ParticipationDecisionId
        });
        var replay = await ReplayAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
        if (replay is not null) return replay;

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                replay = await ReplayAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
                if (replay is not null) return replay;

                var lead = await _db.Leads
                    .Include(x => x.LeadStatus)
                    .Include(x => x.LeadItems)
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
                    ?? throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");
                if (!lead.CurrentRevisionId.HasValue)
                    throw new InvalidOperationException("The lead has no immutable current revision.");
                if (lead.CurrentRevisionId.Value != command.ExpectedLeadRevisionId)
                    throw new InvalidOperationException("The lead changed after participation was decided. Refresh and decide the current revision.");
                if (lead.CurrentRevisionNumber != command.ExpectedDecisionVersion)
                    throw new InvalidOperationException("The Lead decision version changed after participation was decided. Refresh the workbench.");
                LeadConversionGate.EnsureEligible(lead);

                var decision = await _db.Set<LeadParticipationDecision>()
                    .Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                        && (!command.ParticipationDecisionId.HasValue || x.Id == command.ParticipationDecisionId.Value)
                        && x.Sequence == command.ExpectedParticipationVersion
                        && x.LeadId == leadId
                        && x.LeadRevisionId == command.ExpectedLeadRevisionId, ct)
                    ?? throw new InvalidOperationException("The participation decision does not belong to the current lead revision.");
                var latestDecisionId = await _db.Set<LeadParticipationDecision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                    .OrderByDescending(x => x.Sequence).Select(x => x.Id).FirstAsync(ct);
                if (latestDecisionId != decision.Id)
                    throw new InvalidOperationException("A newer participation decision exists for this revision. Promote the current decision.");
                if (!decision.IsCommitted)
                    throw new InvalidOperationException("Participation is still a draft. Commit the current-revision decision before promotion.");
                if (decision.Lines.Any(x => x.Choice is LeadLineParticipationChoice.Pending or LeadLineParticipationChoice.Clarify))
                    throw new InvalidOperationException("Pending or Clarify lines must be resolved before RFQ promotion.");
                var fit = await _db.Set<LeadFitAssessment>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == decision.FitAssessmentId
                    && x.LeadId == leadId && x.LeadRevisionId == command.ExpectedLeadRevisionId, ct)
                    ?? throw new InvalidOperationException("The committed participation decision has no fit assessment for the current revision.");
                var latestFitId = await _db.Set<LeadFitAssessment>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId
                        && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                    .OrderByDescending(x => x.Sequence).Select(x => x.Id).FirstAsync(ct);
                if (fit.Id != latestFitId)
                    throw new InvalidOperationException("The fit assessment changed after participation was committed. Recommit participation against the latest assessment.");
                if (!fit.IsActionable)
                    throw new InvalidOperationException("The current human fit assessment is not actionable for RFQ promotion.");
                if (decision.Outcome == LeadParticipationOutcome.NoBid || decision.Lines.All(x => x.Choice != LeadLineParticipationChoice.Bid))
                    throw new InvalidOperationException("A no-bid decision closes participation without creating an RFQ.");
                var approvedDecisionLines = decision.Lines.Where(x => x.Choice == LeadLineParticipationChoice.Bid).ToArray();
                var selectedProductIds = approvedDecisionLines.Where(x => x.ProductId.HasValue)
                    .Select(x => x.ProductId!.Value).Distinct().ToArray();
                if (selectedProductIds.Length > 0 && await _db.Products.AsNoTracking()
                    .CountAsync(x => selectedProductIds.Contains(x.Id) && x.IsActive != false, ct) != selectedProductIds.Length)
                    throw new InvalidOperationException("A selected catalog product is no longer active or visible to this business unit.");
                var selectedUomIds = approvedDecisionLines.Where(x => x.UomId.HasValue)
                    .Select(x => x.UomId!.Value).Distinct().ToArray();
                if (approvedDecisionLines.Any(x => !x.UomId.HasValue)
                    || await _db.SetUoms.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId
                        && selectedUomIds.Contains(x.UomId) && x.IsActive, ct) != selectedUomIds.Length)
                    throw new InvalidOperationException("Every approved line must retain an active tenant unit-of-measure identity.");
                var selectedCurrencyIds = approvedDecisionLines.Where(x => x.CurrencyId.HasValue)
                    .Select(x => x.CurrencyId!.Value).Distinct().ToArray();
                if (approvedDecisionLines.Any(x => !x.CurrencyId.HasValue)
                    || await _db.Currencies.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId
                        && selectedCurrencyIds.Contains(x.Id) && x.IsActive != false, ct) != selectedCurrencyIds.Length)
                    throw new InvalidOperationException("Every approved line must retain an active tenant currency identity.");

                var existing = await _db.Rfqs.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
                if (existing is not null)
                {
                    if (existing.ParticipationDecisionId == decision.Id && existing.PromotionId.HasValue)
                    {
                        var receipt = await _db.Set<RfqPromotion>().AsNoTracking()
                            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == existing.PromotionId.Value, ct);
                        await tx.CommitAsync(ct);
                        return Result(receipt, existing, command.ExpectedDecisionVersion, decision.Sequence, replayed: true);
                    }
                    throw new InvalidOperationException($"Lead {leadId} already has RFQ #{existing.Id} ({existing.Rfqno}).");
                }

                var revisionLines = await _db.Set<LeadItemRevision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                    .OrderBy(x => x.LineNumber).ToListAsync(ct);
                if (decision.Lines.Count != revisionLines.Count
                    || decision.Lines.Select(x => x.LeadItemRevisionId).Except(revisionLines.Select(x => x.Id)).Any())
                    throw new InvalidOperationException("The participation decision does not cover every line of the current revision.");

                var approvedRevisionIds = decision.Lines
                    .Where(x => x.Choice == LeadLineParticipationChoice.Bid)
                    .Select(x => x.LeadItemRevisionId)
                    .ToHashSet();
                var approvedRevisionLines = revisionLines.Where(x => approvedRevisionIds.Contains(x.Id)).ToArray();
                if (approvedRevisionLines.Any(x => !x.LeadItemId.HasValue))
                    throw new InvalidOperationException(
                        "An approved revision line has no exact immutable canonical Lead-item lineage. Reconcile the source before promotion.");
                var approvedLeadItemIds = approvedRevisionLines.Select(x => x.LeadItemId!.Value).Distinct().ToArray();
                var approvedEvidenceSourceByLeadItemId = await _db.LeadItems.AsNoTracking()
                    .Where(x => approvedLeadItemIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.EvidenceSourceLeadItemId })
                    .ToDictionaryAsync(x => x.Id, x => x.EvidenceSourceLeadItemId ?? x.Id, ct);
                if (approvedEvidenceSourceByLeadItemId.Count != approvedLeadItemIds.Length)
                    throw new InvalidOperationException(
                        "An approved revision line does not resolve to its retained canonical Lead item.");
                var approvedEvidenceSourceIds = approvedEvidenceSourceByLeadItemId.Values.Distinct().ToArray();
                var currentOccurrenceId = await _db.Set<LeadRevision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.Id == command.ExpectedLeadRevisionId
                        && x.LeadId == leadId)
                    .Select(x => x.EstablishedByOccurrenceId).SingleAsync(ct);
                var currentSourceDocumentIds = await _db.Set<LeadOccurrenceDocument>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId
                        && x.OccurrenceId == currentOccurrenceId)
                    .Select(x => x.SourceDocumentId).Distinct().ToListAsync(ct);
                var directSourceDocumentId = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.Id == currentOccurrenceId
                        && x.LeadId == leadId)
                    .Select(x => x.SourceDocumentId).SingleAsync(ct);
                if (directSourceDocumentId.HasValue && !currentSourceDocumentIds.Contains(directSourceDocumentId.Value))
                    currentSourceDocumentIds.Add(directSourceDocumentId.Value);
                if (currentSourceDocumentIds.Count == 0)
                    throw new InvalidOperationException(
                        "The current revision has no exact retained source-document relation.");
                var approvedEvidenceObjects = await (from field in _db.Set<FieldEvidence>().AsNoTracking()
                    join job in _db.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                        on new { field.BusinessUnitId, Id = field.ExtractionRun.ExtractionJobId }
                        equals new { job.BusinessUnitId, job.Id }
                    where field.BusinessUnitId == businessUnitId && field.LineItem != null
                        && field.LineItem.LeadItemId.HasValue
                        && approvedEvidenceSourceIds.Contains(field.LineItem.LeadItemId.Value)
                        && currentSourceDocumentIds.Contains(field.ExtractionRun.SourceDocumentId)
                        && field.ExtractionRun.SourceDocument.SecurityStatus == DocumentSecurityStatus.Cleared
                        && field.ExtractionRun.SourceDocument.PurgeState == EvidencePurgeState.Present
                        && field.ExtractionRun.SourceDocument.ExtractionJobId == job.Id
                        && field.ExtractionRun.SourceDocument.ContentHash == job.ContentHash
                        && job.StoragePath != null && job.StoragePath != ""
                    select new
                    {
                        LeadItemId = field.LineItem!.LeadItemId!.Value,
                        SourceDocumentId = field.ExtractionRun.SourceDocumentId,
                        field.ExtractionRun.SourceDocument.ContentHash,
                        job.StoragePath
                    }).Distinct().ToListAsync(ct);
                var evidencedLeadItemIds = approvedEvidenceObjects.Select(x => x.LeadItemId).Distinct().ToArray();
                if (approvedEvidenceSourceIds.Any(id => !evidencedLeadItemIds.Contains(id)))
                    throw new InvalidOperationException(
                        "Every approved line must have exact persisted source-field evidence before RFQ promotion.");
                foreach (var evidenceObject in approvedEvidenceObjects
                             .GroupBy(x => x.SourceDocumentId).Select(x => x.First()))
                {
                    await using var verified = await _evidenceStorage.OpenVerifiedReadAsync(
                        evidenceObject.StoragePath, evidenceObject.ContentHash, ct);
                }

                var receiptEntity = new RfqPromotion
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = leadId,
                    LeadRevisionId = command.ExpectedLeadRevisionId,
                    ParticipationDecisionId = decision.Id,
                    IdempotencyKey = command.IdempotencyKey.Trim(),
                    RequestHash = requestHash,
                    PromotedBy = command.Actor.Trim(),
                    PromotedAtUtc = DateTimeOffset.UtcNow
                };
                _db.Add(receiptEntity);
                await _db.SaveChangesAsync(ct);

                var now = DateTime.UtcNow;
                var rfq = new Rfq
                {
                    Rfqno = await NextRfqNumberAsync(businessUnitId, ct),
                    BuyersName = lead.BuyersName,
                    RecDate = lead.RecDate,
                    BidClosingDate = lead.BidClosingDate,
                    AcknowledgmentDate = lead.AcknowledgmentDate,
                    SubDate = lead.SubDate,
                    HeaderRemarks = lead.HeaderRemarks,
                    OpportunityNo = lead.OpportunityNo,
                    Rfqtype = lead.Rfqtype,
                    DurationAgreement = lead.DurationAgreement,
                    LeadId = lead.Id,
                    CustomerId = lead.CustomerId,
                    BusinessUnitId = businessUnitId,
                    RfqstatusId = await LifecycleStatusCatalog.ResolveIdAsync(_db, businessUnitId, "Rfq", "DRAFT", ct),
                    CreatedBy = command.Actor.Trim(),
                    CreatedDate = now,
                    PromotionId = receiptEntity.Id,
                    SourceLeadRevisionId = command.ExpectedLeadRevisionId,
                    ParticipationDecisionId = decision.Id
                };
                rfq.InheritCommercialIdentity(lead);

                var currentLinesById = lead.LeadItems.ToDictionary(x => x.Id);
                var revisionById = revisionLines.ToDictionary(x => x.Id);
                foreach (var approved in decision.Lines.Where(x => x.Choice == LeadLineParticipationChoice.Bid)
                             .OrderBy(x => revisionById[x.LeadItemRevisionId].LineNumber))
                {
                    var revisionLine = revisionById[approved.LeadItemRevisionId];
                    var source = revisionLine.LeadItemId.HasValue
                        && currentLinesById.TryGetValue(revisionLine.LeadItemId.Value, out var linkedLeadItem)
                        ? linkedLeadItem
                        : throw new InvalidOperationException(
                            $"Current revision line {revisionLine.LineNumber} has no exact immutable canonical Lead-item lineage. Reconcile the source before promotion.");
                    // LeadItemRevision.SnapshotJson is an identity fingerprint snapshot: its text
                    // is deliberately normalized and must never become customer-facing RFQ data.
                    // Formal values come from the current canonical LeadItem, with only explicit
                    // human decision corrections allowed to override quote-critical fields.
                    var quantity = approved.Quantity ?? source.Quantity;
                    var uom = Clean(approved.UnitOfMeasure) ?? Clean(source.UnitOfMeasure);
                    var currency = Clean(approved.Currency)?.ToUpperInvariant() ?? Clean(source.Currency)?.ToUpperInvariant();
                    var part = Clean(source.ManufacturerPartNumber) ?? Clean(source.ItemMaterialCode);
                    var description = Clean(source.ProductShortDescription) ?? Clean(source.ProductShortName) ?? Clean(source.ItemText);
                    if (quantity is null or <= 0)
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no positive quantity.");
                    if (string.IsNullOrWhiteSpace(uom))
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no unit of measure.");
                    if (string.IsNullOrWhiteSpace(currency))
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no currency.");
                    if (string.IsNullOrWhiteSpace(part) && string.IsNullOrWhiteSpace(description))
                        throw new InvalidOperationException($"Approved line {revisionLine.LineNumber} has no part number or description.");

                    var item = new Rfqitem
                    {
                        CompanyRef = source.CompanyRef,
                        CustomerAccountPortalId = source.CustomerAccountPortalId,
                        CustomerRfqno = source.CustomerRfqno,
                        ItemMaterialCode = source.ItemMaterialCode ?? part,
                        LineItemNo = source.LineItemNo ?? revisionLine.LineNumber.ToString(),
                        ProductId = approved.ProductId,
                        CommodityProduct = source.CommodityProduct,
                        ProductShortName = source.ProductShortName ?? description,
                        ProductShortDescription = source.ProductShortDescription ?? description,
                        Alternative = source.Alternative,
                        BuyerName = source.BuyerName,
                        Currency = currency,
                        CurrencyId = approved.CurrencyId,
                        UnitOfMeasure = uom,
                        UomId = approved.UomId,
                        UnitPrice = source.UnitPrice,
                        Quantity = quantity,
                        StorageLocation = source.StorageLocation,
                        ManufacturerName = source.ManufacturerName,
                        ManufacturerPartNumber = source.ManufacturerPartNumber ?? part,
                        AlternateProductName = source.AlternateProductName,
                        AlternatePartNumber = source.AlternatePartNumber,
                        ItemText = source.ItemText,
                        MaterialPotext = source.MaterialPotext,
                        LeadTime = source.LeadTime,
                        ReceivedDate = source.ReceivedDate,
                        BidClosingDateLine = source.BidClosingDateLine,
                        RequiredDesiredDate = lead.RequiredDeliveryDate,
                        Aiconfidence = source.Aiconfidence,
                        CreatedBy = command.Actor.Trim(),
                        CreatedDate = now,
                        SourceBusinessUnitId = businessUnitId,
                        SourceLeadId = leadId,
                        SourceLeadRevisionId = command.ExpectedLeadRevisionId,
                        SourceLeadItemRevisionId = revisionLine.Id
                    };
                    // Compatibility projection for QuoteService: participation was decided on
                    // the Lead; every RFQ line is therefore already an explicit Quote line.
                    item.DecideParticipation(Rfqitem.ParticipationQuote, null, command.Actor, now);
                    rfq.Rfqitems.Add(item);
                }
                rfq.NoOfLineItems = rfq.Rfqitems.Count;
                _db.Rfqs.Add(rfq);
                await _db.SaveChangesAsync(ct);

                var lifecycle = new LifecycleApplicationService(_db);
                var actor = new LifecycleActor(command.Actor.Trim(), "AuthenticatedUser");
                await lifecycle.CompleteRfqPromotionInCurrentTransactionAsync(
                    businessUnitId, leadId, rfq.Id, receiptEntity.Id, command.ExpectedLeadRevisionId, decision.Id, actor,
                    new LifecycleTransitionCommand("CONVERTED_TO_RFQ", lead.LifecycleVersion, null, decision.Notes,
                        "Api", $"promotion-{receiptEntity.Id}", $"rfq-{rfq.Id}",
                        $"rfq-promotion:{businessUnitId}:{receiptEntity.Id}"), ct);
                await tx.CommitAsync(ct);
                return Result(receiptEntity, rfq, command.ExpectedDecisionVersion, decision.Sequence, replayed: false);
            });
        }
        catch (DbUpdateException ex) when (LeadConversionGate.IsDuplicateKey(ex))
        {
            _db.ChangeTracker.Clear();
            replay = await ReplayAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
            if (replay is not null) return replay;
            var winner = await _db.Rfqs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
            if (winner is null) throw;
            throw new InvalidOperationException($"Lead {leadId} was already promoted to RFQ #{winner.Id} ({winner.Rfqno}).");
        }
    }

    private async Task<RfqPromotionResult?> ReplayAsync(long businessUnitId, string key, string requestHash, CancellationToken ct)
    {
        var receipt = await _db.Set<RfqPromotion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct);
        if (receipt is null) return null;
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key was already used for a different RFQ promotion request.");
        var rfq = await _db.Rfqs.AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.PromotionId == receipt.Id, ct);
        var decision = await _db.Set<LeadParticipationDecision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == receipt.ParticipationDecisionId, ct);
        var revisionNumber = await _db.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == receipt.LeadRevisionId)
            .Select(x => x.RevisionNumber).SingleAsync(ct);
        return Result(receipt, rfq, revisionNumber, decision.Sequence, replayed: true);
    }

    private async Task<string> NextRfqNumberAsync(long businessUnitId, CancellationToken ct)
    {
        long sequence;
        if (_db.Database.IsNpgsql())
            sequence = await _db.Database.SqlQueryRaw<long>(
                "SELECT nextval('public.nexora_rfq_number_seq') AS \"Value\"").SingleAsync(ct);
        else
        {
            var numbers = await _db.Rfqs.IgnoreQueryFilters().AsNoTracking().Select(x => x.Rfqno).ToListAsync(ct);
            sequence = numbers.Select(x => System.Text.RegularExpressions.Regex.Match(x ?? "", "([0-9]+)$"))
                .Where(x => x.Success && long.TryParse(x.Groups[1].Value, out _))
                .Select(x => long.Parse(x.Groups[1].Value)).DefaultIfEmpty().Max() + 1;
        }
        return $"NXR-RFQ-{businessUnitId}-{DateTime.UtcNow:yyyy}-{sequence:D8}";
    }

    private static RfqPromotionResult Result(RfqPromotion p, Rfq r, int revisionNumber, int participationVersion, bool replayed) =>
        new(p.Id, r.Id, r.Rfqno, p.LeadId, p.LeadRevisionId, p.ParticipationDecisionId,
            revisionNumber, participationVersion, r.NoOfLineItems ?? r.Rfqitems.Count,
            p.PromotedAtUtc, p.PromotedBy, replayed);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

}
