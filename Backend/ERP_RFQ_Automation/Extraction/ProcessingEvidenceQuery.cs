using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Extraction;

public static class ProcessingEvidenceQuery
{
    public static async Task<ProcessingEvidenceResponse?> ReadRfqAsync(
        ErpRfqAutomationContext db, CommercialActorScope actor, long rfqId, CancellationToken ct)
    {
        var leadId = await db.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .Where(x => x.Id == rfqId)
            .Select(x => x.LeadId)
            .SingleOrDefaultAsync(ct);
        return leadId.HasValue
            ? await ReadLeadAsync(db, actor, leadId.Value, ct)
            : null;
    }

    public static async Task<ProcessingEvidenceResponse?> ReadSupplierQuoteAsync(
        ErpRfqAutomationContext db, CommercialActorScope actor, long supplierQuoteId, CancellationToken ct)
    {
        var rfqId = await db.Set<SupplierQuote>().AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId && x.Id == supplierQuoteId)
            .Select(x => (long?)x.RfqId)
            .SingleOrDefaultAsync(ct);
        return rfqId.HasValue
            ? await ReadRfqAsync(db, actor, rfqId.Value, ct)
            : null;
    }

    public static async Task<ProcessingEvidenceResponse?> ReadClientPurchaseOrderAsync(
        ErpRfqAutomationContext db, CommercialActorScope actor, long clientPurchaseOrderId, CancellationToken ct)
    {
        var commercialCaseId = await db.CustomerPurchaseOrders.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId && x.Id == clientPurchaseOrderId)
            .Select(x => (long?)x.CommercialCaseId)
            .SingleOrDefaultAsync(ct);
        if (!commercialCaseId.HasValue)
            return null;

        var leadId = await db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .Where(x => x.CommercialCaseId == commercialCaseId.Value)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(ct);
        return leadId.HasValue
            ? await ReadLeadAsync(db, actor, leadId.Value, ct)
            : null;
    }

    public static async Task<ProcessingEvidenceResponse?> ReadLeadAsync(
        ErpRfqAutomationContext db, CommercialActorScope actor, long leadId, CancellationToken ct)
    {
        var businessUnitId = actor.BusinessUnitId;
        var lead = await db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .InCommercialScope(db, businessUnitId, actor.AccountScope, DateTime.UtcNow)
            .Where(x => x.Id == leadId)
            .Select(x => new { x.Id, NexoraSerial = x.CommercialCaseReference })
            .SingleOrDefaultAsync(ct);
        if (lead is null)
            return null;

        var leadOccurrences = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId)
            .ToListAsync(ct);
        leadOccurrences = leadOccurrences.OrderBy(x => x.CreatedAtUtc).ToList();
        var jobIds = leadOccurrences.Where(x => x.ExtractionJobId.HasValue)
            .Select(x => x.ExtractionJobId!.Value).Distinct().ToList();
        jobIds.AddRange(await db.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.ResultLeadId == leadId)
            .Select(x => x.Id).ToListAsync(ct));
        jobIds = jobIds.Distinct().ToList();

        var jobs = await db.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && jobIds.Contains(x.Id))
            .OrderBy(x => x.CreatedOn).ToListAsync(ct);
        var occurrenceIds = leadOccurrences.Where(x => x.SourceDocumentOccurrenceId.HasValue)
            .Select(x => x.SourceDocumentOccurrenceId!.Value)
            .Concat(jobs.Where(x => x.SourceDocumentOccurrenceId.HasValue)
                .Select(x => x.SourceDocumentOccurrenceId!.Value))
            .Distinct().ToArray();
        var sourceOccurrences = await db.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && occurrenceIds.Contains(x.Id))
            .ToListAsync(ct);
        sourceOccurrences = sourceOccurrences.OrderBy(x => x.ReceivedOn).ToList();

        var sourceDocumentIds = sourceOccurrences.Select(x => x.SourceDocumentId)
            .Concat(leadOccurrences.Where(x => x.SourceDocumentId.HasValue)
                .Select(x => x.SourceDocumentId!.Value)).Distinct().ToArray();
        var sourceDocuments = await db.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && sourceDocumentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var runs = await db.Set<ExtractionRun>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && jobIds.Contains(x.ExtractionJobId))
            .ToListAsync(ct);
        runs = runs.OrderBy(x => x.CreatedOn).ToList();
        var aiRequests = await db.Set<AiRequest>().AsNoTracking().Include(x => x.Attempts)
            .Where(x => x.BusinessUnitId == businessUnitId
                && ((x.ExtractionJobId.HasValue && jobIds.Contains(x.ExtractionJobId.Value))
                    || (x.SourceDocumentOccurrenceId.HasValue
                        && occurrenceIds.Contains(x.SourceDocumentOccurrenceId.Value))))
            .ToListAsync(ct);
        aiRequests = aiRequests.OrderBy(x => x.CreatedOn).ToList();
        var rfqs = await db.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId)
            .OrderBy(x => x.CreatedDate)
            .Select(x => new ProcessingRfqEvidence(x.Id, x.Rfqno, x.CreatedDate))
            .ToListAsync(ct);

        var leadOccurrenceBySource = leadOccurrences
            .Where(x => x.SourceDocumentOccurrenceId.HasValue)
            .GroupBy(x => x.SourceDocumentOccurrenceId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.CreatedAtUtc).First());
        var occurrenceEvidence = sourceOccurrences.Select(x =>
        {
            leadOccurrenceBySource.TryGetValue(x.Id, out var linked);
            sourceDocuments.TryGetValue(x.SourceDocumentId, out var source);
            return new ProcessingOccurrenceEvidence(x.Id, x.SourceDocumentId, x.ExtractionJobId,
                x.IntakeStatus.ToString(), x.ReceivedOn, source?.OriginalFileName, source?.ContentHash,
                linked?.Classification.ToString(), linked?.CorrelationId);
        }).ToArray();

        var external = aiRequests.Where(x => x.ProviderClass == AiProviderClass.External).ToArray();
        var localCount = aiRequests.Count(x => x.ProviderClass == AiProviderClass.Local);
        var externalCount = external.Length;
        var classifiedCount = localCount + externalCount;
        var externalCost = ProcessingCostAttribution.Summarize(external);

        return new ProcessingEvidenceResponse(lead.Id, lead.NexoraSerial, rfqs, occurrenceEvidence,
            jobs.Select(x => new ProcessingJobEvidence(x.Id, x.SourceDocumentOccurrenceId,
                x.SourceType.ToString(), x.Status.ToString(), x.Attempts, x.MaxAttempts,
                x.ResultLeadId?.ToString(), x.LastError, x.CreatedOn, x.UpdatedOn)).ToArray(),
            runs.Select(x => new ProcessingRunEvidence(x.Id, x.RunId, x.SourceDocumentId,
                x.ExtractionJobId, x.AttemptNumber, x.Status.ToString(), x.ProcessingPath.ToString(),
                x.OcrStatus.ToString(), x.OcrPageCount, x.OcrTruncated, x.ProcessingCostAmount,
                x.ProcessingCostCurrency, x.ProcessingCostStatus, x.OcrCostStatus, x.ParserVersion,
                x.SchemaVersion, x.FailureReason, x.CreatedOn, x.CompletedOn)).ToArray(),
            aiRequests.Select(x => new ProcessingAiRequestEvidence(x.Id, x.ExtractionJobId,
                x.SourceDocumentOccurrenceId, x.Provider, x.ProviderClass.ToString(), x.Model,
                x.PromptVersion, x.Operation, x.Status, x.CostStatus,
                x.EstimatedCost, x.CostCurrency, x.CostPricingVersion, x.BudgetWarning,
                x.InputTokens, x.OutputTokens,
                x.Attempts.OrderBy(a => a.AttemptNumber).Select(a => new ProcessingAiAttemptEvidence(
                    a.AttemptNumber, a.Provider, a.Model, a.Status, a.HttpStatus,
                    a.ErrorCode, a.InputTokens, a.OutputTokens, a.LatencyMilliseconds,
                    a.StartedOn, a.CompletedOn)).ToArray())).ToArray(),
            localCount, externalCount,
            classifiedCount == 0 ? 0m : decimal.Round((decimal)localCount / classifiedCount, 4),
            classifiedCount == 0 ? 0m : decimal.Round((decimal)externalCount / classifiedCount, 4),
            externalCost.Amount, externalCost.Currency, externalCost.Status);
    }
}
