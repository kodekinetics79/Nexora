using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Deduplication;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Services.Uom;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.LeadIdentity;

public interface ILeadIdentityApplicationService
{
    Task<LeadReconciliationResult> ReconcileAsync(Lead candidate, LeadIntakeDescriptor intake, CancellationToken ct = default);
    Task<BatchReconciliationDto?> GetBatchAsync(long businessUnitId, Guid batchId, CancellationToken ct = default);
    Task<IReadOnlyList<PossibleMatchQueueItemDto>> GetPossibleMatchesAsync(long businessUnitId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateUploadDto>> GetDuplicateUploadsAsync(long businessUnitId, CancellationToken ct = default);
    Task<IReadOnlyList<LeadRevisionDto>> GetRevisionsAsync(long businessUnitId, long leadId, CancellationToken ct = default);
    Task<LeadReconciliationResult> DecideMatchAsync(long businessUnitId, long occurrenceId, MatchDecisionRequest request, string actorId, CancellationToken ct = default);
    Task<LeadIdentityAnalyticsDto> GetAnalyticsAsync(long businessUnitId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Gives a Lead that has no canonical identity its revision 1, from its own stored
    /// commercial facts. Safe to call unconditionally — a lead that already has one is untouched.</summary>
    Task<LeadReconciliationResult> EstablishBaselineRevisionAsync(
        long businessUnitId, long leadId, LeadIdentityBaselineRequest request, CancellationToken ct = default);
}

public sealed class LeadIdentityApplicationService : ILeadIdentityApplicationService
{
    private const string PolicyVersion = "release-01a/v1";
    private const string BaselinePolicyVersion = "release-01a/baseline-v1";
    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.Compiled);
    private readonly ErpRfqAutomationContext _db;
    private readonly ICommercialRoutingApplicationService? _routing;

    public LeadIdentityApplicationService(ErpRfqAutomationContext db, ICommercialRoutingApplicationService? routing = null)
    { _db = db; _routing = routing; }

    public Task<LeadReconciliationResult> ReconcileAsync(Lead candidate, LeadIntakeDescriptor intake, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null || !_db.Database.IsRelational())
            return ReconcileCoreAsync(candidate, intake, ct);
        return _db.Database.CreateExecutionStrategy().ExecuteAsync(
            () => ReconcileCoreAsync(candidate, intake, ct));
    }

    private async Task<LeadReconciliationResult> ReconcileCoreAsync(
        Lead candidate, LeadIntakeDescriptor intake, CancellationToken ct)
    {
        if (candidate.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(candidate.BusinessUnitId));
        if (string.IsNullOrWhiteSpace(intake.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.");
        if (intake.ExternalAiUsed && intake.ProcessingPath != LeadProcessingPath.ExternalModel)
            throw new InvalidOperationException("External use must be authoritatively classified as ExternalModel.");

        var fingerprint = Fingerprint(candidate);
        var scope = CustomerScope(candidate, intake.Sender);
        var normalizedRfq = Normalize(candidate.Rfqno ?? candidate.LeadItems.Select(x => x.CustomerRfqno).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;
        var tx = _db.Database.CurrentTransaction!;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({candidate.BusinessUnitId + ":" + (scope is not null && normalizedRfq is not null ? scope + ":" + normalizedRfq : intake.ExternalSourceId ?? intake.ContentHash ?? fingerprint)}, 0))", ct);
        // THE EXTERNAL-DEPENDENCY CEILING IS NOT ENFORCED HERE — DELIBERATELY.
        //
        // This used to re-implement it: last 100 occurrences, hardcoded .10m, throw on
        // breach. Three things were wrong with that, and together they cost 1,133 paid,
        // successful AI calls that produced zero leads on 2026-08-06.
        //
        // 1. WRONG PLACE. A ceiling on external egress is a decision about whether to
        //    MAKE a call. AiGovernanceService.ReserveAsync makes it, before the call,
        //    and every AI request in this product goes through that reserve/attempt/
        //    settle ledger. By the time control reaches here the call has been
        //    authorized, made, paid for and has returned. Refusing to persist the result
        //    does not prevent the egress that already happened — it only destroys the
        //    work, and guarantees the retry does the same thing again.
        // 2. WRONG NUMBER. It used a literal .10m while the tenant's configured
        //    AiProcessingPolicy.ExternalDependencyCeilingPercent sat in the Trust Center
        //    being ignored — the same defect already fixed in AiGovernanceService.
        // 3. NO EXEMPTION. AiGovernanceService exempts endpoints the tenant explicitly
        //    authorized through the allow-list, precisely because "on a deployment with
        //    no local model the ratio is always 100%". This copy never got that fix, so
        //    on THIS deployment it refused every document after roughly the tenth.
        //
        // Its message also promised "route this occurrence to human review" while
        // throwing — which dead-letters the document instead of routing it anywhere.
        //
        // ExternalAiUsed is still recorded on the occurrence below for audit and for the
        // Trust Center's dependency reporting. Enforcement stays where it can act:
        // AiGovernanceService, before egress.

        var replay = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == candidate.BusinessUnitId && x.IdempotencyKey == intake.IdempotencyKey)
            .Select(x => new { x.Id, x.LeadId, x.LeadRevisionId, x.Classification, x.Confidence, x.DecisionReasonsJson }).SingleOrDefaultAsync(ct);
        if (replay is not null)
        {
            var existingLead = replay.LeadId.HasValue
                ? await _db.Leads.AsNoTracking().SingleAsync(x => x.Id == replay.LeadId.Value, ct)
                : null;
            if (ownsTransaction) await tx.CommitAsync(ct);
            return new(existingLead?.Id ?? 0, existingLead?.CommercialCaseReference ?? string.Empty, replay.Id, replay.LeadRevisionId,
                existingLead?.CurrentRevisionNumber ?? 0, replay.Classification, replay.Confidence,
                JsonSerializer.Deserialize<string[]>(replay.DecisionReasonsJson) ?? [], false);
        }

        var exactLeadId = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(o => o.BusinessUnitId == candidate.BusinessUnitId && o.LeadId.HasValue && o.LogicalInquiryFingerprint == fingerprint
                && ((!string.IsNullOrWhiteSpace(intake.ExternalSourceId) && o.SourceChannel == intake.SourceChannel && o.ExternalSourceId == intake.ExternalSourceId)
                    || (!string.IsNullOrWhiteSpace(intake.ContentHash) && scope != null && o.ContentHash == intake.ContentHash && o.CustomerScopeKey == scope)))
            .OrderByDescending(o => o.Id).Select(o => o.LeadId).FirstOrDefaultAsync(ct);
        if (exactLeadId.HasValue)
        {
            var exact = await _db.Leads.AsNoTracking().SingleAsync(x => x.BusinessUnitId == candidate.BusinessUnitId && x.Id == exactLeadId.Value, ct);
            var occurrence = NewOccurrence(candidate.BusinessUnitId, intake, fingerprint, scope,
                LeadOccurrenceClassification.ExactDuplicate, 1m, ["High-trust source identity or exact content within the same customer scope."], exact.Id, exact.CurrentRevisionId);
            await EnsureBatchAsync(candidate.BusinessUnitId, intake, ct); _db.Add(occurrence);
            // Saved before the audit so the event carries the real occurrence id: an audit row
            // that cannot be joined back to its occurrence is not an audit trail.
            await _db.SaveChangesAsync(ct);
            AddAudit(occurrence, exact.Id, "INGESTION_DUPLICATE_RECORDED", intake, new { exact.CurrentRevisionNumber });
            await _db.SaveChangesAsync(ct); if (ownsTransaction) await tx.CommitAsync(ct);
            return new(exact.Id, exact.CommercialCaseReference, occurrence.Id, exact.CurrentRevisionId,
                exact.CurrentRevisionNumber, occurrence.Classification, occurrence.Confidence, occurrence.DecisionReasons(), false);
        }

        var candidates = await _db.Leads.Include(x => x.LeadItems)
            .Where(x => x.BusinessUnitId == candidate.BusinessUnitId)
            .OrderByDescending(x => x.CreatedDate).Take(250).ToListAsync(ct);

        var strongLeadId = scope is not null && normalizedRfq is not null
            ? await _db.Set<LeadRevision>().AsNoTracking()
                .Where(r => r.BusinessUnitId == candidate.BusinessUnitId && r.NormalizedCustomerRfqReference == normalizedRfq
                    && _db.Set<LeadIngestionOccurrence>().Any(o => o.BusinessUnitId == r.BusinessUnitId && o.LeadId == r.LeadId && o.CustomerScopeKey == scope))
                .OrderByDescending(r => r.RevisionNumber).Select(r => (long?)r.LeadId).FirstOrDefaultAsync(ct)
            : null;
        var strong = strongLeadId.HasValue
            ? candidates.FirstOrDefault(x => x.Id == strongLeadId.Value)
                ?? await _db.Leads.Include(x => x.LeadItems).SingleAsync(x => x.BusinessUnitId == candidate.BusinessUnitId && x.Id == strongLeadId.Value, ct)
            : candidates.FirstOrDefault(x => scope is not null && CustomerScope(x, null) == scope
                && normalizedRfq is not null && CustomerReference(x) == normalizedRfq);
        if (strong is not null)
            return await CreateRevisionAsync(strong, candidate, intake, fingerprint, scope,
                ["Same tenant, customer scope and normalized customer RFQ reference with changed commercial content."], tx, ownsTransaction, ct);

        var groupedLeadIds = string.IsNullOrWhiteSpace(intake.LogicalGroupKey)
            ? Array.Empty<long>()
            : await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
                .Where(x => x.BusinessUnitId == candidate.BusinessUnitId
                    && x.LogicalGroupKey == intake.LogicalGroupKey && x.LeadId.HasValue)
                .Select(x => x.LeadId!.Value).Distinct().ToArrayAsync(ct);
        // Every tenant-scoped candidate scored once, with its identity evidence resolved on both
        // axes. Ordering is by EVIDENCE first and similarity second: the lead whose customer and
        // reference corroborate is the one to offer a reviewer, even when a commodity line item
        // makes an unrelated lead score marginally higher.
        var scored = candidates
            .Select(x => new MatchAssessment(x, Similarity(candidate, x),
                Evidence(scope, CustomerScope(x, null)),
                ReferenceEvidence(normalizedRfq, CustomerReference(x)),
                ReferenceAmends(normalizedRfq, CustomerReference(x)),
                groupedLeadIds.Contains(x.Id),
                DuplicateRules.DuplicateReason(candidate, x)))
            .ToList();

        // The revision arms below stay gated on commercial-content similarity alone. FR-RFQ-06
        // duplicates are deliberately NOT admitted here: that rule can fire on buyer and dates
        // with poor line overlap, and a low-similarity match must never auto-link as a revision.
        var assessments = scored
            .Where(x => x.Score >= PossibleMatchThreshold)
            .OrderByDescending(x => x.EvidenceRank).ThenByDescending(x => x.Score)
            .ToList();

        // Same logical document group (one mail message, one upload set) plus corroborating
        // customer identity or reference. The group key is itself strong evidence, so this arm
        // keeps its lower similarity bar — but it no longer fires when the buyer's own reference
        // says the two documents are different inquiries.
        var grouped = assessments.FirstOrDefault(x => x.Grouped && !x.Contradicted
            && (x.Scope == MatchEvidence.Corroborating || x.Reference == MatchEvidence.Corroborating));
        if (grouped is not null)
            return await CreateRevisionAsync(grouped.Lead, candidate, intake, fingerprint, scope,
                ["Corroborated logical document group, customer identity, and commercial content."], tx, ownsTransaction, ct);

        var ranked = assessments.FirstOrDefault();

        // FR-RFQ-06: same buyer, same item, overlapping dates — held for human review BEFORE any
        // record is created. Content similarity may be well under the match threshold here (two
        // extractions of one tender can disagree on wording), which is precisely why this arm
        // exists rather than relying on the similarity score. A contradicted candidate is
        // excluded: when the buyer's own reference says these are different inquiries, they are.
        ranked ??= scored
            .Where(x => x.BrdDuplicateReason is not null && !x.Contradicted)
            .OrderBy(x => x.Lead.CreatedDate).ThenBy(x => x.Lead.Id)
            .FirstOrDefault();

        if (ranked is not null && !ranked.Contradicted)
        {
            // An amendment whose reference gained a revision marker ("RFQ-4471" -> "RFQ-4471 Rev B")
            // from a customer already on the lead, with near-identical commercial content, is a
            // version of that inquiry. Anything short of that stays a human decision.
            if (ranked.Scope == MatchEvidence.Corroborating
                && ranked.Reference == MatchEvidence.Corroborating
                && ranked.ReferenceAmends
                && ranked.Score >= ConfidentRevisionThreshold)
                return await CreateRevisionAsync(ranked.Lead, candidate, intake, fingerprint, scope,
                    ["Same tenant and customer, an amended form of the same customer RFQ reference, and near-identical commercial content."],
                    tx, ownsTransaction, ct);

            var occurrence = NewOccurrence(candidate.BusinessUnitId, intake, fingerprint, scope,
                LeadOccurrenceClassification.PossibleMatchReviewRequired, ranked.Score,
                [ranked.BrdDuplicateReason is { } duplicateReason ? duplicateReason
                    : ranked.Grouped
                    ? "Documents share a logical group and similar content, but canonical identity requires review."
                    : ranked.Reference == MatchEvidence.Corroborating
                        ? "The customer RFQ reference and commercial content match an existing inquiry, but customer identity is unresolved or differs."
                        : ranked.Scope == MatchEvidence.Corroborating
                            ? "The same customer's commercial content matches an existing inquiry, and no customer RFQ reference confirms whether it is an amendment."
                            : "Commercial content is similar, but customer identity is unresolved or conflicting."], null, null);
            await EnsureBatchAsync(candidate.BusinessUnitId, intake, ct); _db.Add(occurrence);
            await _db.SaveChangesAsync(ct);
            _db.Add(new LeadMatchCandidate { BusinessUnitId = candidate.BusinessUnitId, Occurrence = occurrence,
                CandidateLeadId = ranked.Lead.Id, Confidence = ranked.Score, ReviewState = LeadMatchReviewState.Pending,
                MatchEvidenceJson = JsonSerializer.Serialize(new { lineOverlap = ranked.Score, policy = PolicyVersion,
                    customerIdentity = ranked.Scope.ToString(), customerReference = ranked.Reference.ToString(), logicalGroup = ranked.Grouped }),
                DifferencesJson = Diff(Snapshot(ranked.Lead), Snapshot(candidate)),
                // The buyer's real values, kept verbatim for the human decision. DifferencesJson
                // above is normalised hash input and must never be projected onto a Lead.
                ProposedLeadSnapshotJson = VerbatimSnapshotJson(candidate),
                DownstreamImpactJson = await DownstreamImpactJsonAsync(ranked.Lead, ct) });
            AddAudit(occurrence, null, "POSSIBLE_MATCH_RAISED", intake, new { candidateLeadId = ranked.Lead.Id, ranked.Score,
                customerIdentity = ranked.Scope.ToString(), customerReference = ranked.Reference.ToString() });
            await _db.SaveChangesAsync(ct); if (ownsTransaction) await tx.CommitAsync(ct);
            return new(0, string.Empty, occurrence.Id, null, 0, occurrence.Classification, occurrence.Confidence, occurrence.DecisionReasons(), false);
        }

        await EnsureBatchAsync(candidate.BusinessUnitId, intake, ct);
        _db.Add(candidate); await _db.SaveChangesAsync(ct);
        // A rejected high-similarity candidate is named in the decision reasons. "New" is a
        // decision, and the evidence behind it has to be readable in the batch view.
        string[] newReasons = ranked is null
            ? ["No reliable tenant-scoped canonical inquiry match."]
            : ["No reliable tenant-scoped canonical inquiry match.",
               ranked.Reference == MatchEvidence.Contradicting
                   ? $"Commercial content resembles {ranked.Lead.CommercialCaseReference}, but the customer's own RFQ reference identifies a different inquiry."
                   : $"Commercial content resembles {ranked.Lead.CommercialCaseReference}, but it was received from a different customer."];
        var newOccurrence = NewOccurrence(candidate.BusinessUnitId, intake, fingerprint, scope,
            LeadOccurrenceClassification.New, 1m, newReasons, candidate.Id, null);
        _db.Add(newOccurrence); await _db.SaveChangesAsync(ct);
        var revision = BuildRevision(candidate, newOccurrence, 1, fingerprint, intake);
        _db.Add(revision); await _db.SaveChangesAsync(ct);
        candidate.CurrentRevisionId = revision.Id; candidate.CurrentRevisionNumber = 1; candidate.CurrentInquiryFingerprint = fingerprint;
        candidate.CurrentOccurrenceClassification = LeadOccurrenceClassification.New.ToString(); candidate.SourceReceivedAtUtc = intake.SourceReceivedAtUtc;
        candidate.IngestedAtUtc = intake.IngestedAtUtc; newOccurrence.LeadRevisionId = revision.Id;
        AddAudit(newOccurrence, candidate.Id, "LEAD_CREATED", intake, new { revision = 1, candidate.CommercialCaseReference });
        await _db.SaveChangesAsync(ct); if (ownsTransaction) await tx.CommitAsync(ct);
        return new(candidate.Id, candidate.CommercialCaseReference, newOccurrence.Id, revision.Id, 1,
            newOccurrence.Classification, newOccurrence.Confidence, newOccurrence.DecisionReasons(), true);
    }

    private async Task<LeadReconciliationResult> CreateRevisionAsync(Lead canonical, Lead incoming, LeadIntakeDescriptor intake,
        string fingerprint, string? scope, string[] reasons, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, bool ownsTransaction, CancellationToken ct)
    {
        var matchingRevision = await _db.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == canonical.BusinessUnitId && x.LeadId == canonical.Id
                && x.LogicalInquiryFingerprint == fingerprint)
            .OrderByDescending(x => x.RevisionNumber)
            .Select(x => new { x.Id, x.RevisionNumber })
            .FirstOrDefaultAsync(ct);
        if (matchingRevision is not null)
        {
            var duplicate = NewOccurrence(canonical.BusinessUnitId, intake, fingerprint, scope, LeadOccurrenceClassification.ExactDuplicate, 1m,
                ["Strong business identity and content exactly matches an immutable canonical revision."], canonical.Id, matchingRevision.Id);
            await EnsureBatchAsync(canonical.BusinessUnitId, intake, ct); _db.Add(duplicate);
            await _db.SaveChangesAsync(ct);
            AddAudit(duplicate, canonical.Id, "INGESTION_DUPLICATE_RECORDED", intake,
                new { matchedRevision = matchingRevision.RevisionNumber, currentRevision = canonical.CurrentRevisionNumber });
            await _db.SaveChangesAsync(ct); if (ownsTransaction) await tx.CommitAsync(ct);
            return new(canonical.Id, canonical.CommercialCaseReference, duplicate.Id, matchingRevision.Id, canonical.CurrentRevisionNumber,
                duplicate.Classification, duplicate.Confidence, duplicate.DecisionReasons(), false);
        }
        await EnsureBatchAsync(canonical.BusinessUnitId, intake, ct);
        var occurrence = NewOccurrence(canonical.BusinessUnitId, intake, fingerprint, scope, LeadOccurrenceClassification.Revision, .98m, reasons, canonical.Id, null);
        _db.Add(occurrence); await _db.SaveChangesAsync(ct);
        var next = canonical.CurrentRevisionNumber + 1;
        var revision = BuildRevision(incoming, occurrence, next, fingerprint, intake);
        revision.LeadId = canonical.Id;
        var previousSnapshot = canonical.CurrentRevisionId.HasValue
            ? await _db.Set<LeadRevision>().Where(x => x.Id == canonical.CurrentRevisionId.Value).Select(x => x.SnapshotJson).SingleOrDefaultAsync(ct)
            : JsonSerializer.Serialize(Snapshot(canonical));
        foreach (var d in Diff(previousSnapshot, revision.SnapshotJson))
        {
            d.BusinessUnitId = canonical.BusinessUnitId;
            revision.Differences.Add(d);
        }
        _db.Add(revision); await _db.SaveChangesAsync(ct);
        occurrence.LeadRevisionId = revision.Id; canonical.CurrentRevisionId = revision.Id; canonical.CurrentRevisionNumber = next;
        canonical.CurrentInquiryFingerprint = fingerprint; canonical.CurrentOccurrenceClassification = LeadOccurrenceClassification.Revision.ToString();
        canonical.IngestedAtUtc = intake.IngestedAtUtc; ApplyCurrentProjection(canonical, incoming);
        await AddImpactsAsync(canonical, revision, ct); AddAudit(occurrence, canonical.Id, "LEAD_REVISION_CREATED", intake, new { revision = next });
        await _db.SaveChangesAsync(ct); if (ownsTransaction) await tx.CommitAsync(ct);
        return new(canonical.Id, canonical.CommercialCaseReference, occurrence.Id, revision.Id, next, occurrence.Classification, occurrence.Confidence, occurrence.DecisionReasons(), false);
    }

    public async Task<BatchReconciliationDto?> GetBatchAsync(long bu, Guid batchId, CancellationToken ct = default)
    {
        if (!await _db.Set<LeadIngestionBatch>().AnyAsync(x => x.BusinessUnitId == bu && x.Id == batchId, ct)) return null;
        var intakeOccurrences = await (
            from occurrence in _db.Set<SourceDocumentOccurrence>().AsNoTracking()
            join corpus in _db.Set<DocumentCorpus>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.CorpusId }
                equals new { corpus.BusinessUnitId, CorpusId = corpus.Id }
            join document in _db.Set<SourceDocument>().AsNoTracking()
                on new { occurrence.BusinessUnitId, SourceDocumentId = occurrence.SourceDocumentId }
                equals new { document.BusinessUnitId, SourceDocumentId = document.Id }
            where occurrence.BusinessUnitId == bu && corpus.BatchId == batchId
            orderby occurrence.Id
            select new
            {
                occurrence.Id,
                occurrence.ExtractionJobId,
                occurrence.IntakeStatus,
                occurrence.OutcomeState,
                occurrence.OriginalOccurrenceId,
                occurrence.LastErrorCode,
                occurrence.LastErrorDetailsJson,
                occurrence.SourceMetadataJson,
                occurrence.ReceivedOn,
                occurrence.UpdatedOn,
                document.OriginalFileName,
                document.SecurityStatus
            }).ToListAsync(ct);
        var extractionJobIds = intakeOccurrences.Where(x => x.ExtractionJobId.HasValue)
            .Select(x => x.ExtractionJobId!.Value).Distinct().ToArray();
        var extractionJobs = await _db.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == bu && extractionJobIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, x.LastError, x.UpdatedOn })
            .ToDictionaryAsync(x => x.Id, ct);
        var rows = await _db.Set<LeadIngestionOccurrence>().AsNoTracking().Where(x => x.BusinessUnitId == bu && x.BatchId == batchId)
            .Include(x => x.Lead).ThenInclude(x => x!.AssignToNavigation)
            .Include(x => x.MatchCandidates).ThenInclude(x => x.CandidateLead)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var intakeById = intakeOccurrences.ToDictionary(x => x.Id);
        var items = rows.Select(x =>
        {
            var intake = x.SourceDocumentOccurrenceId.HasValue
                ? intakeById.GetValueOrDefault(x.SourceDocumentOccurrenceId.Value)
                : null;
            var job = intake?.ExtractionJobId.HasValue == true
                ? extractionJobs.GetValueOrDefault(intake.ExtractionJobId.Value)
                : null;
            return new BatchReconciliationItemDto(x.Id, x.LeadId, x.Lead?.CommercialCaseReference,
                x.Classification.ToString(), x.Lead?.CurrentRevisionNumber, x.OriginalFileName, x.IngestedAtUtc,
                x.ProcessingPath.ToString(), x.ExternalAiUsed, x.Confidence, x.DecisionReasons(),
                x.MatchCandidates.Select(c => new LeadMatchCandidateDto(c.Id, c.CandidateLeadId,
                    c.CandidateLead.CommercialCaseReference, c.CandidateLead.Rfqno, c.Confidence,
                    c.MatchEvidenceJson, c.DifferencesJson, c.DownstreamImpactJson,
                    c.ReviewState.ToString(), c.Version)).ToArray(),
                x.Lead?.CustomerMatchStatus ?? "Awaiting customer resolution",
                x.Lead?.AssignToNavigation is null ? null
                    : $"{x.Lead.AssignToNavigation.FirstName} {x.Lead.AssignToNavigation.LastName}".Trim(),
                "Reconciled", null, x.SourceDocumentOccurrenceId)
            {
                SecurityStatus = intake?.SecurityStatus.ToString(),
                SecurityScanUpdatedAtUtc = intake?.UpdatedOn,
                LastUpdatedAtUtc = intake?.UpdatedOn ?? x.IngestedAtUtc,
                ExtractionStatus = job?.Status.ToString(),
                ExtractionUpdatedAtUtc = job is null ? null : AsUtc(job.UpdatedOn),
                RecoverableSecurityHold = intake is not null && IsRecoverableSecurityHold(
                    intake.IntakeStatus, intake.LastErrorCode, intake.SourceMetadataJson)
            };
        }).ToList();

        var reconciledIntakeIds = rows.Where(x => x.SourceDocumentOccurrenceId.HasValue)
            .Select(x => x.SourceDocumentOccurrenceId!.Value).ToHashSet();
        foreach (var intake in intakeOccurrences.Where(x => !reconciledIntakeIds.Contains(x.Id)))
        {
            var awaitingSecurityScan = IsRecoverableSecurityHold(
                intake.IntakeStatus, intake.LastErrorCode, intake.SourceMetadataJson);
            var exactDuplicate = intake.OriginalOccurrenceId.HasValue
                || intake.OutcomeState is IngestionOutcomeState.EXACT_DUPLICATE_PENDING_SECURITY
                    or IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED
                    or IngestionOutcomeState.DUPLICATE_RESCAN_REQUIRED;
            var rejected = !awaitingSecurityScan
                && intake.IntakeStatus is IntakeOccurrenceStatus.Rejected or IntakeOccurrenceStatus.DeadLetter;
            var displayedIntakeStatus = awaitingSecurityScan
                ? IntakeOccurrenceStatus.AwaitingSecurityScan
                : intake.IntakeStatus;
            items.Add(new BatchReconciliationItemDto(
                0, null, null,
                rejected
                    ? LeadOccurrenceClassification.RejectedOrUnprocessable.ToString()
                    : exactDuplicate
                        ? LeadOccurrenceClassification.ExactDuplicate.ToString()
                        : "Pending",
                null, intake.OriginalFileName, intake.ReceivedOn,
                $"Intake{intake.IntakeStatus}", false, rejected ? 1m : 0m,
                IntakeReasons(
                    intake.LastErrorDetailsJson,
                    intake.LastErrorCode,
                    intake.IntakeStatus,
                    intake.ExtractionJobId.HasValue
                        ? extractionJobs.GetValueOrDefault(intake.ExtractionJobId.Value)?.LastError
                        : null),
                Array.Empty<LeadMatchCandidateDto>(), "Awaiting customer resolution", null,
                displayedIntakeStatus.ToString(),
                awaitingSecurityScan ? "security_scanner_unavailable" : intake.LastErrorCode,
                intake.Id)
            {
                RecoverableSecurityHold = awaitingSecurityScan,
                SecurityStatus = intake.SecurityStatus.ToString(),
                SecurityScanUpdatedAtUtc = intake.UpdatedOn,
                LastUpdatedAtUtc = intake.UpdatedOn,
                ExtractionStatus = intake.ExtractionJobId.HasValue
                    ? extractionJobs.GetValueOrDefault(intake.ExtractionJobId.Value)?.Status.ToString()
                    : null,
                ExtractionUpdatedAtUtc = intake.ExtractionJobId.HasValue
                    && extractionJobs.GetValueOrDefault(intake.ExtractionJobId.Value) is { } job
                        ? AsUtc(job.UpdatedOn)
                        : null
            });
        }

        var intakeRejected = intakeOccurrences.Count(x =>
            !reconciledIntakeIds.Contains(x.Id)
            && !IsRecoverableSecurityHold(x.IntakeStatus, x.LastErrorCode, x.SourceMetadataJson)
            && x.IntakeStatus is IntakeOccurrenceStatus.Rejected or IntakeOccurrenceStatus.DeadLetter);
        var preReconciliationExactDuplicates = intakeOccurrences.Count(x =>
            !reconciledIntakeIds.Contains(x.Id)
            && (x.OriginalOccurrenceId.HasValue
                || x.OutcomeState is IngestionOutcomeState.EXACT_DUPLICATE_PENDING_SECURITY
                    or IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED
                    or IngestionOutcomeState.DUPLICATE_RESCAN_REQUIRED));
        return new(batchId, intakeOccurrences.Count, rows.Count,
            Count(LeadOccurrenceClassification.New), Count(LeadOccurrenceClassification.ExactDuplicate) + preReconciliationExactDuplicates, Count(LeadOccurrenceClassification.Revision),
            Count(LeadOccurrenceClassification.PossibleMatchReviewRequired), Count(LeadOccurrenceClassification.RejectedOrUnprocessable) + intakeRejected,
            rows.Count(x => x.ExternalAiUsed), rows.Sum(x => x.ExternalCost), items.OrderBy(x => x.IngestedAtUtc).ToArray())
        {
            AwaitingSecurityScan = intakeOccurrences.Count(x =>
                !reconciledIntakeIds.Contains(x.Id)
                && IsRecoverableSecurityHold(x.IntakeStatus, x.LastErrorCode, x.SourceMetadataJson)),
            LocalFirstOccurrences = rows.Count(x => !x.ExternalAiUsed)
        };
        int Count(LeadOccurrenceClassification c) => rows.Count(x => x.Classification == c);
        static DateTimeOffset AsUtc(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    // Single source of truth, shared with SecurityScanRecoveryService: what this read model counts as
    // "awaiting security scan" must be exactly what the retry endpoint is willing to replay.
    private static bool IsRecoverableSecurityHold(
        IntakeOccurrenceStatus intakeStatus,
        string? lastErrorCode,
        string sourceMetadataJson) =>
        ERP_RFQ_Automation.Extraction.SecurityHoldRecovery.IsRecoverableSecurityHold(
            intakeStatus, lastErrorCode, sourceMetadataJson);

    private static IReadOnlyList<string> IntakeReasons(
        string? detailsJson,
        string? errorCode,
        IntakeOccurrenceStatus status,
        string? extractionError = null)
    {
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            try
            {
                using var details = JsonDocument.Parse(detailsJson);
                if (details.RootElement.TryGetProperty("reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(reason.GetString()))
                    return new[] { reason.GetString()! };
            }
            catch (JsonException)
            {
                // The machine-readable error code remains authoritative if legacy details are malformed.
            }
        }

        if (!string.IsNullOrWhiteSpace(extractionError))
            return new[] { extractionError };

        return new[] { !string.IsNullOrWhiteSpace(errorCode)
            ? $"Intake stopped: {errorCode.Replace('_', ' ')}."
            : $"Intake status: {status}." };
    }

    public async Task<IReadOnlyList<DuplicateUploadDto>> GetDuplicateUploadsAsync(
        long businessUnitId, CancellationToken ct = default)
    {
        var duplicateStates = new[]
        {
            IngestionOutcomeState.EXACT_DUPLICATE_PENDING_SECURITY,
            IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED,
            IngestionOutcomeState.BUSINESS_DUPLICATE_CONFIRMED,
            IngestionOutcomeState.DUPLICATE_RESCAN_REQUIRED
        };
        var occurrences = await _db.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Include(x => x.SourceDocument)
            .Include(x => x.Corpus)
            .Where(x => x.BusinessUnitId == businessUnitId
                        && (x.OriginalOccurrenceId.HasValue || duplicateStates.Contains(x.OutcomeState)))
            .OrderByDescending(x => x.ReceivedOn)
            .Take(500)
            .ToListAsync(ct);
        if (occurrences.Count == 0)
            return Array.Empty<DuplicateUploadDto>();

        var occurrenceIds = occurrences
            .SelectMany(x => new long?[] { x.Id, x.OriginalOccurrenceId })
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var identities = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && x.SourceDocumentOccurrenceId.HasValue
                        && occurrenceIds.Contains(x.SourceDocumentOccurrenceId.Value)
                        && x.LeadId.HasValue)
            .Select(x => new
            {
                SourceOccurrenceId = x.SourceDocumentOccurrenceId!.Value,
                LeadId = x.LeadId!.Value,
                NexoraSerial = x.Lead!.CommercialCaseReference,
                x.CreatedAtUtc
            })
            .ToListAsync(ct);
        var identityByOccurrence = identities
            .GroupBy(x => x.SourceOccurrenceId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.CreatedAtUtc).First());
        var batchIds = occurrences.Select(x => x.Corpus.BatchId).Distinct().ToArray();
        var batches = await _db.Set<LeadIngestionBatch>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && batchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        return occurrences.Select(x =>
        {
            var identity = identityByOccurrence.GetValueOrDefault(x.Id)
                           ?? (x.OriginalOccurrenceId.HasValue
                               ? identityByOccurrence.GetValueOrDefault(x.OriginalOccurrenceId.Value)
                               : null);
            var actions = new List<string> { "Open batch" };
            if (x.IntakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan
                || x.OutcomeState is IngestionOutcomeState.DUPLICATE_RESCAN_REQUIRED
                    or IngestionOutcomeState.SECURITY_SCAN_BLOCKED)
                actions.Add("Retry security scan");
            if (identity is not null)
                actions.Add("Open canonical lead");
            var batch = batches.GetValueOrDefault(x.Corpus.BatchId);
            return new DuplicateUploadDto(
                x.Id,
                x.SourceDocument.OriginalFileName,
                x.Corpus.BatchId,
                x.ReceivedOn,
                ResolveUploadedBy(x.SourceMetadataJson, batch?.CreatedBy),
                batch?.SourceChannel ?? x.Corpus.SourceType.ToString(),
                x.OutcomeState.ToString(),
                x.OriginalOccurrenceId,
                identity?.LeadId,
                identity?.NexoraSerial,
                x.SourceDocument.SecurityStatus.ToString(),
                x.ProcessingReused,
                new DuplicateResourceAccountingDto(
                    x.BytesUploaded, x.HashingDurationMs, x.StoragePhysicalBytes,
                    x.StorageLogicalBytes, x.MalwareScanReused, x.MalwareScanRerun,
                    x.ParserReused, x.OcrReused, x.LocalModelReused, x.ExternalModelReused,
                    x.LocalComputeCost, x.ExternalProcessingCost, x.TotalActualCost,
                    x.EstimatedProcessingAvoided, x.CostStatus),
                actions);
        }).ToArray();
    }

    private static string ResolveUploadedBy(string sourceMetadataJson, string? fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceMetadataJson);
            if (document.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in new[] { "UploadedBy", "FromEmail", "ClientEmail" })
                {
                    if (metadata.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                        return value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            // Older occurrence metadata remains readable through the batch fallback.
        }

        return string.IsNullOrWhiteSpace(fallback) ? "system" : fallback;
    }

    public async Task<IReadOnlyList<PossibleMatchQueueItemDto>> GetPossibleMatchesAsync(
        long bu, CancellationToken ct = default)
    {
        var rows = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == bu
                && x.Classification == LeadOccurrenceClassification.PossibleMatchReviewRequired)
            .Where(x => x.MatchCandidates.Any(candidate => candidate.ReviewState == LeadMatchReviewState.Pending))
            .Include(x => x.MatchCandidates).ThenInclude(x => x.CandidateLead)
            .OrderByDescending(x => x.IngestedAtUtc).Take(100).ToListAsync(ct);
        return rows.Select(x => new PossibleMatchQueueItemDto(x.BatchId, x.Id, x.OriginalFileName,
            x.IngestedAtUtc, x.Confidence, x.MatchCandidates
                .Where(candidate => candidate.ReviewState == LeadMatchReviewState.Pending)
                .Select(candidate => new LeadMatchCandidateDto(candidate.Id, candidate.CandidateLeadId,
                    candidate.CandidateLead.CommercialCaseReference, candidate.CandidateLead.Rfqno,
                    candidate.Confidence, candidate.MatchEvidenceJson, candidate.DifferencesJson,
                    candidate.DownstreamImpactJson, candidate.ReviewState.ToString(), candidate.Version))
                .ToArray())).ToArray();
    }

    public async Task<IReadOnlyList<LeadRevisionDto>> GetRevisionsAsync(long bu, long leadId, CancellationToken ct = default)
    {
        var revisions = await _db.Set<LeadRevision>().AsNoTracking().Where(x => x.BusinessUnitId == bu && x.LeadId == leadId)
            .Include(x => x.Differences).OrderByDescending(x => x.RevisionNumber).ToListAsync(ct);
        var ids = revisions.Select(x => x.Id).ToArray();
        var impacts = await _db.Set<LeadRevisionImpact>().AsNoTracking().Where(x => x.BusinessUnitId == bu && ids.Contains(x.LeadRevisionId)).ToListAsync(ct);
        return revisions.Select(x =>
        {
            var lineDifferences = x.Differences.Where(d => d.Scope == "Line").ToArray();
            return new LeadRevisionDto(x.Id, x.RevisionNumber, x.CreatedAtUtc, x.LogicalInquiryFingerprint,
                x.CustomerRfqReference, x.ProcessingPath.ToString(), x.ExternalAiUsed,
                x.Differences.Select(d => new LeadRevisionDifferenceDto(d.ChangeType.ToString(), d.Scope, d.Path, d.PreviousValueJson, d.CurrentValueJson)).ToArray(),
                impacts.Where(i => i.LeadRevisionId == x.Id).Select(i => new LeadRevisionImpactDto(i.AggregateType, i.AggregateId, i.ImpactType, i.Status, i.DetailsJson)).ToArray(),
                lineDifferences.Count(d => d.ChangeType != LeadRevisionChangeType.Unchanged),
                lineDifferences.Count(d => d.ChangeType == LeadRevisionChangeType.Modified));
        }).ToArray();
    }

    public async Task<LeadIdentityAnalyticsDto> GetAnalyticsAsync(long bu, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (to <= from) throw new ArgumentException("The analytics window must have a positive duration.");
        var windowStart = from;
        var asOf = DateTimeOffset.UtcNow;
        // Identity baselines are excluded at the SOURCE, so every branch below inherits it:
        // ingestion-volume, leads-received and the rate denominators all read from this query. A
        // baseline records content, not arrival, and counting it as a received document would
        // inflate every published ingestion number.
        var source = _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == bu && x.RecordKind == LeadOccurrenceRecordKind.Ingestion);
        var hasDurableIntake = _db.Model.FindEntityType(typeof(ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence)) is not null;
        long[] intakeOccurrenceIds;
        List<LeadIngestionOccurrence> rows;
        if (hasDurableIntake)
        {
            if (_db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            {
                var intake = (await _db.Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>()
                    .AsNoTracking().Where(x => x.BusinessUnitId == bu).ToListAsync(ct))
                    .Where(x => x.ReceivedOn >= windowStart && x.ReceivedOn < to && x.ReceivedOn <= asOf).ToArray();
                intakeOccurrenceIds = intake.Select(x => x.Id).Distinct().ToArray();
                rows = (await source.ToListAsync(ct)).Where(x => x.CreatedAtUtc <= asOf
                    && x.SourceDocumentOccurrenceId.HasValue
                    && intakeOccurrenceIds.Contains(x.SourceDocumentOccurrenceId.Value)).ToList();
            }
            else
            {
                var cohort = await (
                    from intake in _db.Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>().AsNoTracking()
                    join reconciliation in source.Where(x => x.CreatedAtUtc <= asOf)
                        on new { intake.BusinessUnitId, OccurrenceId = (long?)intake.Id }
                        equals new { reconciliation.BusinessUnitId, OccurrenceId = reconciliation.SourceDocumentOccurrenceId } into reconciliations
                    from reconciliation in reconciliations.DefaultIfEmpty()
                    where intake.BusinessUnitId == bu && intake.ReceivedOn >= windowStart && intake.ReceivedOn < to && intake.ReceivedOn <= asOf
                    select new { intake.Id, Reconciliation = reconciliation }).ToListAsync(ct);
                intakeOccurrenceIds = cohort.Select(x => x.Id).Distinct().ToArray();
                rows = cohort.Where(x => x.Reconciliation is not null).Select(x => x.Reconciliation!).ToList();
            }

            // Rows created before durable source occurrences were introduced retain their
            // reconciliation-time cohort until a controlled backfill links them.
            var legacyRows = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true
                ? (await source.Where(x => !x.SourceDocumentOccurrenceId.HasValue).ToListAsync(ct))
                    .Where(x => x.CreatedAtUtc >= windowStart && x.CreatedAtUtc < to && x.CreatedAtUtc <= asOf)
                    .ToList()
                : await source.Where(x => !x.SourceDocumentOccurrenceId.HasValue
                        && x.CreatedAtUtc >= windowStart && x.CreatedAtUtc < to && x.CreatedAtUtc <= asOf)
                    .ToListAsync(ct);
            rows.AddRange(legacyRows);
            intakeOccurrenceIds = intakeOccurrenceIds.Concat(legacyRows.Select(x => x.Id)).Distinct().ToArray();
        }
        else
        {
            var sourceRows = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true
                ? await source.ToListAsync(ct)
                : await source.Where(x => x.CreatedAtUtc <= asOf).ToListAsync(ct);
            rows = sourceRows
                .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc < to).ToList();
            intakeOccurrenceIds = rows.Select(x => x.Id).ToArray();
        }
        var total = intakeOccurrenceIds.Length;
        long[] CohortOccurrenceIds(IEnumerable<LeadIngestionOccurrence> selected) => hasDurableIntake
            ? selected.Select(x => x.SourceDocumentOccurrenceId ?? x.Id).Distinct().ToArray()
            : selected.Select(x => x.Id).Distinct().ToArray();
        LeadIdentityMetricDto CountMetric(string key, LeadOccurrenceClassification classification, bool canonical = false)
        {
            var selected = rows.Where(x => x.Classification == classification).ToArray();
            var leadIds = selected.Where(x => x.LeadId.HasValue).Select(x => x.LeadId!.Value).Distinct().ToArray();
            var occurrenceIds = CohortOccurrenceIds(selected);
            var numerator = canonical ? leadIds.Length : occurrenceIds.Length;
            return new(key, numerator, numerator, null, leadIds, occurrenceIds);
        }
        LeadIdentityMetricDto Rate(string key, LeadOccurrenceClassification classification)
        {
            var selected = rows.Where(x => x.Classification == classification).ToArray();
            var occurrenceIds = CohortOccurrenceIds(selected);
            return new(key, total == 0 ? 0 : decimal.Round((decimal)occurrenceIds.Length / total, 5), occurrenceIds.Length, total,
                selected.Where(x => x.LeadId.HasValue).Select(x => x.LeadId!.Value).Distinct().ToArray(), occurrenceIds);
        }
        return new("release-01c/as-of-v1", from, to, asOf,
        [
            new("ingestion-volume", total, total, null, rows.Where(x => x.LeadId.HasValue).Select(x => x.LeadId!.Value).Distinct().ToArray(), intakeOccurrenceIds),
            CountMetric("leads-received", LeadOccurrenceClassification.New, canonical: true),
            Rate("duplicate-rate", LeadOccurrenceClassification.ExactDuplicate),
            Rate("revision-rate", LeadOccurrenceClassification.Revision),
            Rate("possible-match-rate", LeadOccurrenceClassification.PossibleMatchReviewRequired)
        ]);
    }

    public Task<LeadReconciliationResult> DecideMatchAsync(long bu, long occurrenceId,
        MatchDecisionRequest request, string actorId, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null || !_db.Database.IsRelational())
            return DecideMatchCoreAsync(bu, occurrenceId, request, actorId, ct);
        return _db.Database.CreateExecutionStrategy().ExecuteAsync(
            () => DecideMatchCoreAsync(bu, occurrenceId, request, actorId, ct));
    }

    private async Task<LeadReconciliationResult> DecideMatchCoreAsync(long bu, long occurrenceId,
        MatchDecisionRequest request, string actorId, CancellationToken ct)
    {
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"match-review:{bu}:{occurrenceId}"}, 0))", ct);
        var occurrence = await _db.Set<LeadIngestionOccurrence>().Include(x => x.MatchCandidates)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == bu && x.Id == occurrenceId, ct) ?? throw new KeyNotFoundException();
        if (await _db.Set<LeadIdentityAuditEvent>().AsNoTracking().AnyAsync(x => x.BusinessUnitId == bu && x.IdempotencyKey == request.IdempotencyKey, ct))
        {
            var replayLead = occurrence.LeadId.HasValue
                ? await _db.Leads.AsNoTracking().SingleAsync(x => x.BusinessUnitId == bu && x.Id == occurrence.LeadId.Value, ct) : null;
            if (ownsTransaction) await transaction!.CommitAsync(ct);
            if (occurrence.MatchCandidates.Any(x => x.ReviewState == LeadMatchReviewState.CreatedNew))
                await RouteReviewedNewAsync(bu, replayLead?.Id, request.IdempotencyKey, ct);
            return new(replayLead?.Id ?? 0, replayLead?.CommercialCaseReference ?? "", occurrence.Id, occurrence.LeadRevisionId,
                replayLead?.CurrentRevisionNumber ?? 0, occurrence.Classification,
                occurrence.MatchCandidates.FirstOrDefault(x => x.CandidateLeadId == request.CandidateLeadId)?.Confidence ?? occurrence.Confidence,
                ["Idempotent replay of the governed match-review decision."], false);
        }
        if (occurrence.Classification != LeadOccurrenceClassification.PossibleMatchReviewRequired) throw new InvalidOperationException("Occurrence is not awaiting match review.");
        var candidate = occurrence.MatchCandidates.SingleOrDefault(x => x.CandidateLeadId == request.CandidateLeadId)
            ?? throw new InvalidOperationException("Candidate is not available in this tenant review.");
        if (candidate.Version != request.ExpectedVersion) throw new DbUpdateConcurrencyException("Match review changed. Refresh and retry.");
        if (request.Action is not ("exact_duplicate" or "revision" or "link" or "create_new" or "defer" or "reject"))
            throw new ArgumentException("Unsupported match decision.");
        candidate.ReviewState = request.Action switch { "exact_duplicate" => LeadMatchReviewState.ConfirmedDuplicate,
            "revision" or "link" => LeadMatchReviewState.ConfirmedRevision, "create_new" => LeadMatchReviewState.CreatedNew,
            "reject" => LeadMatchReviewState.Rejected, _ => LeadMatchReviewState.Deferred };
        candidate.ReviewedBy = actorId; candidate.ReviewedAtUtc = DateTimeOffset.UtcNow; candidate.ReviewReason = request.Reason; candidate.Version++;
        // Whether the buyer's verbatim values were available to apply. False only for
        // candidates raised before ProposedLeadSnapshotJson existed; recorded on the audit
        // event either way so "the amendment did not land" is never a silent outcome.
        bool? verbatimApplied = null;
        if (request.Action == "exact_duplicate") { occurrence.Classification = LeadOccurrenceClassification.ExactDuplicate; occurrence.LeadId = candidate.CandidateLeadId; occurrence.LeadRevisionId = await _db.Leads.Where(x => x.Id == candidate.CandidateLeadId).Select(x => x.CurrentRevisionId).SingleAsync(ct); }
        else if (request.Action is "revision" or "link")
        {
            var canonical = await _db.Leads.Include(x => x.LeadItems).SingleAsync(x => x.BusinessUnitId == bu && x.Id == candidate.CandidateLeadId, ct);
            var proposed = ProposedSnapshot(candidate.DifferencesJson);
            var previous = canonical.CurrentRevisionId.HasValue
                ? await _db.Set<LeadRevision>().Where(x => x.Id == canonical.CurrentRevisionId.Value).Select(x => x.SnapshotJson).SingleAsync(ct)
                : JsonSerializer.Serialize(Snapshot(canonical));
            var revision = new LeadRevision { BusinessUnitId = bu, LeadId = canonical.Id,
                RevisionNumber = canonical.CurrentRevisionNumber + 1, EstablishedByOccurrence = occurrence,
                LogicalInquiryFingerprint = occurrence.LogicalInquiryFingerprint, SnapshotJson = proposed,
                CustomerRfqReference = canonical.Rfqno, NormalizedCustomerRfqReference = Normalize(canonical.Rfqno),
                CustomerIdSnapshot = canonical.CustomerId, ContactIdSnapshot = canonical.ContactId, CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = actorId, ProcessingPath = LeadProcessingPath.HumanReview, ExternalAiUsed = occurrence.ExternalAiUsed };
            foreach (var difference in Diff(previous, proposed)) { difference.BusinessUnitId = bu; revision.Differences.Add(difference); }
            PopulateRevisionItems(revision, proposed);
            _db.Add(revision); await _db.SaveChangesAsync(ct);
            occurrence.Classification = LeadOccurrenceClassification.Revision; occurrence.LeadId = canonical.Id; occurrence.LeadRevisionId = revision.Id;
            canonical.CurrentRevisionId = revision.Id; canonical.CurrentRevisionNumber = revision.RevisionNumber;
            canonical.CurrentInquiryFingerprint = occurrence.LogicalInquiryFingerprint; canonical.CurrentOccurrenceClassification = LeadOccurrenceClassification.Revision.ToString();
            verbatimApplied = ApplyVerbatimProjection(canonical, candidate.ProposedLeadSnapshotJson);
            if (verbatimApplied == true)
            {
                // The revision indexes the reference the amendment actually carries. Reading it
                // from the pre-projection canonical made the newest revision index the SUPERSEDED
                // reference, so the unbounded revision lookup lagged one amendment behind.
                revision.CustomerRfqReference = canonical.Rfqno;
                revision.NormalizedCustomerRfqReference = Normalize(canonical.Rfqno);
            }
            await AddImpactsAsync(canonical, revision, ct);
        }
        else if (request.Action == "create_new")
        {
            var configId = await _db.EmailConfigurations.Where(x => x.BusinessUnitId == bu && x.IsActive).Select(x => x.Id).FirstOrDefaultAsync(ct);
            if (configId == 0) throw new InvalidOperationException("No active tenant ingestion configuration is available.");
            var now = DateTime.UtcNow;
            var ingest = new EmailIngest { MessageId = $"match-review:{bu}:{occurrence.Id}", EmailSubject = occurrence.Subject,
                FromEmail = occurrence.Sender ?? "unresolved-source", EmailConfigurationId = configId, CreatedOn = now,
                ParsedAt = now, ParseStatus = "NeedsReview" };
            var newLead = new Lead { Rfqno = null, BuyersName = null, RecDate = now, LeadSource = occurrence.SourceChannel,
                Clientemail = occurrence.Sender, EmailSource = occurrence.MimeType, CreatedBy = actorId, CreatedDate = now,
                BusinessUnitId = bu, EmailIngests = ingest, RequiresCommercialReview = true, CommercialFactsVerified = false };
            var proposed = ProposedSnapshot(candidate.DifferencesJson);
            verbatimApplied = ApplyVerbatimProjection(newLead, candidate.ProposedLeadSnapshotJson);
            _db.Add(newLead); await _db.SaveChangesAsync(ct);
            var revision = new LeadRevision { BusinessUnitId = bu, LeadId = newLead.Id, RevisionNumber = 1,
                EstablishedByOccurrence = occurrence, LogicalInquiryFingerprint = occurrence.LogicalInquiryFingerprint,
                SnapshotJson = proposed, CustomerRfqReference = newLead.Rfqno, NormalizedCustomerRfqReference = Normalize(newLead.Rfqno),
                CustomerIdSnapshot = newLead.CustomerId, ContactIdSnapshot = newLead.ContactId, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = actorId,
                ProcessingPath = LeadProcessingPath.HumanReview, ExternalAiUsed = occurrence.ExternalAiUsed };
            PopulateRevisionItems(revision, proposed);
            _db.Add(revision); await _db.SaveChangesAsync(ct);
            newLead.CurrentRevisionId = revision.Id; newLead.CurrentRevisionNumber = 1; newLead.CurrentInquiryFingerprint = occurrence.LogicalInquiryFingerprint;
            newLead.CurrentOccurrenceClassification = LeadOccurrenceClassification.New.ToString(); newLead.IngestedAtUtc = occurrence.IngestedAtUtc;
            occurrence.Classification = LeadOccurrenceClassification.New; occurrence.LeadId = newLead.Id; occurrence.LeadRevisionId = revision.Id;
        }
        else if (request.Action == "reject") occurrence.Classification = LeadOccurrenceClassification.RejectedOrUnprocessable;
        occurrence.Version++;
        _db.Add(new LeadIdentityAuditEvent { BusinessUnitId = bu, LeadId = occurrence.LeadId, OccurrenceId = occurrence.Id,
            EventType = "POSSIBLE_MATCH_DECIDED", PayloadJson = JsonSerializer.Serialize(new { request.Action, request.Reason, request.CandidateLeadId, verbatimProjectionApplied = verbatimApplied }),
            ActorType = "User", ActorId = actorId, CorrelationId = $"review:{occurrence.Id}", IdempotencyKey = request.IdempotencyKey, OccurredAtUtc = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync(ct);
        var lead = occurrence.LeadId.HasValue ? await _db.Leads.AsNoTracking().SingleAsync(x => x.Id == occurrence.LeadId, ct) : null;
        if (ownsTransaction) await transaction!.CommitAsync(ct);
        if (request.Action == "create_new") await RouteReviewedNewAsync(bu, lead?.Id, request.IdempotencyKey, ct);
        string[] decisionReasons = verbatimApplied == false
            ? ["Governed human match-review decision.",
               "Verbatim source values were not retained for this match, so no commercial values were applied; re-ingest the document to apply its content."]
            : ["Governed human match-review decision."];
        return new(lead?.Id ?? 0, lead?.CommercialCaseReference ?? "", occurrence.Id, occurrence.LeadRevisionId, lead?.CurrentRevisionNumber ?? 0,
            occurrence.Classification, candidate.Confidence, decisionReasons, request.Action == "create_new");
    }

    private async Task RouteReviewedNewAsync(long bu, long? leadId, string idempotencyKey, CancellationToken ct)
    {
        if (_routing is null || !leadId.HasValue) return;
        await _routing.RouteLeadAsync(bu, new RouteLeadCommand(leadId.Value, $"match-review:{Hash(idempotencyKey)}",
            $"match-review:{leadId.Value}"), ct);
    }

    // ================================================================ Identity baseline

    /// <summary>
    /// Establishes canonical identity (revision 1) for a Lead that has none, from the lead's OWN
    /// stored lines and header.
    ///
    /// <para><b>Why this exists.</b> Three ingestion doors — ManualUploadService, EmailService and
    /// LeadUploaderService — create a Lead with <c>_context.Leads.Add(lead)</c> and never call
    /// <see cref="ReconcileAsync"/>. Those leads have line items but no revision, and every read
    /// path that needs the immutable revision refuses them: commercial line resolution throws
    /// "The lead has no immutable current revision", which fails RFQ conversion outright. A lead
    /// uploaded today is born unconvertible. This is the writer those doors call.</para>
    ///
    /// <para><b>What it deliberately does NOT do.</b> It never calls
    /// <c>ApplyCurrentProjection</c> (which deletes and recreates LeadItems, churning ids that
    /// other tables reference), never adds impacts or diffs, and never adds a candidate Lead. It
    /// writes one occurrence, one revision, one audit row, and points the lead at it.</para>
    ///
    /// <para><b>Honesty.</b> The occurrence it must mint (a revision cannot exist without one —
    /// EstablishedByOccurrenceId is NOT NULL) records CONTENT, not arrival. Every document field
    /// is null, <c>SourceReceivedAtUtc</c> is null, and it is marked
    /// <see cref="LeadOccurrenceRecordKind.IdentityBaseline"/> so the analytics readers exclude
    /// it from ingestion volume, leads-received, the touchless KPI and the extraction corpus.
    /// The fingerprint is the real SHA-256 from <see cref="Fingerprint(Lead)"/> over real lines —
    /// not a synthesised value.</para>
    /// </summary>
    public Task<LeadReconciliationResult> EstablishBaselineRevisionAsync(
        long businessUnitId, long leadId, LeadIdentityBaselineRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_db.Database.CurrentTransaction is not null || !_db.Database.IsRelational())
            return EstablishBaselineCoreAsync(businessUnitId, leadId, request, ct);
        return _db.Database.CreateExecutionStrategy().ExecuteAsync(
            () => EstablishBaselineCoreAsync(businessUnitId, leadId, request, ct));
    }

    private async Task<LeadReconciliationResult> EstablishBaselineCoreAsync(
        long businessUnitId, long leadId, LeadIdentityBaselineRequest request, CancellationToken ct)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        // An owned transaction when none is ambient — pg_advisory_xact_lock is transaction-scoped,
        // so without this the lock would be taken and released by a single autocommit statement
        // and protect nothing.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({$"lead-identity-baseline:{businessUnitId}:{leadId}"}, 0))", ct);

        var lead = await _db.Leads.Include(x => x.LeadItems)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
            ?? throw new KeyNotFoundException($"Lead {leadId} was not found in business unit {businessUnitId}.");

        // Already has identity — no writes at all. This is what makes the call safe to make
        // unconditionally from every door and from the backlog sweeper.
        if (lead.CurrentRevisionId.HasValue)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct);
            return new LeadReconciliationResult(lead.Id, lead.CommercialCaseReference ?? string.Empty, 0,
                lead.CurrentRevisionId, lead.CurrentRevisionNumber, LeadOccurrenceClassification.New, 1m,
                new[] { "Lead already carries a canonical revision." }, ShouldRoute: false);
        }

        // Pointer repair: a revision exists but the Lead does not point at it. Repair the pointer
        // rather than minting a second revision, and audit against the EXISTING occurrence —
        // LeadIdentityAuditEvent.OccurrenceId is non-nullable, so a synthetic 0 would be silently
        // written into an append-only ledger.
        var orphan = await _db.Set<LeadRevision>()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId)
            .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        if (orphan is not null)
        {
            lead.CurrentRevisionId = orphan.Id;
            lead.CurrentRevisionNumber = orphan.RevisionNumber;
            lead.CurrentInquiryFingerprint = orphan.LogicalInquiryFingerprint;
            lead.IdentityVersion += 1;
            _db.Add(new LeadIdentityAuditEvent
            {
                BusinessUnitId = businessUnitId, LeadId = lead.Id,
                OccurrenceId = orphan.EstablishedByOccurrenceId, EventType = "CANONICAL_POINTER_REPAIRED",
                PayloadJson = JsonSerializer.Serialize(new { revision = orphan.RevisionNumber, reason = request.Reason }),
                ActorType = request.ActorType, ActorId = request.ActorId, CorrelationId = request.CorrelationId,
                IdempotencyKey = $"identity-pointer-repair:{businessUnitId}:{leadId}",
                OccurredAtUtc = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct);
            return new LeadReconciliationResult(lead.Id, lead.CommercialCaseReference ?? string.Empty,
                orphan.EstablishedByOccurrenceId, orphan.Id, orphan.RevisionNumber,
                LeadOccurrenceClassification.New, 1m, new[] { "Canonical revision pointer repaired." }, ShouldRoute: false);
        }

        var now = DateTimeOffset.UtcNow;
        var fingerprint = Fingerprint(lead);
        var idempotencyKey = $"identity-baseline:{businessUnitId}:{leadId}";

        // No document fields: the descriptor is built with nulls throughout so nothing can assert
        // a receipt. The batch is keyed per (tenant, channel) — EnsureBatchAsync is
        // ON CONFLICT DO NOTHING, so one shared batch would take whichever channel wrote first
        // and mislabel every later one on the batch-review screen.
        var intake = new LeadIntakeDescriptor(
            BatchId: BaselineBatchId(businessUnitId, request.SourceChannel),
            SourceChannel: request.SourceChannel, IdempotencyKey: idempotencyKey,
            ExternalSourceId: null, EmailThreadId: null, SourceSystem: "IdentityBaseline",
            Sender: null, Subject: null, OriginalFileName: null, MimeType: null, FileSize: null,
            ContentHash: null, SourceDocumentId: null, ExtractionJobId: null,
            SourceReceivedAtUtc: null, IngestedAtUtc: now,
            ProcessingPath: LeadProcessingPath.Deterministic, ExternalAiUsed: false, ExternalCost: null,
            ActorType: request.ActorType, ActorId: request.ActorId, CorrelationId: request.CorrelationId);

        await EnsureBatchAsync(businessUnitId, intake, ct);

        // Reuse an occurrence from a crashed earlier attempt rather than colliding on the unique
        // (BusinessUnitId, IdempotencyKey) index.
        var occurrence = await _db.Set<LeadIngestionOccurrence>()
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                                      && x.IdempotencyKey == idempotencyKey
                                      && x.LeadRevisionId == null, ct);
        if (occurrence is null)
        {
            occurrence = NewOccurrence(businessUnitId, intake, fingerprint, scope: null,
                LeadOccurrenceClassification.New, 1m,
                new[]
                {
                    $"Canonical identity established from the lead's stored commercial facts at {now:O}. " +
                    "No inquiry document was received, no receipt time is known, and no matching was attempted. " +
                    "This records content, not arrival.",
                    request.Reason
                },
                leadId: lead.Id, revisionId: null);
            occurrence.PolicyVersion = BaselinePolicyVersion;
            occurrence.RecordKind = LeadOccurrenceRecordKind.IdentityBaseline;
            _db.Add(occurrence);
            await _db.SaveChangesAsync(ct);
        }

        // SnapshotJson is the literal Snapshot(lead) with no wrapper envelope: Fingerprint hashes
        // that exact string, and Diff/IndexedLines read rfq/buyer/closing/items positionally at
        // the top level. Wrapping it would break the first real amendment's diff.
        var revision = BuildRevision(lead, occurrence, 1, fingerprint, intake);
        _db.Add(revision);
        await _db.SaveChangesAsync(ct);

        lead.CurrentRevisionId = revision.Id;
        lead.CurrentRevisionNumber = 1;
        lead.CurrentInquiryFingerprint = fingerprint;
        lead.CurrentOccurrenceClassification = nameof(LeadOccurrenceRecordKind.IdentityBaseline);
        lead.IdentityVersion += 1;
        occurrence.LeadRevisionId = revision.Id;

        AddAudit(occurrence, lead.Id, "CANONICAL_BASELINE_ESTABLISHED", intake, new
        {
            revision = 1,
            lineCount = lead.LeadItems.Count,
            leadCreatedDate = lead.CreatedDate,
            door = request.SourceChannel,
            reason = request.Reason
        });

        await _db.SaveChangesAsync(ct);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct);

        return new LeadReconciliationResult(lead.Id, lead.CommercialCaseReference ?? string.Empty,
            occurrence.Id, revision.Id, 1, LeadOccurrenceClassification.New, 1m,
            new[] { "Canonical identity baseline established." }, ShouldRoute: false);
    }

    /// <summary>Stable batch id per (tenant, channel). A random Guid would create a new batch on
    /// every call and litter the batch-review screen.</summary>
    private static Guid BaselineBatchId(long businessUnitId, string sourceChannel)
    {
        var seed = $"identity-baseline-batch:{businessUnitId}:{sourceChannel}";
        return new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));
    }

    private async Task EnsureBatchAsync(long bu, LeadIntakeDescriptor intake, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LeadIngestionBatches" ("Id","BusinessUnitId","SourceChannel","CreatedBy","CreatedAtUtc","UpdatedAtUtc","Version")
                VALUES ({intake.BatchId},{bu},{intake.SourceChannel},{intake.ActorId},{intake.IngestedAtUtc},{intake.IngestedAtUtc},1)
                ON CONFLICT ("Id") DO NOTHING
                """, ct);
            return;
        }
        if (!await _db.Set<LeadIngestionBatch>().AnyAsync(x => x.BusinessUnitId == bu && x.Id == intake.BatchId, ct))
            _db.Add(new LeadIngestionBatch { Id = intake.BatchId, BusinessUnitId = bu, SourceChannel = intake.SourceChannel,
                CreatedBy = intake.ActorId, CreatedAtUtc = intake.IngestedAtUtc, UpdatedAtUtc = intake.IngestedAtUtc });
    }

    private static LeadIngestionOccurrence NewOccurrence(long bu, LeadIntakeDescriptor x, string fingerprint, string? scope,
        LeadOccurrenceClassification classification, decimal confidence, string[] reasons, long? leadId, long? revisionId)
    {
        var occurrence = new LeadIngestionOccurrence
        {
            BusinessUnitId = bu, BatchId = x.BatchId, LeadId = leadId, LeadRevisionId = revisionId, SourceDocumentId = x.SourceDocumentId,
            SourceDocumentOccurrenceId = x.SourceDocumentOccurrenceId,
            ExtractionJobId = x.ExtractionJobId, SourceChannel = x.SourceChannel, IdempotencyKey = x.IdempotencyKey, ExternalSourceId = x.ExternalSourceId,
            EmailThreadId = x.EmailThreadId, LogicalGroupKey = x.LogicalGroupKey, SourceSystem = x.SourceSystem, Sender = x.Sender, Subject = x.Subject, OriginalFileName = x.OriginalFileName,
            MimeType = x.MimeType, FileSize = x.FileSize, ContentHash = x.ContentHash, CustomerScopeKey = scope, LogicalInquiryFingerprint = fingerprint,
            Classification = classification, Confidence = confidence, DecisionReasonsJson = JsonSerializer.Serialize(reasons), PolicyVersion = PolicyVersion,
            ProcessingPath = x.ProcessingPath, ExternalAiUsed = x.ExternalAiUsed, ExternalCost = x.ExternalCost, SourceReceivedAtUtc = x.SourceReceivedAtUtc,
            IngestedAtUtc = x.IngestedAtUtc, CreatedAtUtc = DateTimeOffset.UtcNow, ActorType = x.ActorType, ActorId = x.ActorId, CorrelationId = x.CorrelationId
        };
        if (x.SourceDocumentId.HasValue)
            occurrence.Documents.Add(new LeadOccurrenceDocument { BusinessUnitId = bu, SourceDocumentId = x.SourceDocumentId.Value,
                Role = "Primary", Ordinal = 1, LinkedAtUtc = DateTimeOffset.UtcNow });
        return occurrence;
    }

    private static LeadRevision BuildRevision(Lead lead, LeadIngestionOccurrence occurrence, int number, string fingerprint, LeadIntakeDescriptor intake)
    {
        var revision = new LeadRevision { BusinessUnitId = lead.BusinessUnitId, LeadId = lead.Id, RevisionNumber = number,
            EstablishedByOccurrence = occurrence, LogicalInquiryFingerprint = fingerprint, SnapshotJson = JsonSerializer.Serialize(Snapshot(lead)),
            CustomerRfqReference = lead.Rfqno, NormalizedCustomerRfqReference = Normalize(lead.Rfqno), CustomerIdSnapshot = lead.CustomerId,
            ContactIdSnapshot = lead.ContactId, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = intake.ActorId,
            ProcessingPath = intake.ProcessingPath, ExternalAiUsed = intake.ExternalAiUsed };
        var line = 0; foreach (var item in lead.LeadItems) revision.Items.Add(new LeadItemRevision { BusinessUnitId = lead.BusinessUnitId,
            LineNumber = ++line, LineFingerprint = LineFingerprint(item), SnapshotJson = JsonSerializer.Serialize(ItemSnapshot(item)) });
        return revision;
    }

    private async Task AddImpactsAsync(Lead lead, LeadRevision revision, CancellationToken ct)
    {
        var rfqs = await _db.Rfqs.Where(x => x.BusinessUnitId == lead.BusinessUnitId && x.LeadId == lead.Id).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in rfqs) _db.Add(Impact(lead, revision, "RFQ", id, "RFQ_REVISION_REQUIRED"));
        var quotes = await _db.Quotes.Where(x => x.BusinessUnitId == lead.BusinessUnitId && x.Rfqid != null && rfqs.Contains(x.Rfqid.Value))
            .Select(x => new { x.Id, x.StatusId, StatusCode = x.Status == null ? null : x.Status.SetupCode }).ToListAsync(ct);
        foreach (var q in quotes) _db.Add(Impact(lead, revision, "QUOTE", q.Id,
            string.Equals(q.StatusCode, "SENT", StringComparison.OrdinalIgnoreCase) || (q.StatusCode == null && q.StatusId == 43)
                ? "QUOTE_REVISION_REQUIRED" : "DRAFT_STALE_REVIEW_REQUIRED"));
        var orders = await _db.Orders.Where(x => x.BusinessUnitId == lead.BusinessUnitId && x.LeadId == lead.Id).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in orders) _db.Add(Impact(lead, revision, "ORDER", id, "CHANGE_REQUEST_REQUIRED"));
    }
    private static LeadRevisionImpact Impact(Lead l, LeadRevision r, string type, long id, string impact) => new()
    { BusinessUnitId = l.BusinessUnitId, LeadId = l.Id, LeadRevisionId = r.Id, AggregateType = type, AggregateId = id, ImpactType = impact,
      DetailsJson = JsonSerializer.Serialize(new { fromRevision = r.RevisionNumber - 1, toRevision = r.RevisionNumber, automaticMutation = false }), CreatedAtUtc = DateTimeOffset.UtcNow };

    private async Task<string> DownstreamImpactJsonAsync(Lead lead, CancellationToken ct) => JsonSerializer.Serialize(new
    { rfqCount = await _db.Rfqs.CountAsync(x => x.BusinessUnitId == lead.BusinessUnitId && x.LeadId == lead.Id, ct), orderCount = await _db.Orders.CountAsync(x => x.BusinessUnitId == lead.BusinessUnitId && x.LeadId == lead.Id, ct) });
    private void AddAudit(LeadIngestionOccurrence occurrence, long? leadId, string type, LeadIntakeDescriptor intake, object payload) =>
        _db.Add(new LeadIdentityAuditEvent { BusinessUnitId = occurrence.BusinessUnitId, LeadId = leadId, OccurrenceId = occurrence.Id, EventType = type,
            PayloadJson = JsonSerializer.Serialize(payload), ActorType = intake.ActorType, ActorId = intake.ActorId, CorrelationId = intake.CorrelationId,
            IdempotencyKey = $"{intake.IdempotencyKey}:{type}", OccurredAtUtc = DateTimeOffset.UtcNow });

    private void ApplyCurrentProjection(Lead target, Lead source)
    {
        target.Rfqno = source.Rfqno; target.BuyersName = source.BuyersName; target.RecDate = source.RecDate; target.BidClosingDate = source.BidClosingDate;
        target.HeaderRemarks = source.HeaderRemarks; target.NoOfLineItems = source.NoOfLineItems; target.Rfqtype = source.Rfqtype; target.ModifiedDate = DateTime.UtcNow;
        _db.RemoveRange(target.LeadItems);
        target.LeadItems.Clear();
        foreach (var item in source.LeadItems) target.LeadItems.Add(CloneCurrentItem(item));
    }
    public static string Fingerprint(Lead lead) => Hash(JsonSerializer.Serialize(Snapshot(lead)));
    private sealed record ItemFingerprintSnapshot(string? line, string? part, string? description, int Quantity, string? uom, string? date);
    private static object Snapshot(Lead x) => new { rfq = Normalize(x.Rfqno), buyer = Normalize(x.BuyersName), closing = x.BidClosingDate?.ToUniversalTime().ToString("O"),
        items = x.LeadItems.Select(ItemSnapshot).OrderBy(i => i.part).ThenBy(i => i.line).ToArray() };
    private static ItemFingerprintSnapshot ItemSnapshot(LeadItem x) => new(Normalize(x.LineItemNo), Normalize(x.ManufacturerPartNumber ?? x.ItemMaterialCode),
        Normalize(x.ProductShortDescription ?? x.ItemText), x.Quantity, NormalizeUom(x.UnitOfMeasure), x.BidClosingDateLine?.ToUniversalTime().ToString("O"));
    private static string LineFingerprint(LeadItem x) => Hash(JsonSerializer.Serialize(ItemSnapshot(x)));
    private static string LineIdentityFingerprint(LeadItem x) => Hash(JsonSerializer.Serialize(new
    {
        part = Normalize(x.ManufacturerPartNumber ?? x.ItemMaterialCode),
        description = Normalize(x.ProductShortDescription ?? x.ItemText),
        uom = NormalizeUom(x.UnitOfMeasure)
    }));
    /// <summary>
    /// UoM for identity purposes. <see cref="Normalize"/> alone only lowercases and drops
    /// punctuation, so "each", "EA", "pcs", "piece" and "NOS" — five spellings of ONE unit,
    /// all present in production — produced five different hashes and the same RFQ arriving
    /// twice failed to dedup. The canonicaliser collapses the spellings and deliberately does
    /// NOT collapse packaging ("Pack" stays distinct from "EA"), so two genuinely different
    /// lines still hash differently.
    /// </summary>
    private static string? NormalizeUom(string? value) => Normalize(UomCanonicalizer.EquivalenceKey(value));
    private static string? CustomerScope(Lead x, string? sender)
    {
        if (x.CustomerId.HasValue) return $"customer:{x.CustomerId}";
        if (Normalize(x.Clientemail ?? sender) is { } email) return $"email:{email}";
        return Normalize(x.BuyersName) is { } buyer ? $"buyer:{buyer}" : null;
    }
    private static string? Normalize(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var v = NonWord.Replace(value.Trim().ToLowerInvariant(), ""); return v.Length == 0 ? null : v; }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static decimal Similarity(Lead a, Lead b)
    {
        var left = a.LeadItems.Select(LineIdentityFingerprint).ToHashSet();
        var right = b.LeadItems.Select(LineIdentityFingerprint).ToHashSet();
        if (left.Count == 0 || right.Count == 0) return Normalize(a.Rfqno) == Normalize(b.Rfqno) && Normalize(a.Rfqno) != null ? .8m : 0m;
        var overlap = left.Intersect(right).Count();
        var jaccard = (decimal)overlap / left.Union(right).Count();
        var containment = (decimal)overlap / Math.Min(left.Count, right.Count);
        return Math.Max(jaccard, containment * .75m);
    }
    /// <summary>Similarity at or above which an existing lead is offered as a match candidate at all.</summary>
    private const decimal PossibleMatchThreshold = .65m;

    /// <summary>
    /// Similarity at or above which corroborating identity evidence may create a revision with
    /// NO human in the loop.
    ///
    /// <para>Set at .90 against what <see cref="Similarity"/> can actually produce. Line identity
    /// hashes part + description + UoM and deliberately excludes quantity, so an amendment that
    /// only re-prices or re-quantifies existing lines scores 1.00 and links automatically — the
    /// FR-RFQ-05 case. Adding one line to a five-line RFQ scores .83 and one added line to a
    /// two-line RFQ scores .67; both stay below this bar and go to a human. Containment alone is
    /// capped at .75 by <see cref="Similarity"/>, so a subset can never auto-link.</para>
    /// </summary>
    private const decimal ConfidentRevisionThreshold = .90m;

    /// <summary>
    /// What an identity signal says about two documents. The distinction that matters is
    /// <see cref="Absent"/> versus <see cref="Contradicting"/>: a signal nobody could resolve is
    /// no evidence and must never be read as agreement, while a signal that resolved on both
    /// sides and DISAGREES is positive evidence of two different inquiries.
    /// </summary>
    private enum MatchEvidence { Contradicting, Absent, Corroborating }

    private sealed record MatchAssessment(Lead Lead, decimal Score, MatchEvidence Scope, MatchEvidence Reference,
        bool ReferenceAmends, bool Grouped, string? BrdDuplicateReason = null)
    {
        /// <summary>
        /// The identity evidence positively says "different inquiry". The customer's own RFQ
        /// reference is decisive: when both documents carry one and they differ, the buyer has
        /// told us these are two inquiries, however identical the line items look — that is why a
        /// repeat order for the same commodity does not become an amendment. A differing sender
        /// is decisive too, unless the shared reference contradicts it, which is exactly the
        /// "two contacts at one company, one RFQ" case a human should see.
        /// </summary>
        public bool Contradicted =>
            Reference == MatchEvidence.Contradicting
            || (Scope == MatchEvidence.Contradicting && Reference != MatchEvidence.Corroborating);

        public int EvidenceRank => Contradicted ? 0
            : Scope == MatchEvidence.Corroborating || Reference == MatchEvidence.Corroborating ? 3
            : Grouped ? 2
            : 1;
    }

    private static MatchEvidence Evidence(string? left, string? right) =>
        left is null || right is null ? MatchEvidence.Absent
        : string.Equals(left, right, StringComparison.Ordinal) ? MatchEvidence.Corroborating
        : MatchEvidence.Contradicting;

    private static string? CustomerReference(Lead x) => Normalize(x.Rfqno
        ?? x.LeadItems.Select(i => i.CustomerRfqno).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>
    /// Customer RFQ reference evidence, tolerant of the one way a reference legitimately changes
    /// on an amendment: a revision marker appended to the original ("RFQ-4471" -> "RFQ-4471 Rev B",
    /// normalising to <c>rfq4471</c> -> <c>rfq4471revb</c>).
    ///
    /// <para>The longer reference must EXTEND the shorter one and the extension must begin with a
    /// letter. That keeps a genuine revision suffix corroborating while a different sequence
    /// number — <c>RFQ-1</c> against <c>RFQ-10</c>, or <c>SEPARATE-1</c> against
    /// <c>SEPARATE-10</c> — stays a contradiction rather than silently merging two orders.</para>
    /// </summary>
    private static MatchEvidence ReferenceEvidence(string? incoming, string? existing)
    {
        var exact = Evidence(incoming, existing);
        if (exact != MatchEvidence.Contradicting) return exact;
        var (shorter, longer) = incoming!.Length <= existing!.Length ? (incoming!, existing!) : (existing!, incoming!);
        return Extends(longer, shorter) ? MatchEvidence.Corroborating : MatchEvidence.Contradicting;
    }

    /// <summary>
    /// The INCOMING document carries the amended form of the reference already on file
    /// ("RFQ-4471" on the lead, "RFQ-4471 Rev B" arriving) — or the identical reference.
    ///
    /// <para>Direction matters, and only this arm cares about it. An amendment extends the
    /// reference it supersedes; the reverse — the ORIGINAL arriving after the amendment — must
    /// not silently roll the canonical record back to older commercial values, so it goes to a
    /// human even though the two references are just as related. <see cref="ReferenceEvidence"/>
    /// stays symmetric because "related but ambiguous" is exactly what a review queue is for.</para>
    /// </summary>
    private static bool ReferenceAmends(string? incoming, string? existing) =>
        incoming is not null && existing is not null
        && (string.Equals(incoming, existing, StringComparison.Ordinal) || Extends(incoming, existing));

    private static bool Extends(string longer, string shorter) =>
        shorter.Length >= 4 && longer.Length > shorter.Length
        && longer.StartsWith(shorter, StringComparison.Ordinal)
        && char.IsLetter(longer[shorter.Length]);

    private static string Diff(object previous, object current) => JsonSerializer.Serialize(new { previous, current });
    private static string ProposedSnapshot(string differencesJson)
    {
        using var document = JsonDocument.Parse(differencesJson);
        if (document.RootElement.ValueKind == JsonValueKind.String && document.RootElement.GetString() is { } nested)
            return ProposedSnapshot(nested);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("current", out var current)
            ? current.GetRawText() : "{}";
    }
    private static IEnumerable<LeadRevisionDifference> Diff(string? previousJson, string currentJson)
    {
        if (string.IsNullOrWhiteSpace(previousJson))
            return [new LeadRevisionDifference { ChangeType = LeadRevisionChangeType.Added, Scope = "Inquiry", Path = "$", CurrentValueJson = currentJson }];

        using var previousDocument = JsonDocument.Parse(previousJson);
        using var currentDocument = JsonDocument.Parse(currentJson);
        var previous = previousDocument.RootElement;
        var current = currentDocument.RootElement;
        var differences = new List<LeadRevisionDifference>();

        foreach (var field in new[] { "rfq", "buyer", "closing" })
        {
            var before = PropertyJson(previous, field);
            var after = PropertyJson(current, field);
            differences.Add(new LeadRevisionDifference
            {
                ChangeType = string.Equals(before, after, StringComparison.Ordinal)
                    ? LeadRevisionChangeType.Unchanged
                    : LeadRevisionChangeType.Modified,
                Scope = "Field",
                Path = $"$.{field}",
                PreviousValueJson = before,
                CurrentValueJson = after
            });
        }

        var previousLines = IndexedLines(previous);
        var currentLines = IndexedLines(current);
        foreach (var key in previousLines.Keys.Union(currentLines.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            var hasPrevious = previousLines.TryGetValue(key, out var before);
            var hasCurrent = currentLines.TryGetValue(key, out var after);
            var change = !hasPrevious ? LeadRevisionChangeType.Added
                : !hasCurrent ? LeadRevisionChangeType.Removed
                : JsonEquivalent(before!, after!) ? LeadRevisionChangeType.Unchanged
                : LeadRevisionChangeType.Modified;
            differences.Add(new LeadRevisionDifference
            {
                ChangeType = change,
                Scope = "Line",
                Path = $"$.items[{JsonSerializer.Serialize(key)}]",
                PreviousValueJson = hasPrevious ? before : null,
                CurrentValueJson = hasCurrent ? after : null
            });
        }

        return differences;
    }

    private static bool JsonEquivalent(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static void PopulateRevisionItems(LeadRevision revision, string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
        var line = 0;
        foreach (var item in items.EnumerateArray())
        {
            var json = item.GetRawText();
            revision.Items.Add(new LeadItemRevision { BusinessUnitId = revision.BusinessUnitId, LineNumber = ++line,
                LineFingerprint = Hash(json), SnapshotJson = json });
        }
    }

    /// <summary>
    /// Captures the incoming document's values verbatim, for a human decision that will be
    /// taken in a later request once this <see cref="Lead"/> object no longer exists.
    /// </summary>
    private static string VerbatimSnapshotJson(Lead lead) => JsonSerializer.Serialize(new VerbatimLeadSnapshot(
        lead.Rfqno, lead.BuyersName, lead.RecDate, lead.BidClosingDate, lead.HeaderRemarks,
        lead.NoOfLineItems, lead.Rfqtype,
        lead.LeadItems.Select(x => new VerbatimLeadItemSnapshot(
            x.CompanyRef, x.CustomerAccountPortalId, x.CustomerRfqno, x.ItemMaterialCode, x.CommodityProduct,
            x.BuyerName, x.LineItemNo, x.ProductShortName, x.Alternative, x.ProductShortDescription,
            x.Currency, x.UnitOfMeasure, x.UnitPrice, x.Quantity, x.StorageLocation, x.ManufacturerName,
            x.ManufacturerPartNumber, x.AlternateProductName, x.AlternatePartNumber, x.ItemText,
            x.MaterialPotext, x.LeadTime, x.ReceivedDate, x.BidClosingDateLine, x.Aiconfidence, x.ExtraFields)).ToArray()));

    /// <summary>
    /// Applies a human-confirmed amendment to a canonical <see cref="Lead"/> using the
    /// buyer's REAL values.
    ///
    /// <para>This replaces a projection that read <c>LeadMatchCandidate.DifferencesJson</c> —
    /// the normalised fingerprint snapshot. That snapshot exists to be hashed: it lowercases,
    /// strips every non-alphanumeric character and keeps five of a line item's twenty-two
    /// properties. Writing it back turned <c>RFQ-2026/0012</c> into <c>rfq20260012</c>,
    /// <c>AB-123/X</c> into <c>ab123x</c>, and deleted <c>UnitPrice</c>, <c>Currency</c>,
    /// <c>CustomerRfqno</c>, <c>ItemMaterialCode</c>, <c>ManufacturerName</c>,
    /// <c>LeadTime</c>, <c>BidClosingDateLine</c>, <c>Aiconfidence</c> and <c>ExtraFields</c>
    /// with no verbatim copy anywhere in the database. A fingerprint is a hashing artefact
    /// and is never business data.</para>
    ///
    /// <para>Returns false — and changes NOTHING — when no verbatim snapshot was captured
    /// (a candidate raised before this column existed). Refusing to project is the honest
    /// outcome: the canonical record keeps the values it already has, and the decision's
    /// audit event records that the projection was skipped.</para>
    /// </summary>
    private bool ApplyVerbatimProjection(Lead lead, string? verbatimJson)
    {
        VerbatimLeadSnapshot? snapshot;
        if (string.IsNullOrWhiteSpace(verbatimJson)) return false;
        try { snapshot = JsonSerializer.Deserialize<VerbatimLeadSnapshot>(verbatimJson); }
        catch (JsonException) { return false; }
        if (snapshot is null) return false;

        lead.Rfqno = snapshot.Rfqno;
        lead.BuyersName = snapshot.BuyersName;
        if (snapshot.RecDate.HasValue) lead.RecDate = snapshot.RecDate.Value;
        lead.BidClosingDate = snapshot.BidClosingDate;
        lead.HeaderRemarks = snapshot.HeaderRemarks;
        lead.Rfqtype = snapshot.Rfqtype;
        lead.NoOfLineItems = snapshot.NoOfLineItems ?? snapshot.Items.Count;
        lead.ModifiedDate = DateTime.UtcNow;
        if (lead.Id != 0) { _db.RemoveRange(lead.LeadItems); lead.LeadItems.Clear(); }
        foreach (var item in snapshot.Items)
            lead.LeadItems.Add(new LeadItem
            {
                CompanyRef = item.CompanyRef, CustomerAccountPortalId = item.CustomerAccountPortalId,
                CustomerRfqno = item.CustomerRfqno, ItemMaterialCode = item.ItemMaterialCode,
                CommodityProduct = item.CommodityProduct, BuyerName = item.BuyerName,
                LineItemNo = item.LineItemNo, ProductShortName = item.ProductShortName,
                Alternative = item.Alternative, ProductShortDescription = item.ProductShortDescription,
                Currency = item.Currency, UnitOfMeasure = item.UnitOfMeasure, UnitPrice = item.UnitPrice,
                Quantity = item.Quantity, StorageLocation = item.StorageLocation,
                ManufacturerName = item.ManufacturerName, ManufacturerPartNumber = item.ManufacturerPartNumber,
                AlternateProductName = item.AlternateProductName, AlternatePartNumber = item.AlternatePartNumber,
                ItemText = item.ItemText, MaterialPotext = item.MaterialPotext, LeadTime = item.LeadTime,
                ReceivedDate = item.ReceivedDate, BidClosingDateLine = item.BidClosingDateLine,
                Aiconfidence = item.Aiconfidence, ExtraFields = item.ExtraFields
            });
        return true;
    }

    private static LeadItem CloneCurrentItem(LeadItem x) => new()
    {
        CompanyRef = x.CompanyRef, CustomerAccountPortalId = x.CustomerAccountPortalId, CustomerRfqno = x.CustomerRfqno,
        ItemMaterialCode = x.ItemMaterialCode, CommodityProduct = x.CommodityProduct, BuyerName = x.BuyerName,
        LineItemNo = x.LineItemNo, ProductShortName = x.ProductShortName, Alternative = x.Alternative,
        ProductShortDescription = x.ProductShortDescription, Currency = x.Currency, UnitOfMeasure = x.UnitOfMeasure,
        UnitPrice = x.UnitPrice, Quantity = x.Quantity, StorageLocation = x.StorageLocation, ManufacturerName = x.ManufacturerName,
        ManufacturerPartNumber = x.ManufacturerPartNumber, AlternateProductName = x.AlternateProductName,
        AlternatePartNumber = x.AlternatePartNumber, ItemText = x.ItemText, MaterialPotext = x.MaterialPotext,
        LeadTime = x.LeadTime, ReceivedDate = x.ReceivedDate, BidClosingDateLine = x.BidClosingDateLine,
        Aiconfidence = x.Aiconfidence, ExtraFields = x.ExtraFields
    };

    private static string? PropertyJson(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetRawText() : null;

    private static Dictionary<string, string> IndexedLines(JsonElement snapshot)
    {
        var indexed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!snapshot.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return indexed;
        var ordinal = 0;
        foreach (var item in items.EnumerateArray())
        {
            ordinal++;
            var key = StringProperty(item, "line") ?? StringProperty(item, "part") ?? $"ordinal:{ordinal}";
            var unique = key;
            var duplicate = 2;
            while (indexed.ContainsKey(unique)) unique = $"{key}#{duplicate++}";
            indexed[unique] = item.GetRawText();
        }
        return indexed;
    }

    private static string? StringProperty(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString() is { Length: > 0 } text ? text : null;
    }
}
