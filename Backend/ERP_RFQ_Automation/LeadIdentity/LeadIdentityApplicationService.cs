using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialRouting;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.LeadIdentity;

public interface ILeadIdentityApplicationService
{
    Task<LeadReconciliationResult> ReconcileAsync(Lead candidate, LeadIntakeDescriptor intake, CancellationToken ct = default);
    Task<BatchReconciliationDto?> GetBatchAsync(long businessUnitId, Guid batchId, CancellationToken ct = default);
    Task<IReadOnlyList<PossibleMatchQueueItemDto>> GetPossibleMatchesAsync(long businessUnitId, CancellationToken ct = default);
    Task<IReadOnlyList<LeadRevisionDto>> GetRevisionsAsync(long businessUnitId, long leadId, CancellationToken ct = default);
    Task<LeadReconciliationResult> DecideMatchAsync(long businessUnitId, long occurrenceId, MatchDecisionRequest request, string actorId, CancellationToken ct = default);
    Task<LeadIdentityAnalyticsDto> GetAnalyticsAsync(long businessUnitId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public sealed class LeadIdentityApplicationService : ILeadIdentityApplicationService
{
    private const string PolicyVersion = "release-01a/v1";
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
        if (intake.ExternalAiUsed)
        {
            var recent = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
                .Where(x => x.BusinessUnitId == candidate.BusinessUnitId)
                .OrderByDescending(x => x.Id).Take(100)
                .Select(x => x.ExternalAiUsed).ToListAsync(ct);
            if ((recent.Count(x => x) + 1m) / (recent.Count + 1m) > .10m)
                throw new InvalidOperationException("External AI dependency policy would exceed 10%; route this occurrence to human review.");
        }

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
                && normalizedRfq is not null && Normalize(x.Rfqno ?? x.LeadItems.Select(i => i.CustomerRfqno)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))) == normalizedRfq);
        if (strong is not null)
            return await CreateRevisionAsync(strong, candidate, intake, fingerprint, scope,
                ["Same tenant, customer scope and normalized customer RFQ reference with changed commercial content."], tx, ownsTransaction, ct);

        var groupedLeadIds = string.IsNullOrWhiteSpace(intake.LogicalGroupKey)
            ? Array.Empty<long>()
            : await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
                .Where(x => x.BusinessUnitId == candidate.BusinessUnitId
                    && x.LogicalGroupKey == intake.LogicalGroupKey && x.LeadId.HasValue)
                .Select(x => x.LeadId!.Value).Distinct().ToArrayAsync(ct);
        var grouped = candidates.Where(x => groupedLeadIds.Contains(x.Id))
            .Select(x => new { Lead = x, Score = Similarity(candidate, x) })
            .OrderByDescending(x => x.Score).FirstOrDefault();
        if (grouped is { Score: >= 0.65m })
        {
            var groupedScope = CustomerScope(grouped.Lead, null);
            var groupedRfq = Normalize(grouped.Lead.Rfqno ?? grouped.Lead.LeadItems.Select(x => x.CustomerRfqno)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
            if ((scope is not null && groupedScope == scope)
                || (normalizedRfq is not null && groupedRfq == normalizedRfq))
                return await CreateRevisionAsync(grouped.Lead, candidate, intake, fingerprint, scope,
                    ["Corroborated logical document group, customer identity, and commercial content."], tx, ownsTransaction, ct);
        }

        var ranked = (grouped is { Score: >= 0.65m }
                ? new[] { grouped }
                : candidates.Select(x => new { Lead = x, Score = Similarity(candidate, x) }))
            .Where(x => x.Score >= 0.65m).OrderByDescending(x => x.Score).FirstOrDefault();
        if (ranked is not null && (groupedLeadIds.Contains(ranked.Lead.Id)
            || scope is null || CustomerScope(ranked.Lead, null) is null))
        {
            var occurrence = NewOccurrence(candidate.BusinessUnitId, intake, fingerprint, scope,
                LeadOccurrenceClassification.PossibleMatchReviewRequired, ranked.Score,
                [groupedLeadIds.Contains(ranked.Lead.Id)
                    ? "Documents share a logical group and similar content, but canonical identity requires review."
                    : "Commercial content is similar, but customer identity is unresolved or conflicting."], null, null);
            await EnsureBatchAsync(candidate.BusinessUnitId, intake, ct); _db.Add(occurrence);
            _db.Add(new LeadMatchCandidate { BusinessUnitId = candidate.BusinessUnitId, Occurrence = occurrence,
                CandidateLeadId = ranked.Lead.Id, Confidence = ranked.Score, ReviewState = LeadMatchReviewState.Pending,
                MatchEvidenceJson = JsonSerializer.Serialize(new { lineOverlap = ranked.Score, policy = PolicyVersion }),
                DifferencesJson = Diff(Snapshot(ranked.Lead), Snapshot(candidate)),
                DownstreamImpactJson = await DownstreamImpactJsonAsync(ranked.Lead, ct) });
            AddAudit(occurrence, null, "POSSIBLE_MATCH_RAISED", intake, new { candidateLeadId = ranked.Lead.Id, ranked.Score });
            await _db.SaveChangesAsync(ct); if (ownsTransaction) await tx.CommitAsync(ct);
            return new(0, string.Empty, occurrence.Id, null, 0, occurrence.Classification, occurrence.Confidence, occurrence.DecisionReasons(), false);
        }

        await EnsureBatchAsync(candidate.BusinessUnitId, intake, ct);
        _db.Add(candidate); await _db.SaveChangesAsync(ct);
        var newOccurrence = NewOccurrence(candidate.BusinessUnitId, intake, fingerprint, scope,
            LeadOccurrenceClassification.New, 1m, ["No reliable tenant-scoped canonical inquiry match."], candidate.Id, null);
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
        var filesReceived = await (
            from occurrence in _db.Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>().AsNoTracking()
            join corpus in _db.Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.DocumentCorpus>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.CorpusId }
                equals new { corpus.BusinessUnitId, CorpusId = corpus.Id }
            where occurrence.BusinessUnitId == bu && corpus.BatchId == batchId
            select occurrence.Id).Distinct().CountAsync(ct);
        var rows = await _db.Set<LeadIngestionOccurrence>().AsNoTracking().Where(x => x.BusinessUnitId == bu && x.BatchId == batchId)
            .Include(x => x.Lead).ThenInclude(x => x!.AssignToNavigation)
            .Include(x => x.MatchCandidates).ThenInclude(x => x.CandidateLead)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var items = rows.Select(x => new BatchReconciliationItemDto(x.Id, x.LeadId, x.Lead?.CommercialCaseReference,
            x.Classification.ToString(), x.Lead?.CurrentRevisionNumber, x.OriginalFileName, x.IngestedAtUtc,
            x.ProcessingPath.ToString(), x.ExternalAiUsed, x.Confidence, x.DecisionReasons(),
            x.MatchCandidates.Select(c => new LeadMatchCandidateDto(c.Id, c.CandidateLeadId,
                c.CandidateLead.CommercialCaseReference, c.CandidateLead.Rfqno, c.Confidence,
                c.MatchEvidenceJson, c.DifferencesJson, c.DownstreamImpactJson,
                c.ReviewState.ToString(), c.Version)).ToArray(),
            x.Lead?.CustomerMatchStatus ?? "Awaiting customer resolution",
            x.Lead?.AssignToNavigation is null ? null
                : $"{x.Lead.AssignToNavigation.FirstName} {x.Lead.AssignToNavigation.LastName}".Trim())).ToArray();
        return new(batchId, filesReceived, rows.Count,
            Count(LeadOccurrenceClassification.New), Count(LeadOccurrenceClassification.ExactDuplicate), Count(LeadOccurrenceClassification.Revision),
            Count(LeadOccurrenceClassification.PossibleMatchReviewRequired), Count(LeadOccurrenceClassification.RejectedOrUnprocessable),
            rows.Count(x => x.ExternalAiUsed), rows.Sum(x => x.ExternalCost), items);
        int Count(LeadOccurrenceClassification c) => rows.Count(x => x.Classification == c);
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
        var source = _db.Set<LeadIngestionOccurrence>().AsNoTracking().Where(x => x.BusinessUnitId == bu);
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
            ? selected.Where(x => x.SourceDocumentOccurrenceId.HasValue)
                .Select(x => x.SourceDocumentOccurrenceId!.Value).Distinct().ToArray()
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
            ApplySnapshotProjection(canonical, proposed);
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
            ApplySnapshotProjection(newLead, proposed);
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
            EventType = "POSSIBLE_MATCH_DECIDED", PayloadJson = JsonSerializer.Serialize(new { request.Action, request.Reason, request.CandidateLeadId }),
            ActorType = "User", ActorId = actorId, CorrelationId = $"review:{occurrence.Id}", IdempotencyKey = request.IdempotencyKey, OccurredAtUtc = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync(ct);
        var lead = occurrence.LeadId.HasValue ? await _db.Leads.AsNoTracking().SingleAsync(x => x.Id == occurrence.LeadId, ct) : null;
        if (ownsTransaction) await transaction!.CommitAsync(ct);
        if (request.Action == "create_new") await RouteReviewedNewAsync(bu, lead?.Id, request.IdempotencyKey, ct);
        return new(lead?.Id ?? 0, lead?.CommercialCaseReference ?? "", occurrence.Id, occurrence.LeadRevisionId, lead?.CurrentRevisionNumber ?? 0,
            occurrence.Classification, candidate.Confidence, ["Governed human match-review decision."], request.Action == "create_new");
    }

    private async Task RouteReviewedNewAsync(long bu, long? leadId, string idempotencyKey, CancellationToken ct)
    {
        if (_routing is null || !leadId.HasValue) return;
        await _routing.RouteLeadAsync(bu, new RouteLeadCommand(leadId.Value, $"match-review:{Hash(idempotencyKey)}",
            $"match-review:{leadId.Value}"), ct);
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
        Normalize(x.ProductShortDescription ?? x.ItemText), x.Quantity, Normalize(x.UnitOfMeasure), x.BidClosingDateLine?.ToUniversalTime().ToString("O"));
    private static string LineFingerprint(LeadItem x) => Hash(JsonSerializer.Serialize(ItemSnapshot(x)));
    private static string LineIdentityFingerprint(LeadItem x) => Hash(JsonSerializer.Serialize(new
    {
        part = Normalize(x.ManufacturerPartNumber ?? x.ItemMaterialCode),
        description = Normalize(x.ProductShortDescription ?? x.ItemText),
        uom = Normalize(x.UnitOfMeasure)
    }));
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

    private void ApplySnapshotProjection(Lead lead, string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        lead.Rfqno = StringProperty(root, "rfq");
        lead.BuyersName = StringProperty(root, "buyer");
        if (root.TryGetProperty("closing", out var closing) && closing.ValueKind == JsonValueKind.String
            && DateTime.TryParse(closing.GetString(), out var closingDate)) lead.BidClosingDate = closingDate;
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
        if (lead.Id != 0) { _db.RemoveRange(lead.LeadItems); lead.LeadItems.Clear(); }
        foreach (var item in items.EnumerateArray())
            lead.LeadItems.Add(new LeadItem { LineItemNo = StringProperty(item, "line"), ManufacturerPartNumber = StringProperty(item, "part"),
                ProductShortDescription = StringProperty(item, "description"), Quantity = item.TryGetProperty("Quantity", out var upperQuantity)
                    ? upperQuantity.GetInt32() : item.TryGetProperty("quantity", out var quantity) ? quantity.GetInt32() : 0,
                UnitOfMeasure = StringProperty(item, "uom") });
        lead.NoOfLineItems = items.GetArrayLength();
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
