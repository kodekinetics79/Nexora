using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Services.Uom;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

public sealed class LeadParticipationService : ILeadParticipationService
{
    public static readonly string[] GovernedFitCriterionCodes =
        ["ELIGIBILITY", "CAPABILITY", "DELIVERY", "COMPLIANCE", "COMMERCIAL"];
    private readonly ErpRfqAutomationContext _db;
    private readonly ILeadDecisionService _decisionIntelligence;
    private readonly ILeadConversionIntelligence _conversionIntelligence;
    private readonly ILeadOutcomeReasons _leadOutcomeReasons;

    public LeadParticipationService(
        ErpRfqAutomationContext db,
        ILeadDecisionService decisionIntelligence,
        ILeadConversionIntelligence conversionIntelligence,
        ILeadOutcomeReasons leadOutcomeReasons)
    {
        _db = db;
        _decisionIntelligence = decisionIntelligence;
        _conversionIntelligence = conversionIntelligence;
        _leadOutcomeReasons = leadOutcomeReasons;
    }

    // Keeps focused domain tests and non-DI composition roots source-compatible while routing
    // them through the same deterministic, read-only catalog preview used in production.
    public LeadParticipationService(
        ErpRfqAutomationContext db,
        ILeadDecisionService decisionIntelligence,
        ILeadOutcomeReasons leadOutcomeReasons)
        : this(db, decisionIntelligence, new LeadConversionIntelligence(db), leadOutcomeReasons)
    {
    }

    public async Task<LeadFitAssessmentResult> RecordFitAssessmentAsync(
        long businessUnitId, long leadId, RecordLeadFitAssessmentCommand command, CancellationToken ct = default)
    {
        ValidateIdentity(businessUnitId, command.IdempotencyKey, command.Actor);
        var overallDecision = NormalizeOverallDecision(command.OverallDecision);
        if (string.IsNullOrWhiteSpace(command.Rationale))
            throw new ArgumentException("A human fit-assessment rationale is required.");
        var criteria = command.Criteria ?? Array.Empty<LeadFitCriterionCommand>();
        if (criteria.Count == 0 || criteria.Any(x => string.IsNullOrWhiteSpace(x.Code)))
            throw new ArgumentException("At least one named human fit criterion is required.");
        var normalizedCriteria = criteria.Select(x => new
        {
            Code = x.Code.Trim().ToUpperInvariant(),
            Decision = NormalizeCriterionDecision(x.Decision),
            Note = Clean(x.Note)
        }).ToArray();
        if (!HasExactGovernedFitCriteria(normalizedCriteria.Select(x => x.Code)))
            throw new ArgumentException(
                $"The fit assessment must decide each governed criterion exactly once: {string.Join(", ", GovernedFitCriterionCodes)}.");
        if (normalizedCriteria.Any(x => x.Decision == "CONCERN" && (x.Note is null || x.Note.Length < 5)))
            throw new ArgumentException("Every fit concern requires a meaningful evidence note.");
        var requestHash = Hash(new
        {
            businessUnitId, leadId, command.ExpectedLeadRevisionId, command.ExpectedDecisionVersion,
            command.ExpectedFitVersion, OverallDecision = overallDecision, Rationale = command.Rationale.Trim(),
            Criteria = normalizedCriteria.OrderBy(x => x.Code)
        });
        var replay = await _db.Set<LeadFitAssessment>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == command.IdempotencyKey, ct);
        if (replay is not null) return FitReplay(replay, requestHash);

        // The deterministic brief is persisted verbatim so a later policy/catalog change cannot
        // rewrite the evidence the person used when deciding this revision.
        var brief = await _decisionIntelligence.GetBriefAsync(leadId, businessUnitId, ct);
        var snapshot = JsonSerializer.Serialize(new
        {
            human = new
            {
                overallDecision,
                rationale = command.Rationale.Trim(),
                criteria = normalizedCriteria.Select(x => new { code = x.Code, decision = x.Decision, note = x.Note })
            },
            decisionIntelligence = brief
        });
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var lead = await _db.Leads.AsNoTracking().Include(x => x.LeadStatus)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
                ?? throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");
            EnsureCurrentRevision(lead, command.ExpectedLeadRevisionId);
            EnsureDecisionVersion(lead, command.ExpectedDecisionVersion);
            await EnsureDecisionRecordIsOpenAsync(lead, ct);

            replay = await _db.Set<LeadFitAssessment>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == command.IdempotencyKey, ct);
            if (replay is not null) return FitReplay(replay, requestHash);

            var currentVersion = await _db.Set<LeadFitAssessment>()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                .MaxAsync(x => (int?)x.Sequence, ct);
            var expectedFitVersion = command.ExpectedFitVersion is null or 0 ? null : command.ExpectedFitVersion;
            if (currentVersion != expectedFitVersion)
                throw new InvalidOperationException("The fit assessment changed after this workbench was loaded. Refresh and try again.");
            var sequence = (currentVersion ?? 0) + 1;
            var entity = new LeadFitAssessment
            {
                BusinessUnitId = businessUnitId,
                LeadId = leadId,
                LeadRevisionId = command.ExpectedLeadRevisionId,
                Sequence = sequence,
                PolicyVersion = brief.PolicyVersion,
                Recommendation = overallDecision,
                IsActionable = IsHumanFitActionable(overallDecision, normalizedCriteria.Select(x => x.Decision)),
                AssessmentJson = snapshot,
                IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = requestHash,
                AssessedBy = command.Actor.Trim(),
                AssessedAtUtc = DateTimeOffset.UtcNow
            };
            _db.Add(entity);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
                return ToResult(entity);
            });
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            replay = await _db.Set<LeadFitAssessment>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == command.IdempotencyKey, ct);
            if (replay is not null) return FitReplay(replay, requestHash);
            throw;
        }
    }

    public async Task<LeadParticipationResult> CommitDecisionAsync(
        long businessUnitId, long leadId, CommitLeadParticipationCommand command, CancellationToken ct = default)
    {
        ValidateIdentity(businessUnitId, command.IdempotencyKey, command.Actor);
        var suppliedLines = command.Lines ?? Array.Empty<LeadLineParticipationCommand>();
        var canonicalRequest = new
        {
            businessUnitId,
            leadId,
            command.ExpectedLeadRevisionId,
            command.ExpectedDecisionVersion,
            command.ExpectedParticipationVersion,
            command.Commit,
            command.FitAssessmentId,
            command.ReasonCode,
            command.Notes,
            Lines = suppliedLines.OrderBy(x => x.LeadItemRevisionId).Select(x => new
            {
                x.LeadItemRevisionId, x.Choice, x.ReasonCode, x.ReasonNotes, x.ProductId,
                x.Quantity, UnitOfMeasure = x.UnitOfMeasure?.Trim(), Currency = x.Currency?.Trim().ToUpperInvariant()
            })
        };
        var requestHash = Hash(canonicalRequest);
        var replay = await LoadDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
        if (replay is not null) return DecisionReplay(replay, requestHash);

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var lead = await _db.Leads.AsNoTracking().Include(x => x.LeadStatus)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
                ?? throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");
            EnsureCurrentRevision(lead, command.ExpectedLeadRevisionId);
            EnsureDecisionVersion(lead, command.ExpectedDecisionVersion);
            await EnsureDecisionRecordIsOpenAsync(lead, ct);

            replay = await LoadDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
            if (replay is not null) return DecisionReplay(replay, requestHash);

            var revisionLines = await _db.Set<LeadItemRevision>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                .Select(x => new { x.Id, x.LeadItemId }).ToListAsync(ct);
            var revisionLineIds = revisionLines.Select(x => x.Id).ToList();
            if (revisionLineIds.Count == 0)
                throw new InvalidOperationException("The current lead revision has no requested lines to decide.");
            var suppliedIds = suppliedLines.Select(x => x.LeadItemRevisionId).ToList();
            if (suppliedIds.Count != suppliedIds.Distinct().Count())
                throw new ArgumentException("Each current revision line must be decided exactly once.");
            if (suppliedIds.Count != revisionLineIds.Count || suppliedIds.Except(revisionLineIds).Any())
                throw new ArgumentException("A participation decision must contain exactly one answer for every line of the current lead revision.");

            var preview = await _conversionIntelligence.PreviewAsync(leadId, businessUnitId, ct);
            var previewByLeadItem = preview.Items.ToDictionary(x => x.LeadItemId);
            var leadItemByRevisionLine = revisionLines
                .Where(x => x.LeadItemId.HasValue)
                .ToDictionary(x => x.Id, x => x.LeadItemId!.Value);
            var currentLeadItemIds = leadItemByRevisionLine.Values.Distinct().ToArray();
            var currentLeadItems = await _db.LeadItems.AsNoTracking()
                .Where(x => currentLeadItemIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
            var effectiveBidValues = suppliedLines.Where(x => x.Choice == LeadLineParticipationChoice.Bid)
                .Select(line =>
                {
                    var source = leadItemByRevisionLine.TryGetValue(line.LeadItemRevisionId, out var leadItemId)
                        && currentLeadItems.TryGetValue(leadItemId, out var found) ? found : null;
                    return new
                    {
                        line.LeadItemRevisionId,
                        Quantity = line.Quantity ?? source?.Quantity,
                        Uom = UomCanonicalizer.CanonicalizeForStorage(line.UnitOfMeasure ?? source?.UnitOfMeasure),
                        Currency = Clean(line.Currency ?? source?.Currency)?.ToUpperInvariant()
                    };
                }).ToArray();
            var requestedUoms = effectiveBidValues.Where(x => x.Uom is not null)
                .Select(x => x.Uom!.ToUpperInvariant()).Distinct().ToArray();
            var requestedCurrencies = effectiveBidValues.Where(x => x.Currency is not null)
                .Select(x => x.Currency!).Distinct().ToArray();
            var uomMasters = await _db.SetUoms.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                    && x.IsActive && requestedUoms.Contains(x.UomCode.ToUpper()))
                .ToListAsync(ct);
            var currencyMasters = await _db.Currencies.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                    && x.IsActive != false && requestedCurrencies.Contains(x.Code.ToUpper()))
                .ToListAsync(ct);
            var uomByCode = uomMasters.GroupBy(x => x.UomCode.ToUpperInvariant())
                .ToDictionary(x => x.Key, x => x.Single());
            var currencyByCode = currencyMasters.GroupBy(x => x.Code.ToUpperInvariant())
                .ToDictionary(x => x.Key, x => x.Single());
            var resolvedCommercialByRevisionLine = effectiveBidValues.ToDictionary(x => x.LeadItemRevisionId, x =>
            {
                var uom = x.Uom is null ? null : uomByCode.GetValueOrDefault(x.Uom.ToUpperInvariant());
                var currency = x.Currency is null ? null : currencyByCode.GetValueOrDefault(x.Currency);
                return new { x.Quantity, Uom = uom, Currency = currency };
            });

            var latestFit = await _db.Set<LeadFitAssessment>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId
                    && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Save a human fit assessment for the current revision before deciding participation.");
            if (command.FitAssessmentId.HasValue && command.FitAssessmentId.Value != latestFit.Id)
                throw new InvalidOperationException("The selected fit assessment is not the latest assessment for the current revision.");
            var fit = latestFit;

            if (command.Commit && suppliedLines.Any(x => x.Choice == LeadLineParticipationChoice.Bid))
            {
                if (!fit.IsActionable)
                    throw new InvalidOperationException(
                        "A committed Bid requires an actionable human fit assessment for the current revision.");
                LeadConversionGate.EnsureEligible(lead);
                if (!lead.CommercialFactsVerified)
                    throw new InvalidOperationException(
                        "A committed Bid requires human verification of the current revision's commercial facts.");
                await EnsureBidSourceEvidenceReadyAsync(businessUnitId, leadId,
                    command.ExpectedLeadRevisionId, suppliedLines, leadItemByRevisionLine,
                    currentLeadItems, ct);
            }

            var currentParticipationVersion = await _db.Set<LeadParticipationDecision>()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == command.ExpectedLeadRevisionId)
                .MaxAsync(x => (int?)x.Sequence, ct);
            if (currentParticipationVersion != command.ExpectedParticipationVersion)
                throw new InvalidOperationException("Participation changed after this workbench was loaded. Refresh and try again.");

            foreach (var line in suppliedLines)
            {
                if (line.Quantity is <= 0)
                    throw new ArgumentException($"Quantity for revision line {line.LeadItemRevisionId} must be greater than zero.");
                if (line.Choice == LeadLineParticipationChoice.NoBid
                    && (string.IsNullOrWhiteSpace(line.ReasonCode)
                        || await _leadOutcomeReasons.ResolveAsync(businessUnitId, line.ReasonCode.Trim(), ct) is null))
                    throw new ArgumentException(
                        $"No-bid revision line {line.LeadItemRevisionId} requires a reason from this business unit's governed outcome-reason list.");
                if (line.Choice == LeadLineParticipationChoice.Clarify
                    && (string.IsNullOrWhiteSpace(line.ReasonCode)
                        || !GovernedClarificationReasonCodes.Contains(line.ReasonCode.Trim(), StringComparer.OrdinalIgnoreCase)))
                    throw new ArgumentException(
                        $"Clarify revision line {line.LeadItemRevisionId} requires a governed clarification reason.");
                if (command.Commit && line.Choice == LeadLineParticipationChoice.Bid
                    && leadItemByRevisionLine.TryGetValue(line.LeadItemRevisionId, out var leadItemId)
                    && previewByLeadItem.TryGetValue(leadItemId, out var linePreview)
                    && linePreview.NeedsAttention
                    && (string.IsNullOrWhiteSpace(line.ReasonNotes) || line.ReasonNotes.Trim().Length < 5))
                    throw new ArgumentException(
                        $"Bid revision line {line.LeadItemRevisionId} has a catalog or normalization warning and requires a meaningful human acknowledgement note.");
                if (command.Commit && line.Choice == LeadLineParticipationChoice.Bid)
                {
                    if (!leadItemByRevisionLine.TryGetValue(line.LeadItemRevisionId, out var currentLeadItemId)
                        || !currentLeadItems.TryGetValue(currentLeadItemId, out var sourceLine))
                        throw new InvalidOperationException(
                            $"Bid revision line {line.LeadItemRevisionId} has no current canonical Lead-line projection.");
                    var effective = resolvedCommercialByRevisionLine[line.LeadItemRevisionId];
                    if (effective.Quantity is null or <= 0)
                        throw new ArgumentException($"Bid revision line {line.LeadItemRevisionId} requires a positive quantity.");
                    if (effective.Uom is null)
                        throw new ArgumentException($"Bid revision line {line.LeadItemRevisionId} requires an active tenant unit of measure.");
                    if (effective.Currency is null)
                        throw new ArgumentException($"Bid revision line {line.LeadItemRevisionId} requires an active tenant currency.");
                }
            }

            var hasUnresolved = suppliedLines.Any(x => x.Choice is LeadLineParticipationChoice.Pending or LeadLineParticipationChoice.Clarify);
            if (command.Commit && hasUnresolved)
                throw new InvalidOperationException("A committed participation decision cannot contain Pending or Clarify lines. Resolve them or save a draft.");

            var explicitProductIds = suppliedLines.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToList();
            if (explicitProductIds.Count > 0)
            {
                var visible = await _db.Products.AsNoTracking().Where(x => explicitProductIds.Contains(x.Id))
                    .Select(x => x.Id).ToListAsync(ct);
                var missing = explicitProductIds.Except(visible).ToList();
                if (missing.Count > 0) throw new ArgumentException($"Product(s) [{string.Join(", ", missing)}] are not visible to this business unit.");
            }

            var bidCount = suppliedLines.Count(x => x.Choice == LeadLineParticipationChoice.Bid);
            var outcome = !command.Commit || hasUnresolved ? LeadParticipationOutcome.Pending
                : bidCount == 0 ? LeadParticipationOutcome.NoBid
                : bidCount == suppliedLines.Count ? LeadParticipationOutcome.FullBid
                : LeadParticipationOutcome.PartialBid;
            var headerReasonCode = Clean(command.ReasonCode);
            if (outcome == LeadParticipationOutcome.NoBid)
            {
                if (headerReasonCode is null || await _leadOutcomeReasons.ResolveAsync(businessUnitId, headerReasonCode, ct) is null)
                    throw new ArgumentException("A full no-bid commitment requires a reason from this business unit's governed outcome-reason list.");
            }
            var sequence = (currentParticipationVersion ?? 0) + 1;
            var entity = new LeadParticipationDecision
            {
                BusinessUnitId = businessUnitId,
                LeadId = leadId,
                LeadRevisionId = command.ExpectedLeadRevisionId,
                FitAssessmentId = fit.Id,
                Sequence = sequence,
                IsCommitted = command.Commit,
                Outcome = outcome,
                ReasonCode = headerReasonCode,
                Notes = Clean(command.Notes),
                IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = requestHash,
                DecidedBy = command.Actor.Trim(),
                DecidedAtUtc = DateTimeOffset.UtcNow
            };
            foreach (var line in suppliedLines)
            {
                var effective = line.Choice == LeadLineParticipationChoice.Bid
                    ? resolvedCommercialByRevisionLine[line.LeadItemRevisionId] : null;
                var linePreview = leadItemByRevisionLine.TryGetValue(line.LeadItemRevisionId, out var previewLeadItemId)
                    && previewByLeadItem.TryGetValue(previewLeadItemId, out var foundPreview) ? foundPreview : null;
                entity.Lines.Add(new LeadLineParticipationDecision
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = leadId,
                    LeadRevisionId = command.ExpectedLeadRevisionId,
                    LeadItemRevisionId = line.LeadItemRevisionId,
                    DecisionIsCommitted = command.Commit,
                    Choice = line.Choice,
                    ReasonCode = Clean(line.ReasonCode),
                    ReasonNotes = Clean(line.ReasonNotes),
                    ProductId = line.ProductId,
                    Quantity = effective?.Quantity ?? line.Quantity,
                    UnitOfMeasure = effective?.Uom?.UomCode ?? Clean(line.UnitOfMeasure),
                    UomId = effective?.Uom?.UomId,
                    Currency = effective?.Currency?.Code.ToUpperInvariant() ?? Clean(line.Currency)?.ToUpperInvariant(),
                    CurrencyId = effective?.Currency?.Id,
                    CatalogPolicyVersion = "lead-conversion-preview/v1",
                    WarningSnapshotJson = JsonSerializer.Serialize(new
                    {
                        linePreview?.NeedsAttention,
                        linePreview?.AttentionReason,
                        linePreview?.Confidence,
                        Matches = linePreview?.Matches.Select(x => new
                        {
                            x.ProductId, x.ProductName, x.MaterialCode, x.ManufacturerPartNumber, x.Score, x.Reason
                        }) ?? []
                    })
                });
            }
            _db.Add(entity);
            await _db.SaveChangesAsync(ct);
            if (outcome == LeadParticipationOutcome.NoBid)
            {
                var lifecycle = new LifecycleApplicationService(_db, _leadOutcomeReasons);
                await lifecycle.TransitionLeadInCurrentTransactionAsync(
                    businessUnitId,
                    leadId,
                    new LifecycleActor(command.Actor.Trim(), "AuthenticatedUser"),
                    new LifecycleTransitionCommand(
                        "DISQUALIFIED",
                        lead.LifecycleVersion,
                        headerReasonCode,
                        Clean(command.Notes) ?? "Full no-bid participation decision.",
                        "LeadParticipation",
                        $"participation-{entity.Id}",
                        $"lead-{leadId}",
                        $"lead-no-bid:{businessUnitId}:{entity.Id}"),
                    reopen: false,
                    ct);
            }
            await tx.CommitAsync(ct);
                return ToResult(entity);
            });
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            replay = await LoadDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
            if (replay is not null) return DecisionReplay(replay, requestHash);
            throw;
        }
    }

    private async Task EnsureBidSourceEvidenceReadyAsync(long businessUnitId, long leadId,
        long leadRevisionId, IReadOnlyList<LeadLineParticipationCommand> suppliedLines,
        IReadOnlyDictionary<long, long> leadItemByRevisionLine,
        IReadOnlyDictionary<long, LeadItem> currentLeadItems, CancellationToken ct)
    {
        var bidLeadItemIds = suppliedLines.Where(x => x.Choice == LeadLineParticipationChoice.Bid)
            .Select(x => leadItemByRevisionLine.GetValueOrDefault(x.LeadItemRevisionId))
            .Where(x => x > 0).Distinct().ToArray();
        if (bidLeadItemIds.Length != suppliedLines.Count(x => x.Choice == LeadLineParticipationChoice.Bid))
            throw new InvalidOperationException(
                "Every Bid line must retain exact immutable canonical Lead-item lineage before participation can be committed.");
        var evidenceSourceIds = bidLeadItemIds.Select(id => currentLeadItems[id].EvidenceSourceLeadItemId ?? id)
            .Distinct().ToArray();
        var occurrenceId = await _db.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == leadRevisionId && x.LeadId == leadId)
            .Select(x => x.EstablishedByOccurrenceId).SingleAsync(ct);
        var documentIds = await _db.Set<LeadOccurrenceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.OccurrenceId == occurrenceId)
            .Select(x => x.SourceDocumentId).Distinct().ToListAsync(ct);
        var directDocumentId = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == occurrenceId && x.LeadId == leadId)
            .Select(x => x.SourceDocumentId).SingleAsync(ct);
        if (directDocumentId.HasValue && !documentIds.Contains(directDocumentId.Value))
            documentIds.Add(directDocumentId.Value);
        if (documentIds.Count == 0)
            throw new InvalidOperationException(
                "A committed Bid requires retained source documents for the current revision.");

        var evidencedLeadItemIds = await (from field in _db.Set<FieldEvidence>().AsNoTracking()
            join job in _db.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                on new { field.BusinessUnitId, Id = field.ExtractionRun.ExtractionJobId }
                equals new { job.BusinessUnitId, job.Id }
            where field.BusinessUnitId == businessUnitId && field.LineItem != null
                && field.LineItem.LeadItemId.HasValue
                && evidenceSourceIds.Contains(field.LineItem.LeadItemId.Value)
                && documentIds.Contains(field.ExtractionRun.SourceDocumentId)
                && field.ExtractionRun.SourceDocument.SecurityStatus == DocumentSecurityStatus.Cleared
                && field.ExtractionRun.SourceDocument.PurgeState == EvidencePurgeState.Present
                && field.ExtractionRun.SourceDocument.ExtractionJobId == job.Id
                && field.ExtractionRun.SourceDocument.ContentHash == job.ContentHash
                && job.StoragePath != null && job.StoragePath != ""
            select field.LineItem.LeadItemId.Value).Distinct().ToArrayAsync(ct);
        if (evidenceSourceIds.Except(evidencedLeadItemIds).Any())
            throw new InvalidOperationException(
                "Every Bid line requires exact persisted source-field evidence for the current revision before participation can be committed.");
    }

    private static readonly string[] GovernedClarificationReasonCodes =
        ["TECHNICAL_CLARIFICATION", "COMMERCIAL_CLARIFICATION", "DELIVERY_CLARIFICATION"];

    public async Task<LeadParticipationResult?> GetCurrentDecisionAsync(
        long businessUnitId, long leadId, CancellationToken ct = default)
    {
        var revisionId = await _db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == leadId)
            .Select(x => x.CurrentRevisionId).SingleOrDefaultAsync(ct);
        if (!revisionId.HasValue) return null;
        var entity = await _db.Set<LeadParticipationDecision>().AsNoTracking().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId && x.LeadRevisionId == revisionId.Value)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        return entity is null ? null : ToResult(entity);
    }

    private Task<LeadParticipationDecision?> LoadDecisionByKeyAsync(long businessUnitId, string key, CancellationToken ct) =>
        _db.Set<LeadParticipationDecision>().AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct);

    private static LeadFitAssessmentResult FitReplay(LeadFitAssessment entity, string requestHash)
    {
        if (!string.Equals(entity.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key was already used for a different fit assessment request.");
        return ToResult(entity);
    }

    private static LeadParticipationResult DecisionReplay(LeadParticipationDecision entity, string requestHash)
    {
        if (!string.Equals(entity.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key was already used for a different participation request.");
        return ToResult(entity);
    }

    private static void ValidateIdentity(long businessUnitId, string key, string actor)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An idempotency key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }

    private static void EnsureCurrentRevision(Lead lead, long expectedRevisionId)
    {
        if (!lead.CurrentRevisionId.HasValue)
            throw new InvalidOperationException("The lead has no immutable current revision.");
        if (lead.CurrentRevisionId.Value != expectedRevisionId)
            throw new InvalidOperationException("The lead changed after this workbench was loaded. Refresh and assess the current revision.");
    }

    private static void EnsureDecisionVersion(Lead lead, int expectedDecisionVersion)
    {
        if (lead.CurrentRevisionNumber != expectedDecisionVersion)
            throw new InvalidOperationException("The Lead decision version changed after this workbench was loaded. Refresh and try again.");
    }

    private async Task EnsureDecisionRecordIsOpenAsync(Lead lead, CancellationToken ct)
    {
        var status = LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue);
        if (status is "CONVERTED_TO_RFQ" or "QUOTED" or "NEGOTIATION" or "AWARDED"
            or "PARTIALLY_AWARDED" or "LOST" or "COMPLETED")
            throw new InvalidOperationException(
                "The Lead decision record is read-only after RFQ conversion or downstream commercial activity.");

        if (await _db.Rfqs.AsNoTracking()
            .AnyAsync(x => x.BusinessUnitId == lead.BusinessUnitId && x.LeadId == lead.Id, ct))
            throw new InvalidOperationException(
                "The Lead decision record is read-only because a formal RFQ already exists.");
    }

    private static string NormalizeOverallDecision(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized is "FIT" or "CONDITIONAL" or "NOT_FIT"
            ? normalized
            : throw new ArgumentException("Overall fit decision must be FIT, CONDITIONAL, or NOT_FIT.");
    }

    private static string NormalizeCriterionDecision(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized is "PASS" or "CONCERN" or "UNKNOWN" or "NOT_APPLICABLE"
            ? normalized
            : throw new ArgumentException("Fit criterion decision must be PASS, CONCERN, UNKNOWN, or NOT_APPLICABLE.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static bool IsHumanFitActionable(string overallDecision, IEnumerable<string> criterionDecisions)
    {
        var governedOverall = NormalizeOverallDecision(overallDecision);
        var governedCriteria = criterionDecisions.Select(NormalizeCriterionDecision).ToArray();
        return governedCriteria.Length > 0
            && governedCriteria.All(x => x is "PASS" or "NOT_APPLICABLE")
            && governedOverall is "FIT" or "CONDITIONAL";
    }

    public static bool HasExactGovernedFitCriteria(IEnumerable<string> codes)
    {
        var supplied = codes.Select(x => x.Trim().ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var governed = GovernedFitCriterionCodes.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return supplied.Length == governed.Length
            && supplied.Distinct(StringComparer.Ordinal).Count() == governed.Length
            && supplied.SequenceEqual(governed, StringComparer.Ordinal);
    }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
    private static LeadFitAssessmentResult ToResult(LeadFitAssessment x) =>
        new(x.Id, x.LeadId, x.LeadRevisionId, x.Sequence, x.PolicyVersion, x.Recommendation,
            x.IsActionable, x.AssessmentJson, x.AssessedAtUtc);
    private static LeadParticipationResult ToResult(LeadParticipationDecision x) =>
        new(x.Id, x.LeadId, x.LeadRevisionId, x.FitAssessmentId, x.Sequence, x.IsCommitted, x.Outcome,
            x.ReasonCode, x.Notes, x.DecidedAtUtc, x.Lines.OrderBy(l => l.LeadItemRevisionId)
                .Select(l => new LeadLineParticipationResult(l.LeadItemRevisionId, l.Choice, l.ReasonCode,
                    l.ReasonNotes, l.ProductId, l.Quantity, l.UnitOfMeasure, l.UomId, l.Currency, l.CurrencyId,
                    l.CatalogPolicyVersion, l.WarningSnapshotJson)).ToArray());
}
