using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed record ArchiveSearchRequest(string? Search, string? DocumentType, string? Status,
    DateTimeOffset? From, DateTimeOffset? To, string? Sort, int Page = 1, int PageSize = 50);

public sealed record ArchiveDocumentItem(long OccurrenceId, long SourceDocumentId, string FileName,
    string MimeType, long ByteSize, string ContentHash, DateTimeOffset IngestedOn, string IntakeStatus,
    string SecurityStatus, string ProcessingStatus, string DocumentType, string ReviewStatus,
    decimal? ClassificationConfidence, long? LeadId, string? NexoraSerial, string? CustomerRfq,
    long? CustomerId, long? ContactId, IReadOnlyList<string> CommercialLinks, bool LegalHold,
    bool DeletionRequested, int GovernanceVersion);

public sealed record ArchiveSearchResult(IReadOnlyList<ArchiveDocumentItem> Items, int Page,
    int PageSize, int Total, string SearchScope, string DefinitionVersion);

public sealed record ArchiveGovernanceCommand(int ExpectedVersion, string Action, string Reason);
public sealed record ArchiveGovernanceResult(long OccurrenceId, bool LegalHold, bool DeletionRequested,
    int GovernanceVersion, bool IdempotentReplay);

public sealed class CommercialDocumentArchiveService(ErpRfqAutomationContext db)
{
    private const string Area = "CommercialDocumentArchive";

    public async Task<ArchiveSearchResult> SearchAsync(long tenantId, ArchiveSearchRequest request,
        CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var query = from occurrence in db.Set<SourceDocumentOccurrence>().AsNoTracking()
                    join document in db.Set<SourceDocument>().AsNoTracking()
                        on new { occurrence.BusinessUnitId, Id = occurrence.SourceDocumentId }
                        equals new { document.BusinessUnitId, document.Id }
                    where occurrence.BusinessUnitId == tenantId
                    select new { Occurrence = occurrence, Document = document };

        if (request.From.HasValue) query = query.Where(x => x.Occurrence.ReceivedOn >= request.From.Value);
        if (request.To.HasValue) query = query.Where(x => x.Occurrence.ReceivedOn <= request.To.Value);
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var value = request.Status.Trim();
            var hasIntake = Enum.TryParse<IntakeOccurrenceStatus>(value, true, out var intakeStatus);
            var hasProcessing = Enum.TryParse<DocumentProcessingStatus>(value, true, out var processingStatus);
            var hasSecurity = Enum.TryParse<DocumentSecurityStatus>(value, true, out var securityStatus);
            query = query.Where(x => hasIntake && x.Occurrence.IntakeStatus == intakeStatus
                || hasProcessing && x.Document.ProcessingStatus == processingStatus
                || hasSecurity && x.Document.SecurityStatus == securityStatus);
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            var linkedOccurrenceIds = db.Set<LeadIngestionOccurrence>().AsNoTracking()
                .Where(x => x.BusinessUnitId == tenantId && x.SourceDocumentOccurrenceId.HasValue
                    && (x.OriginalFileName != null && x.OriginalFileName.ToLower().Contains(term)
                        || x.ExternalSourceId != null && x.ExternalSourceId.ToLower().Contains(term)
                        || x.Lead != null && (x.Lead.CommercialCaseReference.ToLower().Contains(term)
                            || x.Lead.Rfqno != null && x.Lead.Rfqno.ToLower().Contains(term)
                            || x.Lead.BuyersName != null && x.Lead.BuyersName.ToLower().Contains(term)
                            || x.Lead.Clientemail != null && x.Lead.Clientemail.ToLower().Contains(term)
                            || x.Lead.LeadItems.Any(line => line.ManufacturerPartNumber != null
                                && line.ManufacturerPartNumber.ToLower().Contains(term)
                                || line.AlternatePartNumber != null && line.AlternatePartNumber.ToLower().Contains(term)
                                || line.ItemMaterialCode != null && line.ItemMaterialCode.ToLower().Contains(term)))))
                .Select(x => x.SourceDocumentOccurrenceId!.Value);
            query = query.Where(x => x.Document.OriginalFileName.ToLower().Contains(term)
                || x.Document.ContentHash.ToLower().Contains(term)
                || x.Occurrence.LogicalGroupKey != null && x.Occurrence.LogicalGroupKey.ToLower().Contains(term)
                || linkedOccurrenceIds.Contains(x.Occurrence.Id));
        }

        if (!string.IsNullOrWhiteSpace(request.DocumentType))
        {
            if (!Enum.TryParse<CommercialDocumentType>(request.DocumentType.Trim(), true, out var documentType))
                query = query.Where(_ => false);
            else
            {
                var matchingDocumentIds = db.CommercialDocumentClassifications.AsNoTracking()
                    .Where(x => x.BusinessUnitId == tenantId && x.DocumentType == documentType)
                    .Select(x => x.SourceDocumentId);
                query = query.Where(x => matchingDocumentIds.Contains(x.Document.Id));
            }
        }

        var total = await query.CountAsync(ct);
        query = request.Sort?.Trim().ToLowerInvariant() switch
        {
            "oldest" => query.OrderBy(x => x.Occurrence.ReceivedOn).ThenBy(x => x.Occurrence.Id),
            "filename" => query.OrderBy(x => x.Document.OriginalFileName).ThenByDescending(x => x.Occurrence.ReceivedOn),
            _ => query.OrderByDescending(x => x.Occurrence.ReceivedOn).ThenByDescending(x => x.Occurrence.Id)
        };
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var documentIds = rows.Select(x => x.Document.Id).Distinct().ToArray();
        var occurrenceIds = rows.Select(x => x.Occurrence.Id).ToArray();

        var classifications = await db.CommercialDocumentClassifications.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && documentIds.Contains(x.SourceDocumentId))
            .OrderByDescending(x => x.Version).ToListAsync(ct);
        var classificationByDocument = classifications.GroupBy(x => x.SourceDocumentId)
            .ToDictionary(x => x.Key, x => x.First());
        var leadLinks = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.SourceDocumentOccurrenceId.HasValue
                && occurrenceIds.Contains(x.SourceDocumentOccurrenceId.Value))
            .Select(x => new
            {
                OccurrenceId = x.SourceDocumentOccurrenceId!.Value,
                x.LeadId,
                NexoraSerial = x.Lead == null ? null : x.Lead.CommercialCaseReference,
                CustomerRfq = x.Lead == null ? null : x.Lead.Rfqno,
                CustomerId = x.Lead == null ? null : x.Lead.CustomerId,
                ContactId = x.Lead == null ? null : x.Lead.ContactId
            })
            .ToListAsync(ct);
        var leadByOccurrence = leadLinks.GroupBy(x => x.OccurrenceId).ToDictionary(x => x.Key, x => x.First());
        var leadIds = leadLinks.Where(x => x.LeadId.HasValue).Select(x => x.LeadId!.Value).Distinct().ToArray();
        var rfqs = await db.Rfqs.AsNoTracking().Where(x => x.BusinessUnitId == tenantId
                && x.LeadId.HasValue && leadIds.Contains(x.LeadId.Value))
            .Select(x => new { LeadId = x.LeadId!.Value, x.Id, x.Rfqno }).ToListAsync(ct);
        var rfqIds = rfqs.Select(x => x.Id).ToArray();
        var quotes = await db.Quotes.AsNoTracking().Where(x => x.BusinessUnitId == tenantId
                && x.Rfqid.HasValue && rfqIds.Contains(x.Rfqid.Value))
            .Select(x => new { x.Rfqid, x.Id, x.QuoteNo }).ToListAsync(ct);
        var orders = await db.Orders.AsNoTracking().Where(x => x.BusinessUnitId == tenantId
                && x.LeadId.HasValue && leadIds.Contains(x.LeadId.Value))
            .Select(x => new { LeadId = x.LeadId!.Value, x.Id, x.OrderNo }).ToListAsync(ct);
        var references = leadIds.ToDictionary(id => id, id =>
            (IReadOnlyList<string>)rfqs.Where(x => x.LeadId == id).Select(x => $"RFQ:{x.Rfqno}")
                .Concat(quotes.Where(x => rfqs.Any(r => r.LeadId == id && r.Id == x.Rfqid))
                    .Select(x => $"QUOTE:{x.QuoteNo}"))
                .Concat(orders.Where(x => x.LeadId == id).Select(x => $"ORDER:{x.OrderNo}"))
                .Distinct().ToArray());

        var aggregateReferences = occurrenceIds.Select(Reference).ToArray();
        var governanceEvents = await db.TenantGovernanceAuditEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.Area == Area
                && aggregateReferences.Contains(x.AggregateReference))
            .OrderBy(x => x.OccurredOn).ThenBy(x => x.Id).ToListAsync(ct);
        var stateByOccurrence = occurrenceIds.ToDictionary(id => id,
            id => State(governanceEvents.Where(x => x.AggregateReference == Reference(id))));

        var items = rows.Select(row =>
        {
            classificationByDocument.TryGetValue(row.Document.Id, out var classification);
            leadByOccurrence.TryGetValue(row.Occurrence.Id, out var lead);
            var state = stateByOccurrence[row.Occurrence.Id];
            return new ArchiveDocumentItem(row.Occurrence.Id, row.Document.Id,
                row.Document.OriginalFileName, row.Document.DetectedMimeType, row.Document.ByteSize,
                row.Document.ContentHash, row.Occurrence.ReceivedOn, row.Occurrence.IntakeStatus.ToString(),
                row.Document.SecurityStatus.ToString(), row.Document.ProcessingStatus.ToString(),
                classification?.DocumentType.ToString() ?? "Unclassified",
                classification?.ReviewStatus.ToString() ?? "NotReviewed", classification?.Confidence,
                lead?.LeadId, lead?.NexoraSerial, lead?.CustomerRfq, lead?.CustomerId, lead?.ContactId,
                lead?.LeadId is long leadId && references.TryGetValue(leadId, out var links) ? links : [],
                state.LegalHold, state.DeletionRequested, state.Version);
        }).ToList();

        return new(items, page, pageSize, total,
            "Tenant metadata, filenames, immutable hashes, Nexora Serial, customer RFQ, buyer/contact, part numbers and linked commercial records. Document bytes and extracted customer text are not returned.",
            "archive-search/v1");
    }

    public async Task<ArchiveGovernanceResult> GovernAsync(long tenantId, long actorUserId,
        long occurrenceId, string idempotencyKey, ArchiveGovernanceCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        var action = PlatformGovernanceService.Required(command.Action, 48, "Action is required.")
            .ToUpperInvariant();
        if (action is not ("ACCESS_RECORDED" or "HOLD_APPLIED" or "HOLD_RELEASED"
            or "EXPORT_REQUESTED" or "DELETION_REQUESTED" or "DELETION_CANCELLED"))
            throw new PlatformGovernanceValidationException("Unsupported archive governance action.");
        var reason = PlatformGovernanceService.Required(command.Reason, 1000,
            "A governance reason is required.");

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await db.TenantGovernanceAuditEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
            if (replay is not null)
            {
                var replayEvents = await EventsAsync(tenantId, occurrenceId, ct);
                var replayState = State(replayEvents);
                return new(occurrenceId, replayState.LegalHold, replayState.DeletionRequested,
                    replayState.Version, true);
            }
            if (!await db.Set<SourceDocumentOccurrence>().AsNoTracking().AnyAsync(x =>
                    x.BusinessUnitId == tenantId && x.Id == occurrenceId, ct))
                throw new PlatformGovernanceNotFoundException("The archived document occurrence was not found.");
            var events = await EventsAsync(tenantId, occurrenceId, ct);
            var state = State(events);
            if (command.ExpectedVersion != state.Version)
                throw new PlatformGovernanceConflictException(
                    $"Archive governance version is {state.Version}; refresh and retry.");
            if (action == "DELETION_REQUESTED" && state.LegalHold)
                throw new PlatformGovernanceConflictException("Deletion cannot be requested while legal hold is active.");
            if (action == "HOLD_RELEASED" && !state.LegalHold)
                throw new PlatformGovernanceConflictException("The document is not on legal hold.");

            var next = state with
            {
                LegalHold = action == "HOLD_APPLIED" || action != "HOLD_RELEASED" && state.LegalHold,
                DeletionRequested = action == "DELETION_REQUESTED"
                    || action != "DELETION_CANCELLED" && state.DeletionRequested,
                Version = state.Version + 1
            };
            db.TenantGovernanceAuditEvents.Add(new()
            {
                BusinessUnitId = tenantId,
                Area = Area,
                AggregateType = "SourceDocumentOccurrence",
                AggregateReference = Reference(occurrenceId),
                Action = action,
                Reason = reason,
                EvidenceJson = JsonSerializer.Serialize(new { before = state, after = next }),
                IdempotencyKey = idempotencyKey,
                ActorUserId = actorUserId,
                OccurredOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ArchiveGovernanceResult(occurrenceId, next.LegalHold,
                next.DeletionRequested, next.Version, false);
        });
    }

    private Task<List<TenantGovernanceAuditEvent>> EventsAsync(long tenantId, long occurrenceId,
        CancellationToken ct) => db.TenantGovernanceAuditEvents.AsNoTracking().Where(x =>
            x.BusinessUnitId == tenantId && x.Area == Area && x.AggregateReference == Reference(occurrenceId))
            .OrderBy(x => x.OccurredOn).ThenBy(x => x.Id).ToListAsync(ct);

    private static ArchiveState State(IEnumerable<TenantGovernanceAuditEvent> events)
    {
        var state = new ArchiveState(false, false, 0);
        foreach (var audit in events)
            state = state with
            {
                LegalHold = audit.Action == "HOLD_APPLIED" || audit.Action != "HOLD_RELEASED" && state.LegalHold,
                DeletionRequested = audit.Action == "DELETION_REQUESTED"
                    || audit.Action != "DELETION_CANCELLED" && state.DeletionRequested,
                Version = state.Version + 1
            };
        return state;
    }

    private static string Reference(long occurrenceId) => $"occurrence:{occurrenceId}";
    private sealed record ArchiveState(bool LegalHold, bool DeletionRequested, int Version);
}
