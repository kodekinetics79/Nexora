using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Opportunity;

public sealed class OpportunityPriorityApplicationService(
    ErpRfqAutomationContext db,
    ITenantContext tenantContext,
    ILeadDecisionService leadDecisions) : IOpportunityPriorityApplicationService
{
    public const string PolicyVersion = "opportunity-priority-shadow-v3";
    public const string FeatureSchemaVersion = "opportunity-commercial-components-v2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly OpportunityActionOption[] ActionCatalog =
    [
        new("REVIEW_EXPIRED_DEADLINE", "Review expired deadline"),
        new("RESOLVE_CUSTOMER_IDENTITY", "Resolve Customer identity"),
        new("REQUEST_CLARIFICATION", "Request Product clarification"),
        new("REFRESH_FULFILMENT_EVIDENCE", "Refresh fulfilment evidence"),
        new("SEARCH_KNOWN_SUPPLIERS", "Search known Suppliers"),
        new("RESERVE_AVAILABLE_STOCK", "Review stock reservation"),
        new("ESCALATE_APPROVAL", "Escalate margin review"),
        new("REVIEW_INQUIRY", "Review inquiry evidence"),
        new("OPEN_OPPORTUNITY", "Open opportunity"),
        new("RESOLVE_UNMATCHED_PARTS", "Resolve unmatched parts"),
        new("SOURCE_UNKNOWN_PARTS", "Review unknown parts")
    ];

    public async Task<OpportunityPriorityPage> QueryAsync(
        long businessUnitId,
        OpportunityPriorityQuery query,
        OpportunityPriorityAccessScope scope,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(query.PageNumber));
        if (query.PageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize));

        var latestIds = db.OpportunityRecommendations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .GroupBy(x => x.CommercialCaseId)
            .Select(x => x.Max(y => y.Id));
        var scopedSource = db.OpportunityRecommendations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && latestIds.Contains(x.Id));
        if (!scope.TenantWide)
            scopedSource = scopedSource.Where(x => x.Lead.AssignTo == scope.OwnerUserId!.Value);
        var source = scopedSource.Where(x =>
            !db.Orders.Any(order =>
                order.BusinessUnitId == businessUnitId &&
                order.CommercialCaseId == x.CommercialCaseId) &&
            !db.Quotes.Any(quote =>
                quote.BusinessUnitId == businessUnitId &&
                quote.CommercialCaseId == x.CommercialCaseId &&
                quote.Status != null &&
                (quote.Status.SetupCode == "ACCEPTED" ||
                 quote.Status.SetupCode == "REJECTED" ||
                 quote.Status.SetupCode == "EXPIRED" ||
                 quote.Status.SetupCode == "ORDERED")));
        if (!string.IsNullOrWhiteSpace(query.PriorityBand))
            source = source.Where(x => x.PriorityBand == query.PriorityBand.Trim());
        if (query.InsufficientEvidenceOnly)
            source = source.Where(x => x.ExpectedCommercialValue == null);

        var total = await source.CountAsync(cancellationToken);
        var currentRecommendations = await scopedSource.CountAsync(cancellationToken);
        var eligible = await source.CountAsync(
            x => x.CohortKey == "eligible-shadow", cancellationToken);
        var insufficientEvidence = await source.CountAsync(
            x => x.CohortKey == "insufficient-evidence", cancellationToken);
        var withObservedOutcome = await scopedSource.CountAsync(
            x => db.OpportunityOutcomes.Any(outcome =>
                outcome.BusinessUnitId == businessUnitId &&
                outcome.OpportunityRecommendationId == x.Id),
            cancellationToken);
        var withFeedback = await scopedSource.CountAsync(
            x => db.OpportunityFeedback.Any(feedback =>
                feedback.BusinessUnitId == businessUnitId &&
                feedback.OpportunityRecommendationId == x.Id),
            cancellationToken);
        // ECV remains currency-denominated. Do not compare unlike currencies in the tenant-wide queue.
        var rows = await source
            .OrderByDescending(x => x.PriorityScore)
            .ThenBy(x => x.EvidenceCutoffAtUtc)
            .ThenBy(x => x.Id)
            .Skip((query.PageNumber - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        var items = await ToItemsAsync(
            businessUnitId,
            rows,
            (query.PageNumber - 1) * query.PageSize,
            cancellationToken);

        return new OpportunityPriorityPage(
            items,
            total,
            query.PageNumber,
            query.PageSize,
            DateTime.UtcNow,
            scope.TenantWide ? "tenant" : "assigned_to_me",
            PolicyVersion,
            OpportunityPriorityMode.Shadow,
            new OpportunityPriorityCohort(
                currentRecommendations,
                eligible,
                insufficientEvidence,
                withObservedOutcome,
                withFeedback,
                null,
                "Not measured: shadow action recommendations are not calibrated win predictions."));
    }

    public async Task<OpportunityPriorityItem> GetForCommercialCaseAsync(
        long businessUnitId,
        long commercialCaseId,
        OpportunityPriorityAccessScope scope,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        ValidateScope(scope);
        if (commercialCaseId <= 0)
            throw new ArgumentOutOfRangeException(nameof(commercialCaseId));

        var source = db.OpportunityRecommendations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CommercialCaseId == commercialCaseId);
        if (!scope.TenantWide)
            source = source.Where(x => x.Lead.AssignTo == scope.OwnerUserId!.Value);
        var row = await source.OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new OpportunityPriorityNotFoundException(
                "No opportunity priority is available for this commercial case in the permitted scope.");
        return (await ToItemsAsync(businessUnitId, [row], 0, cancellationToken))[0];
    }

    public Task<ReconcileOpportunityPrioritiesResult> ReconcileAsync(
        long businessUnitId,
        ReconcileOpportunityPrioritiesCommand command,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        ValidateReconcile(command);
        var normalized = command with
        {
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            ActorId = command.ActorId.Trim()
        };
        var requestHash = Hash(new
        {
            operation = "reconcile",
            businessUnitId,
            normalized.ActorId,
            normalized.AfterCommercialCaseId,
            normalized.BatchSize,
            policyVersion = PolicyVersion,
            featureSchemaVersion = FeatureSchemaVersion
        });
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await AcquireLockAsync($"opportunity-priorities:{businessUnitId}", cancellationToken);

            var receipt = await db.OpportunityOperations.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId &&
                    x.IdempotencyKey == normalized.IdempotencyKey,
                    cancellationToken);
            if (receipt is not null)
            {
                EnsureReplay(receipt, "Reconcile", null, requestHash);
                var replay = Deserialize<ReconcileOpportunityPrioritiesResult>(receipt.ResultJson);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var reconciledAtUtc = DateTime.UtcNow;
            var leadBatch = await db.Leads.AsNoTracking()
                .Where(x =>
                    x.BusinessUnitId == businessUnitId &&
                    x.CommercialCaseId > (normalized.AfterCommercialCaseId ?? 0))
                .OrderBy(x => x.CommercialCaseId)
                .Select(x => new LeadIdentity(
                    x.Id,
                    x.CommercialCaseId,
                    x.CommercialCaseReference,
                    x.CustomerId,
                    x.AssignTo,
                    x.LifecycleVersion,
                    x.CurrentRevisionId,
                    x.Aiconfidence,
                    x.BidClosingDate,
                    x.ModifiedDate ?? x.CreatedDate,
                    db.Orders.Any(order =>
                        order.BusinessUnitId == businessUnitId &&
                        order.CommercialCaseId == x.CommercialCaseId) ||
                    db.Quotes.Any(quote =>
                        quote.BusinessUnitId == businessUnitId &&
                        quote.CommercialCaseId == x.CommercialCaseId &&
                        quote.Status != null &&
                        (quote.Status.SetupCode == "ACCEPTED" ||
                         quote.Status.SetupCode == "REJECTED" ||
                         quote.Status.SetupCode == "EXPIRED" ||
                         quote.Status.SetupCode == "ORDERED")),
                    db.Quotes.Any(quote =>
                        quote.BusinessUnitId == businessUnitId &&
                        quote.CommercialCaseId == x.CommercialCaseId)
                        ? "CustomerQuote"
                        : db.Rfqs.Any(rfq =>
                            rfq.BusinessUnitId == businessUnitId &&
                            rfq.CommercialCaseId == x.CommercialCaseId)
                            ? "RFQ"
                            : "Lead"))
                .Take(normalized.BatchSize + 1)
                .ToListAsync(cancellationToken);
            var hasMore = leadBatch.Count > normalized.BatchSize;
            var leads = leadBatch.Take(normalized.BatchSize).ToArray();

            var created = 0;
            var replayed = 0;
            var outcomesRecorded = 0;
            var terminalCasesSkipped = 0;
            foreach (var lead in leads)
            {
                await AcquireLockAsync(
                    $"opportunity-priorities:{businessUnitId}:{lead.CommercialCaseId}",
                    cancellationToken);
                var existing = await db.OpportunityRecommendations
                    .Where(x =>
                        x.BusinessUnitId == businessUnitId &&
                        x.CommercialCaseId == lead.CommercialCaseId)
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lead.HasTerminalOutcome)
                {
                    terminalCasesSkipped++;
                    if (existing is not null)
                    {
                        outcomesRecorded += await ObserveLaterOutcomesAsync(
                            businessUnitId,
                            existing,
                            normalized,
                            requestHash,
                            reconciledAtUtc,
                            cancellationToken);
                    }
                    continue;
                }

                var brief = await leadDecisions.GetBriefAsync(lead.LeadId, businessUnitId, cancellationToken);
                var fulfilment = await GetFulfilmentEvidenceAsync(
                    businessUnitId,
                    lead.LeadId,
                    lead.CurrentRevisionId,
                    Math.Max(0, brief.Coverage.TotalItems),
                    lead.EvidenceAsOfUtc,
                    reconciledAtUtc,
                    cancellationToken);
                var candidate = BuildCandidate(lead, brief, fulfilment, reconciledAtUtc);

                OpportunityRecommendation recommendation;
                if (existing is not null &&
                    string.Equals(existing.PolicyVersion, PolicyVersion, StringComparison.Ordinal) &&
                    string.Equals(existing.FeatureSchemaVersion, FeatureSchemaVersion, StringComparison.Ordinal) &&
                    HasCurrentComponentSet(existing.ComponentsJson) &&
                    FixedEquals(existing.EvidenceHash, candidate.EvidenceHash))
                {
                    recommendation = existing;
                    replayed++;
                }
                else
                {
                    recommendation = candidate.ToEntity(businessUnitId, lead, existing?.Id, reconciledAtUtc);
                    db.OpportunityRecommendations.Add(recommendation);
                    await db.SaveChangesAsync(cancellationToken);
                    AppendEvent(
                        recommendation,
                        "OpportunityRecommendation.Generated",
                        "Recommendation",
                        recommendation.Id,
                        normalized.ActorId,
                        reconciledAtUtc,
                        normalized.CorrelationId,
                        DerivedKey(normalized.IdempotencyKey, $"recommendation:{recommendation.Id}"),
                        requestHash,
                        new
                        {
                            recommendation.Id,
                            recommendation.CommercialCaseId,
                            recommendation.NexoraSerial,
                            recommendation.LeadId,
                            recommendation.PriorityScore,
                            recommendation.PriorityBand,
                            recommendation.RecommendedActionCode,
                            recommendation.PolicyVersion,
                            recommendation.FeatureSchemaVersion,
                            recommendation.EvidenceHash,
                            recommendation.Mode,
                            recommendation.SupersedesRecommendationId
                        });
                    created++;
                }

                outcomesRecorded += await ObserveLaterOutcomesAsync(
                    businessUnitId,
                    recommendation,
                    normalized,
                    requestHash,
                    reconciledAtUtc,
                    cancellationToken);
            }

            var result = new ReconcileOpportunityPrioritiesResult(
                leads.Length,
                created,
                replayed,
                outcomesRecorded,
                terminalCasesSkipped,
                hasMore,
                hasMore && leads.Length > 0 ? leads[^1].CommercialCaseId : null,
                reconciledAtUtc,
                PolicyVersion,
                OpportunityPriorityMode.Shadow);
            db.OpportunityOperations.Add(new OpportunityOperation
            {
                BusinessUnitId = businessUnitId,
                OperationType = "Reconcile",
                IdempotencyKey = normalized.IdempotencyKey,
                RequestHash = requestHash,
                CorrelationId = normalized.CorrelationId,
                ActorId = normalized.ActorId,
                ResultJson = JsonSerializer.Serialize(result, JsonOptions),
                OccurredAtUtc = reconciledAtUtc
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException)
            {
                throw new OpportunityPriorityConflictException(
                    "Opportunity priority reconciliation conflicted with another request.");
            }
        });
    }

    public Task<OpportunityFeedbackItem> RecordFeedbackAsync(
        long businessUnitId,
        long recommendationId,
        RecordOpportunityFeedbackCommand command,
        OpportunityPriorityAccessScope scope,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        ValidateScope(scope);
        ValidateFeedback(recommendationId, command);
        var normalized = command with
        {
            Decision = NormalizeDecision(command.Decision),
            ReplacementActionCode = string.IsNullOrWhiteSpace(command.ReplacementActionCode)
                ? null
                : command.ReplacementActionCode.Trim().ToUpperInvariant(),
            Reason = command.Reason.Trim(),
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            ActorId = command.ActorId.Trim()
        };
        var requestHash = Hash(new
        {
            operation = "feedback",
            businessUnitId,
            recommendationId,
            normalized.ExpectedRecommendationId,
            normalized.Decision,
            normalized.ReplacementActionCode,
            normalized.Reason,
            normalized.SupersedesFeedbackId,
            normalized.ActorId
        });
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            var recommendationQuery = db.OpportunityRecommendations
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == recommendationId);
            if (!scope.TenantWide)
                recommendationQuery = recommendationQuery.Where(x => x.Lead.AssignTo == scope.OwnerUserId!.Value);
            var recommendation = await recommendationQuery.SingleOrDefaultAsync(cancellationToken)
                ?? throw new OpportunityPriorityNotFoundException(
                    "Opportunity recommendation was not found in the permitted scope.");
            await AcquireLockAsync(
                $"opportunity-priorities:{businessUnitId}:{recommendation.CommercialCaseId}",
                cancellationToken);

            var receipt = await db.OpportunityOperations.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId &&
                    x.IdempotencyKey == normalized.IdempotencyKey,
                    cancellationToken);
            if (receipt is not null)
            {
                EnsureReplay(receipt, "Feedback", recommendationId, requestHash);
                var replay = Deserialize<OpportunityFeedbackItem>(receipt.ResultJson);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var latestRecommendationId = await db.OpportunityRecommendations.AsNoTracking()
                .Where(x =>
                    x.BusinessUnitId == businessUnitId &&
                    x.CommercialCaseId == recommendation.CommercialCaseId)
                .MaxAsync(x => x.Id, cancellationToken);
            if (latestRecommendationId != normalized.ExpectedRecommendationId)
                throw new OpportunityPriorityConflictException(
                    "The opportunity recommendation is stale. Refresh and retry.");
            if (normalized.Decision == OpportunityFeedbackDecision.Reverted && !normalized.IsManager)
                throw new UnauthorizedAccessException("Only a manager can revert opportunity feedback.");

            OpportunityFeedback? superseded = null;
            if (normalized.SupersedesFeedbackId.HasValue)
            {
                superseded = await db.OpportunityFeedback.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.BusinessUnitId == businessUnitId &&
                        x.Id == normalized.SupersedesFeedbackId.Value &&
                        x.OpportunityRecommendationId == recommendationId,
                        cancellationToken)
                    ?? throw new OpportunityPriorityConflictException(
                        "The feedback being superseded is not part of this recommendation.");
                var latestFeedbackId = await db.OpportunityFeedback.AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.OpportunityRecommendationId == recommendationId)
                    .MaxAsync(x => (long?)x.Id, cancellationToken);
                if (latestFeedbackId != superseded.Id)
                    throw new OpportunityPriorityConflictException(
                        "The feedback sequence changed. Refresh and retry.");
            }
            if (normalized.Decision == OpportunityFeedbackDecision.Reverted && superseded is null)
                throw new ArgumentException("Reverted feedback must identify the feedback it supersedes.");

            var occurredAtUtc = DateTime.UtcNow;
            var feedback = new OpportunityFeedback
            {
                BusinessUnitId = businessUnitId,
                OpportunityRecommendationId = recommendationId,
                Decision = normalized.Decision,
                ReplacementActionCode = normalized.ReplacementActionCode,
                Reason = normalized.Reason,
                ActorId = normalized.ActorId,
                OccurredAtUtc = occurredAtUtc,
                IdempotencyKey = normalized.IdempotencyKey,
                CorrelationId = normalized.CorrelationId,
                SupersedesFeedbackId = normalized.SupersedesFeedbackId
            };
            db.OpportunityFeedback.Add(feedback);
            await db.SaveChangesAsync(cancellationToken);

            var result = ToFeedbackItem(feedback);
            AppendEvent(
                recommendation,
                "OpportunityRecommendation.FeedbackRecorded",
                "Feedback",
                feedback.Id,
                normalized.ActorId,
                occurredAtUtc,
                normalized.CorrelationId,
                normalized.IdempotencyKey,
                requestHash,
                new
                {
                    feedback.Id,
                    feedback.OpportunityRecommendationId,
                    feedback.Decision,
                    feedback.ReplacementActionCode,
                    feedback.Reason,
                    feedback.ActorId,
                    feedback.SupersedesFeedbackId
                });
            db.OpportunityOperations.Add(new OpportunityOperation
            {
                BusinessUnitId = businessUnitId,
                OperationType = "Feedback",
                CommercialCaseId = recommendation.CommercialCaseId,
                OpportunityRecommendationId = recommendationId,
                IdempotencyKey = normalized.IdempotencyKey,
                RequestHash = requestHash,
                CorrelationId = normalized.CorrelationId,
                ActorId = normalized.ActorId,
                ResultJson = JsonSerializer.Serialize(result, JsonOptions),
                OccurredAtUtc = occurredAtUtc
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException)
            {
                throw new OpportunityPriorityConflictException(
                    "Opportunity feedback conflicted with another request.");
            }
        });
    }

    private async Task<int> ObserveLaterOutcomesAsync(
        long businessUnitId,
        OpportunityRecommendation recommendation,
        ReconcileOpportunityPrioritiesCommand command,
        string requestHash,
        DateTime reconciledAtUtc,
        CancellationToken cancellationToken)
    {
        var candidates = new List<OutcomeCandidate>();
        var orders = await db.Orders.AsNoTracking()
            .Where(x =>
                x.BusinessUnitId == businessUnitId &&
                x.CommercialCaseId == recommendation.CommercialCaseId &&
                x.CreatedOn > recommendation.GeneratedAtUtc)
            .Select(x => new { x.Id, x.CreatedOn })
            .ToListAsync(cancellationToken);
        candidates.AddRange(orders.Select(x => new OutcomeCandidate(
            OpportunityOutcomeCode.OrderCreated,
            x.CreatedOn,
            "Order",
            x.Id,
            1,
            JsonSerializer.Serialize(new
            {
                authoritative = true,
                sourceType = "Order",
                sourceId = x.Id,
                x.CreatedOn
            }, JsonOptions))));

        var quotes = await db.Quotes.AsNoTracking()
            .Where(x =>
                x.BusinessUnitId == businessUnitId &&
                x.CommercialCaseId == recommendation.CommercialCaseId &&
                x.OutcomeOn.HasValue &&
                x.OutcomeOn.Value > recommendation.GeneratedAtUtc)
            .Select(x => new
            {
                x.Id,
                ObservedAtUtc = x.OutcomeOn!.Value,
                x.LifecycleVersion,
                StatusCode = x.Status == null ? null : x.Status.SetupCode,
                StatusValue = x.Status == null ? null : x.Status.SetupValue,
                x.OutcomeReasonId
            })
            .ToListAsync(cancellationToken);
        foreach (var quote in quotes)
        {
            var outcomeCode = QuoteOutcomeCode(quote.StatusCode, quote.StatusValue);
            if (outcomeCode is null) continue;
            candidates.Add(new OutcomeCandidate(
                outcomeCode,
                quote.ObservedAtUtc,
                "Quote",
                quote.Id,
                Math.Max(1, quote.LifecycleVersion),
                JsonSerializer.Serialize(new
                {
                    authoritative = true,
                    sourceType = "Quote",
                    sourceId = quote.Id,
                    sourceVersion = Math.Max(1, quote.LifecycleVersion),
                    quote.ObservedAtUtc,
                    quote.StatusCode,
                    quote.StatusValue,
                    quote.OutcomeReasonId
                }, JsonOptions)));
        }

        var added = 0;
        foreach (var candidate in candidates.OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.SourceType).ThenBy(x => x.SourceId))
        {
            var exists = await db.OpportunityOutcomes.AsNoTracking().AnyAsync(x =>
                x.BusinessUnitId == businessUnitId &&
                x.OpportunityRecommendationId == recommendation.Id &&
                x.SourceType == candidate.SourceType &&
                x.SourceId == candidate.SourceId &&
                x.SourceVersion == candidate.SourceVersion &&
                x.OutcomeCode == candidate.OutcomeCode,
                cancellationToken);
            if (exists) continue;

            var outcome = new OpportunityOutcome
            {
                BusinessUnitId = businessUnitId,
                OpportunityRecommendationId = recommendation.Id,
                OutcomeCode = candidate.OutcomeCode,
                ObservedAtUtc = candidate.ObservedAtUtc,
                SourceType = candidate.SourceType,
                SourceId = candidate.SourceId,
                SourceVersion = candidate.SourceVersion,
                EvidenceJson = candidate.EvidenceJson,
                EvidenceHash = HashJson(candidate.EvidenceJson),
                CorrelationId = command.CorrelationId
            };
            db.OpportunityOutcomes.Add(outcome);
            await db.SaveChangesAsync(cancellationToken);
            AppendEvent(
                recommendation,
                "OpportunityRecommendation.OutcomeObserved",
                "Outcome",
                outcome.Id,
                command.ActorId,
                reconciledAtUtc,
                command.CorrelationId,
                DerivedKey(command.IdempotencyKey, $"outcome:{outcome.Id}"),
                requestHash,
                new
                {
                    outcome.Id,
                    outcome.OpportunityRecommendationId,
                    outcome.OutcomeCode,
                    outcome.ObservedAtUtc,
                    outcome.SourceType,
                    outcome.SourceId,
                    outcome.SourceVersion,
                    outcome.EvidenceHash
                });
            added++;
        }
        return added;
    }

    private async Task<IReadOnlyList<OpportunityPriorityItem>> ToItemsAsync(
        long businessUnitId,
        IReadOnlyList<OpportunityRecommendation> rows,
        int rankOffset,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var recommendationIds = rows.Select(x => x.Id).ToArray();
        var leadIds = rows.Select(x => x.LeadId).Distinct().ToArray();
        var leads = await db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && leadIds.Contains(x.Id))
            .Select(x => new { x.Id, x.AssignTo })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var ownerIds = leads.Values.Where(x => x.AssignTo.HasValue).Select(x => x.AssignTo!.Value).Distinct().ToArray();
        var ownerNames = await db.Users.AsNoTracking()
            .Where(x => x.Buid == businessUnitId && ownerIds.Contains(x.Id))
            .Select(x => new { x.Id, Name = (x.FirstName + " " + x.LastName).Trim() })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var outcomes = (await db.OpportunityOutcomes.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && recommendationIds.Contains(x.OpportunityRecommendationId))
                .OrderBy(x => x.ObservedAtUtc)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.OpportunityRecommendationId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<OpportunityOutcomeItem>)x.Select(ToOutcomeItem).ToArray());
        var feedback = (await db.OpportunityFeedback.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && recommendationIds.Contains(x.OpportunityRecommendationId))
                .OrderByDescending(x => x.Id)
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.OpportunityRecommendationId)
            .ToDictionary(x => x.Key, x => ToFeedbackItem(x.First()));

        var items = new List<OpportunityPriorityItem>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var reasons = Deserialize<string[]>(row.RationaleJson);
            leads.TryGetValue(row.LeadId, out var lead);
            var ownerId = lead?.AssignTo;
            var components = Deserialize<OpportunityPriorityComponentSet>(row.ComponentsJson);
            items.Add(new OpportunityPriorityItem(
                row.Id,
                row.CommercialCaseId,
                row.NexoraSerial,
                row.LeadId,
                ownerId,
                ownerId.HasValue && ownerNames.TryGetValue(ownerId.Value, out var ownerName) ? ownerName : null,
                rankOffset + index + 1,
                row.PriorityScore,
                row.PriorityBand,
                row.Confidence,
                row.Completeness,
                row.SampleSize,
                string.Equals(components.Status, "insufficient_evidence", StringComparison.Ordinal),
                row.RecommendedActionCode,
                row.RecommendedActionLabel,
                row.ExpectedCommercialValue,
                row.ExpectedCommercialValueCurrency,
                components.Status,
                components.Signals,
                components.ResponseDeadline,
                components.CurrentBlocker,
                ActionCatalog,
                reasons,
                row.EvidenceSnapshotJson,
                row.EvidenceCutoffAtUtc,
                row.GeneratedAtUtc,
                row.PolicyVersion,
                row.FeatureSchemaVersion,
                row.Mode,
                true,
                outcomes.GetValueOrDefault(row.Id, []),
                feedback.GetValueOrDefault(row.Id)));
        }
        return items;
    }

    private static Candidate BuildCandidate(
        LeadIdentity lead,
        LeadDecisionBrief brief,
        FulfilmentEvidence fulfilment,
        DateTime cutoffAtUtc)
    {
        var exactItems = brief.Coverage.Items
            .Where(x => x.Matched &&
                        (string.Equals(x.MatchType, "code", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(x.MatchType, "mpn", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.LeadItemId)
            .Select(x => new ExactMatch(x.LeadItemId, x.ProductId, x.MatchType!.ToLowerInvariant()))
            .ToArray();
        var totalItems = Math.Max(0, brief.Coverage.TotalItems);
        var exactCoverage = totalItems == 0 ? 0m : decimal.Round(exactItems.Length * 100m / totalItems, 2);
        var hasCanonicalCustomer = brief.Customer.IsDecisionGradeIdentity && brief.Customer.CustomerId.HasValue;
        var missingSignals = new List<string>();
        if (totalItems == 0) missingSignals.Add("No lead lines are available for scoring.");
        if (exactItems.Length < totalItems) missingSignals.Add("Some lines lack an exact code or manufacturer-part match.");
        if (!hasCanonicalCustomer) missingSignals.Add("Canonical customer identity is unresolved or invalid.");
        if (!lead.ExtractionConfidence.HasValue) missingSignals.Add("Extraction confidence is unavailable.");
        if (!lead.BidClosingDate.HasValue || lead.BidClosingDate.Value.Year < 2000)
            missingSignals.Add("A usable bid deadline is unavailable.");

        var score = (int)Math.Round(exactCoverage * 0.55m, MidpointRounding.AwayFromZero);
        if (hasCanonicalCustomer) score += 15;
        if (lead.ExtractionConfidence is >= 0.8m) score += 10;
        var deadline = lead.BidClosingDate is { Year: >= 2000 } ? lead.BidClosingDate : null;
        var deadlineBand = deadline switch
        {
            { } value when value < cutoffAtUtc => "overdue",
            { } value when value <= cutoffAtUtc.AddDays(3) => "critical",
            { } value when value <= cutoffAtUtc.AddDays(7) => "soon",
            not null => "comfortable",
            _ => "unknown"
        };
        if (deadline.HasValue)
        {
            if (deadline.Value < cutoffAtUtc) score -= 25;
            else if (deadline.Value <= cutoffAtUtc.AddDays(3)) score += 20;
            else if (deadline.Value <= cutoffAtUtc.AddDays(7)) score += 15;
            else score += 8;
        }
        score = Math.Clamp(score, 0, 100);

        var customerQuality = brief.Customer.IsDecisionGradeIdentity
            ? Math.Min(1m, 0.5m + Math.Min(5, brief.Customer.Orders) * 0.1m)
            : (decimal?)null;
        var winLikelihood = brief.Customer.IsDecisionGradeIdentity && brief.Customer.Quotes >= 3
            ? Math.Min(1m, Math.Max(0m, (decimal)Math.Min(brief.Customer.Orders, brief.Customer.Quotes) / brief.Customer.Quotes))
            : (decimal?)null;
        var normalizedCurrency = brief.Currency?.Trim().ToUpperInvariant();
        var currencySafe = IsIsoCurrencyCode(normalizedCurrency);
        var margin = brief.IsMarginComplete && brief.MarginPotentialPct is >= 0m and <= 100m
            ? brief.MarginPotentialPct
            : null;
        var opportunityValue = brief.EstimatedValue is > 0m &&
                               string.Equals(brief.ValueConfidence, "high", StringComparison.OrdinalIgnoreCase) &&
                               currencySafe
            ? brief.EstimatedValue
            : null;
        var expectedGrossProfit = opportunityValue.HasValue && margin.HasValue
            ? decimal.Round(opportunityValue.Value * margin.Value / 100m, 4)
            : (decimal?)null;
        var effort = Math.Max(1m, totalItems + fulfilment.ShortageLines * 2m + fulfilment.UnresolvedLines * 3m);
        var expectedCommercialValue = expectedGrossProfit.HasValue && winLikelihood.HasValue &&
                                      customerQuality.HasValue && fulfilment.Value.HasValue
            ? decimal.Round(
                winLikelihood.Value * expectedGrossProfit.Value * customerQuality.Value *
                fulfilment.Value.Value / effort, 4)
            : (decimal?)null;
        var completenessSignals = 2 + (hasCanonicalCustomer ? 1 : 0) +
                                  (lead.ExtractionConfidence.HasValue ? 1 : 0) +
                                  (deadline.HasValue ? 1 : 0);
        var completeness = decimal.Round(completenessSignals / 5m, 4);
        var confidence = decimal.Round(Math.Min(1m, completeness * (0.5m + exactCoverage / 200m)), 4);
        var currentBlocker = deadline.HasValue && deadline.Value < cutoffAtUtc
            ? "Response deadline has passed."
                : !hasCanonicalCustomer
                ? "Canonical customer identity is unresolved or invalid."
                : fulfilment.UnresolvedLines > 0
                    ? "One or more lines require Product or possible-match review."
                    : fulfilment.Value is <= 0m
                        ? "No deadline-qualified internal availability is evidenced."
                    : fulfilment.Status != "measured"
                        ? fulfilment.Evidence
                        : !opportunityValue.HasValue
                            ? "Currency-safe opportunity value is unavailable."
                            : !margin.HasValue
                                ? "Verified cost and margin evidence is unavailable."
                                : !winLikelihood.HasValue
                                    ? "At least three canonical Customer Quote outcomes are required for win likelihood."
                                    : "No evidence blocker is active.";
        var urgencyValue = deadlineBand switch
        {
            "critical" => 1m,
            "soon" => 0.75m,
            "comfortable" => 0.4m,
            "overdue" => 0m,
            _ => (decimal?)null
        };
        var components = new OpportunityPriorityComponentSet(
            [
                Signal("opportunity_value", "Opportunity value", opportunityValue, brief.Currency, totalItems,
                    opportunityValue.HasValue ? 0.9m : 0m,
                    opportunityValue.HasValue ? "measured" : "unavailable", lead.EvidenceAsOfUtc,
                    "High-confidence priced Lead lines in one ISO currency only."),
                Signal("win_likelihood", "Evidenced win likelihood", winLikelihood, "ratio", brief.Customer.Quotes,
                    winLikelihood.HasValue ? Math.Min(1m, brief.Customer.Quotes / 20m) : 0m,
                    winLikelihood.HasValue ? "evidenced_proxy" : "insufficient_sample",
                    brief.Customer.EvidenceAsOfUtc ?? lead.EvidenceAsOfUtc,
                    "Linked Orders divided by sent Quotes in the same 24-month canonical Customer cohort; minimum sample is three Quotes."),
                Signal("margin", "Expected margin", margin, "percent", exactItems.Length,
                    margin.HasValue ? confidence : 0m, margin.HasValue ? "measured" : "unavailable", lead.EvidenceAsOfUtc,
                    "Available only when every line has price and cost in the same authoritative currency; Product master costs without currency are excluded."),
                Signal("urgency", "Urgency", urgencyValue, "ratio", deadline.HasValue ? 1 : 0,
                    deadline.HasValue ? 1m : 0m, deadline.HasValue ? "measured" : "unavailable", lead.EvidenceAsOfUtc,
                    deadlineBand),
                Signal("customer_quality", "Customer quality", customerQuality, "ratio", brief.Customer.Orders,
                    customerQuality.HasValue ? Math.Min(1m, (brief.Customer.Orders + 1) / 10m) : 0m,
                    customerQuality.HasValue ? "evidenced_proxy" : "unavailable",
                    brief.Customer.EvidenceAsOfUtc ?? lead.EvidenceAsOfUtc,
                    "Deterministic bounded proxy from canonical Customer identity and linked 24-month Order count."),
                Signal("fulfilment_confidence", "Fulfilment confidence", fulfilment.Value, "ratio",
                    fulfilment.SampleSize, fulfilment.Confidence, fulfilment.Status, fulfilment.EvidenceAsOfUtc,
                    fulfilment.Evidence),
                Signal("sourcing_effort", "Estimated sourcing effort", effort, "effort_points", totalItems,
                    fulfilment.Confidence, "estimated", fulfilment.EvidenceAsOfUtc,
                    "One point per line, two per shortage line, and three per unresolved line.")
            ],
            expectedCommercialValue,
            expectedCommercialValue.HasValue ? normalizedCurrency : null,
            expectedCommercialValue.HasValue ? "shadow_unvalidated" : "insufficient_evidence",
            deadline,
            currentBlocker);

        var (actionCode, actionLabel) = deadline.HasValue && deadline.Value < cutoffAtUtc
            ? ("REVIEW_EXPIRED_DEADLINE", "Review expired deadline")
            : totalItems == 0
                ? ("REVIEW_INQUIRY", "Review inquiry evidence")
            : exactItems.Length == 0
                ? ("SOURCE_UNKNOWN_PARTS", "Review unknown parts")
            : exactItems.Length < totalItems
                ? ("RESOLVE_UNMATCHED_PARTS", "Resolve unmatched parts")
            : fulfilment.UnresolvedLines > 0
                ? ("REQUEST_CLARIFICATION", "Request Product clarification")
                : !hasCanonicalCustomer
                    ? ("RESOLVE_CUSTOMER_IDENTITY", "Resolve Customer identity")
                : !fulfilment.Value.HasValue
                    ? ("REFRESH_FULFILMENT_EVIDENCE", "Refresh fulfilment evidence")
                : fulfilment.ShortageLines > 0
                    ? ("SEARCH_KNOWN_SUPPLIERS", "Search known Suppliers")
                : fulfilment.Value is < 0.999m
                        ? ("SEARCH_KNOWN_SUPPLIERS", "Source uncovered quantity")
                    : margin is null or < 15m
                        ? ("ESCALATE_APPROVAL", "Escalate margin review")
                    : string.Equals(lead.CurrentStage, "Lead", StringComparison.Ordinal)
                        ? ("OPEN_OPPORTUNITY", "Open opportunity")
                        : ("RESERVE_AVAILABLE_STOCK", "Review stock reservation");
        var reasons = new List<string>
        {
            $"{exactItems.Length} of {totalItems} lines have deterministic code or manufacturer-part matches.",
            hasCanonicalCustomer
                ? "Canonical tenant-qualified customer identity is present."
                : "Customer history is excluded because canonical identity is unresolved or invalid.",
            "Raw inventory quantity, ambiguous name matches, mixed-currency value and unverified margin are excluded."
        };
        reasons.AddRange(missingSignals);

        var evidenceJson = JsonSerializer.Serialize(new
        {
            source = "local_deterministic",
            leadId = lead.LeadId,
            leadVersion = lead.LeadVersion,
            commercialCaseId = lead.CommercialCaseId,
            lead.NexoraSerial,
            currentStage = lead.CurrentStage,
            suppliedCustomerId = lead.CustomerId,
            canonicalCustomerId = hasCanonicalCustomer ? brief.Customer.CustomerId : null,
            ownerUserId = lead.OwnerUserId,
            extractionConfidence = lead.ExtractionConfidence,
            bidClosingDate = deadline,
            deadlineBand,
            totalItems,
            exactMatchedItems = exactItems,
            exactCoveragePct = exactCoverage,
            excludedSignals = new[] { "ambiguous_name_match", "raw_qty_on_hand", "mixed_currency_value", "unverified_margin" },
            missingSignals,
            components
        }, JsonOptions);
        var hash = HashJson(evidenceJson);
        var band = score >= 75 ? "Critical" : score >= 50 ? "High" : score >= 25 ? "Medium" : "Low";
        var cohort = expectedCommercialValue.HasValue ? "eligible-shadow" : "insufficient-evidence";
        return new Candidate(
            hash,
            evidenceJson,
            score,
            band,
            confidence,
            completeness,
            exactItems.Length,
            actionCode,
            actionLabel,
            expectedCommercialValue,
            expectedCommercialValue.HasValue ? normalizedCurrency : null,
            JsonSerializer.Serialize(components, JsonOptions),
            JsonSerializer.Serialize(reasons, JsonOptions),
            cohort);
    }

    private async Task<FulfilmentEvidence> GetFulfilmentEvidenceAsync(
        long businessUnitId,
        long leadId,
        long? currentRevisionId,
        int expectedLineCount,
        DateTime fallbackEvidenceAsOfUtc,
        DateTime cutoffAtUtc,
        CancellationToken cancellationToken)
    {
        if (expectedLineCount <= 0)
            return new(null, 0, 0m, "unavailable", fallbackEvidenceAsOfUtc,
                "No Lead lines are available for fulfillment measurement.", 0, 0);
        if (!currentRevisionId.HasValue)
            return new(null, 0, 0m, "unavailable", fallbackEvidenceAsOfUtc,
                "The Lead does not have a current immutable revision for fulfillment evidence.", 0, 0);

        var rows = await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId &&
                        x.LeadId == leadId &&
                        x.LeadRevisionId == currentRevisionId.Value)
            .OrderByDescending(x => x.ResolvedOn)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
        var latest = rows.GroupBy(x => x.LeadLineId).Select(x => x.First()).ToArray();
        if (latest.Length == 0)
            return new(null, 0, 0m, "unavailable", fallbackEvidenceAsOfUtc,
                "Authoritative line-resolution evidence has not been created.", 0, 0);
        var asOf = latest.Min(x => x.InventoryAsOfUtc == default ? x.ResolvedOn : x.InventoryAsOfUtc);
        if (asOf < cutoffAtUtc.AddHours(-72))
            return new(null, latest.Length, 0m, "stale", asOf,
                "Inventory resolution is older than 72 hours.",
                latest.Count(x => x.Classification == CommercialResolutionClassification.KnownShortage),
                latest.Count(x => x.Classification is CommercialResolutionClassification.UnknownProduct
                    or CommercialResolutionClassification.PossibleMatchReview));
        var requested = latest.Sum(x => Math.Max(0m, x.RequestedQuantity));
        var covered = latest.Sum(x => Math.Min(
            Math.Max(0m, x.RequestedQuantity), Math.Max(0m, x.AvailableToPromise)));
        var unresolved = latest.Count(x => x.Classification is CommercialResolutionClassification.UnknownProduct
            or CommercialResolutionClassification.PossibleMatchReview);
        var shortages = latest.Count(x => x.Classification == CommercialResolutionClassification.KnownShortage);
        var coverageCompleteness = decimal.Round(
            Math.Min(1m, (decimal)latest.Length / expectedLineCount), 4);
        var allLinesResolved = latest.Length >= expectedLineCount;
        var value = requested > 0m && unresolved == 0 && allLinesResolved
            ? decimal.Round(covered / requested, 4)
            : (decimal?)null;
        var status = !allLinesResolved ? "incomplete" : value.HasValue ? "measured" : "unresolved";
        return new(value, latest.Length, coverageCompleteness, status, asOf,
            value.HasValue
                ? "Latest persisted ATP by current-revision Lead line; incoming supply is excluded without deadline-qualified timing and certainty."
                : !allLinesResolved
                    ? $"Commercial resolution exists for {latest.Length} of {expectedLineCount} Lead lines."
                    : "Product resolution is incomplete for one or more Lead lines.",
            shortages, unresolved);
    }

    private static OpportunityPrioritySignal Signal(
        string code, string label, decimal? value, string? unit, int sampleSize, decimal confidence,
        string status, DateTime evidenceAsOfUtc, string evidence)
    {
        var (sourceType, sourceReference) = code switch
        {
            "fulfilment_confidence" or "sourcing_effort" =>
                ("LeadLineCommercialResolution", "Current immutable Lead revision"),
            "win_likelihood" or "customer_quality" =>
                ("CustomerCommercialHistory", "Canonical Customer quote and order counters"),
            _ => ("LeadDecisionBrief", LeadDecisionPolicy.Version)
        };
        return new(code, label, value, unit, Math.Max(0, sampleSize), Math.Clamp(confidence, 0m, 1m),
            status, evidenceAsOfUtc, sourceType, sourceReference, evidence);
    }

    private static bool IsIsoCurrencyCode(string? value)
        => value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z');

    private void AppendEvent(
        OpportunityRecommendation recommendation,
        string eventType,
        string sourceType,
        long sourceId,
        string actorId,
        DateTime occurredAtUtc,
        string correlationId,
        string idempotencyKey,
        string requestHash,
        object payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var opportunityEvent = new OpportunityEvent
        {
            BusinessUnitId = recommendation.BusinessUnitId,
            OpportunityRecommendationId = recommendation.Id,
            EventType = eventType,
            SourceType = sourceType,
            SourceId = sourceId,
            ActorId = actorId,
            OccurredAtUtc = occurredAtUtc,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            PayloadJson = payloadJson
        };
        db.OpportunityEvents.Add(opportunityEvent);
        db.OpportunityOutbox.Add(new OpportunityOutbox
        {
            BusinessUnitId = recommendation.BusinessUnitId,
            Event = opportunityEvent,
            EventType = eventType,
            PayloadJson = payloadJson,
            OccurredAtUtc = occurredAtUtc,
            AvailableAtUtc = occurredAtUtc
        });
    }

    private async Task AcquireLockAsync(string lockKey, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                cancellationToken);
    }

    private long RequireTenant(long requestedBusinessUnitId)
    {
        if (!tenantContext.BusinessUnitId.HasValue || tenantContext.BusinessUnitId.Value <= 0)
            throw new UnauthorizedAccessException("An authenticated tenant context is required.");
        if (requestedBusinessUnitId <= 0 || requestedBusinessUnitId != tenantContext.BusinessUnitId.Value)
            throw new UnauthorizedAccessException("Requested business unit does not match the authenticated tenant.");
        return requestedBusinessUnitId;
    }

    private static void ValidateScope(OpportunityPriorityAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.TenantWide && scope.OwnerUserId.HasValue)
            throw new ArgumentException("Tenant-wide scope cannot specify an owner.", nameof(scope));
        if (!scope.TenantWide && (!scope.OwnerUserId.HasValue || scope.OwnerUserId <= 0))
            throw new ArgumentException("Individual scope requires an owner.", nameof(scope));
    }

    private static void ValidateReconcile(ReconcileOpportunityPrioritiesCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Required(command.CorrelationId, nameof(command.CorrelationId), 100);
        Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 160);
        Required(command.ActorId, nameof(command.ActorId), 160);
        if (command.AfterCommercialCaseId is < 0)
            throw new ArgumentOutOfRangeException(nameof(command.AfterCommercialCaseId));
        if (command.BatchSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(command.BatchSize));
    }

    private static void ValidateFeedback(long recommendationId, RecordOpportunityFeedbackCommand command)
    {
        if (recommendationId <= 0) throw new ArgumentOutOfRangeException(nameof(recommendationId));
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedRecommendationId != recommendationId)
            throw new OpportunityPriorityConflictException(
                "Expected recommendation must match the recommendation in the route.");
        var decision = NormalizeDecision(command.Decision);
        Required(command.Reason, nameof(command.Reason), 1000);
        Required(command.CorrelationId, nameof(command.CorrelationId), 100);
        Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 160);
        Required(command.ActorId, nameof(command.ActorId), 160);
        if (decision == OpportunityFeedbackDecision.Replaced)
        {
            Required(command.ReplacementActionCode, nameof(command.ReplacementActionCode), 80);
            if (!ActionCatalog.Any(option => string.Equals(
                    option.Code, command.ReplacementActionCode?.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Replacement action must be selected from the governed action catalog.",
                    nameof(command.ReplacementActionCode));
        }
        else if (!string.IsNullOrWhiteSpace(command.ReplacementActionCode))
            throw new ArgumentException("A replacement action is only valid for Replaced feedback.");
    }

    private static string NormalizeDecision(string decision)
    {
        var normalized = OpportunityFeedbackDecision.All.FirstOrDefault(x =>
            string.Equals(x, decision?.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized ?? throw new ArgumentException("Unsupported opportunity feedback decision.", nameof(decision));
    }

    private static void Required(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        if (value.Trim().Length > maximumLength)
            throw new ArgumentException($"{name} cannot exceed {maximumLength} characters.", name);
    }

    internal static string? QuoteOutcomeCode(string? statusCode, string? statusValue)
    {
        _ = statusValue;
        return statusCode?.Trim().ToUpperInvariant() switch
        {
            "ACCEPTED" or "ORDERED" => OpportunityOutcomeCode.QuoteWon,
            "REJECTED" => OpportunityOutcomeCode.QuoteLost,
            "EXPIRED" => OpportunityOutcomeCode.QuoteExpired,
            _ => null
        };
    }

    private static OpportunityOutcomeItem ToOutcomeItem(OpportunityOutcome outcome)
        => new(
            outcome.Id,
            outcome.OutcomeCode,
            outcome.ObservedAtUtc,
            outcome.SourceType,
            outcome.SourceId,
            outcome.SourceVersion,
            outcome.EvidenceJson);

    private static OpportunityFeedbackItem ToFeedbackItem(OpportunityFeedback feedback)
        => new(
            feedback.Id,
            feedback.Decision,
            feedback.ReplacementActionCode,
            feedback.Reason,
            feedback.ActorId,
            feedback.OccurredAtUtc,
            feedback.SupersedesFeedbackId);

    private static string DerivedKey(string baseKey, string discriminator)
        => $"{(baseKey.Length <= 95 ? baseKey : baseKey[..95])}:{Hash(new { baseKey, discriminator })}";

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions))))
            .ToLowerInvariant();

    private static string HashJson(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string persisted, string requested)
    {
        var left = Encoding.ASCII.GetBytes(persisted);
        var right = Encoding.ASCII.GetBytes(requested);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void EnsureReplay(
        OpportunityOperation receipt,
        string operationType,
        long? recommendationId,
        string requestHash)
    {
        if (!FixedEquals(receipt.RequestHash, requestHash) ||
            !string.Equals(receipt.OperationType, operationType, StringComparison.Ordinal) ||
            receipt.OpportunityRecommendationId != recommendationId)
            throw new OpportunityPriorityConflictException(
                "The idempotency key was already used for a different opportunity operation.");
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new JsonException("The persisted result was empty.");
        }
        catch (JsonException)
        {
            throw new OpportunityPriorityConflictException(
                "The persisted idempotency receipt could not be replayed safely.");
        }
    }

    private static bool HasCurrentComponentSet(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                string.Equals(status.GetString(), "legacy_reconcile_required", StringComparison.Ordinal))
                return false;
            if (!root.TryGetProperty("signals", out var signals) ||
                signals.ValueKind != JsonValueKind.Array || signals.GetArrayLength() != 7)
                return false;

            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var signal in signals.EnumerateArray())
            {
                if (signal.ValueKind != JsonValueKind.Object ||
                    !signal.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(code.GetString()))
                    return false;
                codes.Add(code.GetString()!);
            }
            return codes.Count == 7;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record LeadIdentity(
        long LeadId,
        long CommercialCaseId,
        string NexoraSerial,
        long? CustomerId,
        long? OwnerUserId,
        long LeadVersion,
        long? CurrentRevisionId,
        decimal? ExtractionConfidence,
        DateTime? BidClosingDate,
        DateTime EvidenceAsOfUtc,
        bool HasTerminalOutcome,
        string CurrentStage);

    private sealed record ExactMatch(long LeadItemId, long? ProductId, string MatchType);

    private sealed record OutcomeCandidate(
        string OutcomeCode,
        DateTime ObservedAtUtc,
        string SourceType,
        long SourceId,
        long SourceVersion,
        string EvidenceJson);

    private sealed record FulfilmentEvidence(
        decimal? Value,
        int SampleSize,
        decimal Confidence,
        string Status,
        DateTime EvidenceAsOfUtc,
        string Evidence,
        int ShortageLines,
        int UnresolvedLines);

    private sealed record Candidate(
        string EvidenceHash,
        string EvidenceJson,
        int PriorityScore,
        string PriorityBand,
        decimal Confidence,
        decimal Completeness,
        int SampleSize,
        string ActionCode,
        string ActionLabel,
        decimal? ExpectedCommercialValue,
        string? ExpectedCommercialValueCurrency,
        string ComponentsJson,
        string RationaleJson,
        string CohortKey)
    {
        public OpportunityRecommendation ToEntity(
            long businessUnitId,
            LeadIdentity lead,
            long? supersedesRecommendationId,
            DateTime generatedAtUtc)
            => new()
            {
                BusinessUnitId = businessUnitId,
                CommercialCaseId = lead.CommercialCaseId,
                NexoraSerial = lead.NexoraSerial,
                LeadId = lead.LeadId,
                LeadVersion = Math.Max(1, lead.LeadVersion),
                RecommendationKey = $"{lead.CommercialCaseId}:{PolicyVersion}:{EvidenceHash}",
                PolicyVersion = OpportunityPriorityApplicationService.PolicyVersion,
                FeatureSchemaVersion = OpportunityPriorityApplicationService.FeatureSchemaVersion,
                EvidenceCutoffAtUtc = generatedAtUtc,
                EvidenceSnapshotJson = EvidenceJson,
                EvidenceHash = EvidenceHash,
                PriorityScore = PriorityScore,
                PriorityBand = PriorityBand,
                Confidence = Confidence,
                Completeness = Completeness,
                SampleSize = SampleSize,
                RecommendedActionCode = ActionCode,
                RecommendedActionLabel = ActionLabel,
                ExpectedCommercialValue = ExpectedCommercialValue,
                ExpectedCommercialValueCurrency = ExpectedCommercialValueCurrency,
                ComponentsJson = ComponentsJson,
                RationaleJson = RationaleJson,
                CohortKey = CohortKey,
                Mode = OpportunityPriorityMode.Shadow,
                GeneratedAtUtc = generatedAtUtc,
                SupersedesRecommendationId = supersedesRecommendationId
            };
    }
}
