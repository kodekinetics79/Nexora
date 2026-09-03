using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public interface ILifecycleApplicationService
{
    Task<LifecycleStateView> GetLeadStateAsync(long businessUnitId, long leadId, CancellationToken ct);
    Task<LifecycleStateView> GetRfqStateAsync(long businessUnitId, long rfqId, CancellationToken ct);
    Task<LifecycleStateView> GetQuoteStateAsync(long businessUnitId, long quoteId, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionLeadAsync(long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionRfqAsync(long businessUnitId, long rfqId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionQuoteAsync(long businessUnitId, long quoteId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionLeadInCurrentTransactionAsync(long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
    Task<LifecycleTransitionResult> CompleteRfqPromotionInCurrentTransactionAsync(long businessUnitId, long leadId,
        long rfqId, long promotionId, long leadRevisionId, long participationDecisionId,
        LifecycleActor actor, LifecycleTransitionCommand command, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionQuoteInCurrentTransactionAsync(long businessUnitId, long quoteId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
    Task RecordLeadPromotedToRfqInCurrentTransactionAsync(long businessUnitId, long leadId, long rfqId, LifecycleActor actor, string correlationId, CancellationToken ct);
}

public sealed class LifecycleApplicationService : ILifecycleApplicationService
{
    private const string LeadAggregate = "Lead";
    private const string RfqAggregate = "Rfq";
    private const string QuoteAggregate = "Quote";
    private readonly ErpRfqAutomationContext _db;
    private readonly ILeadOutcomeReasons _leadOutcomeReasons;

    /// <param name="leadOutcomeReasons">
    /// The governed outcome-reason picklist shared with the quote outcome flow. Optional so the
    /// existing lightweight <c>new LifecycleApplicationService(db)</c> constructions keep working;
    /// the fallback reads the same SetupMaster rows, it just cannot seed them.
    /// </param>
    public LifecycleApplicationService(ErpRfqAutomationContext db, ILeadOutcomeReasons? leadOutcomeReasons = null)
    {
        _db = db;
        _leadOutcomeReasons = leadOutcomeReasons ?? new LeadOutcomeReasons(db);
    }

    public async Task<LifecycleStateView> GetLeadStateAsync(long businessUnitId, long leadId, CancellationToken ct)
    {
        var lead = await _db.Leads.AsNoTracking().Include(x => x.LeadStatus)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
            ?? throw new LifecycleNotFoundException("Lead was not found.");
        return await BuildStateAsync(LeadAggregate, lead.Id, lead.CommercialCaseId, lead.CommercialCaseReference,
            lead.LeadStatusId, lead.LeadStatus, lead.LifecycleVersion, businessUnitId, ct);
    }

    public async Task<LifecycleStateView> GetRfqStateAsync(long businessUnitId, long rfqId, CancellationToken ct)
    {
        var rfq = await _db.Rfqs.AsNoTracking().Include(x => x.Rfqstatus).Include(x => x.Lead)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == rfqId, ct)
            ?? throw new LifecycleNotFoundException("RFQ was not found.");
        EnsureLinked(rfq);
        return await BuildStateAsync(RfqAggregate, rfq.Id, rfq.Lead!.CommercialCaseId, rfq.Lead.CommercialCaseReference,
            rfq.RfqstatusId, rfq.Rfqstatus, rfq.LifecycleVersion, businessUnitId, ct);
    }

    public async Task<LifecycleStateView> GetQuoteStateAsync(long businessUnitId, long quoteId, CancellationToken ct)
    {
        var quote = await _db.Quotes.AsNoTracking().Include(x => x.Status)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == quoteId, ct)
            ?? throw new LifecycleNotFoundException("Quote was not found.");
        EnsureLinked(quote);
        return await BuildStateAsync(QuoteAggregate, quote.Id, quote.CommercialCaseId!.Value, quote.NexoraSerial!,
            quote.StatusId, quote.Status, quote.LifecycleVersion, businessUnitId, ct);
    }

    public Task<LifecycleTransitionResult> TransitionLeadAsync(
        long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
    {
        RejectReservedLeadPromotion(command);
        return ExecuteAsync(LeadAggregate, businessUnitId, leadId, actor, command, reopen, ct);
    }

    public Task<LifecycleTransitionResult> TransitionRfqAsync(
        long businessUnitId, long rfqId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
        => ExecuteAsync(RfqAggregate, businessUnitId, rfqId, actor, command, reopen, ct);

    public Task<LifecycleTransitionResult> TransitionQuoteAsync(
        long businessUnitId, long quoteId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
        => ExecuteAsync(QuoteAggregate, businessUnitId, quoteId, actor, command, reopen, ct);

    public async Task<LifecycleTransitionResult> TransitionLeadInCurrentTransactionAsync(
        long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
    {
        RejectReservedLeadPromotion(command);
        return await TransitionLeadInCurrentTransactionCoreAsync(
            businessUnitId, leadId, actor, command, reopen, ct);
    }

    public async Task<LifecycleTransitionResult> CompleteRfqPromotionInCurrentTransactionAsync(
        long businessUnitId, long leadId, long rfqId, long promotionId, long leadRevisionId,
        long participationDecisionId, LifecycleActor actor, LifecycleTransitionCommand command, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("An active database transaction is required.");
        if (LifecyclePolicy.Canonicalize(LeadAggregate, command.TargetStatusCode) != "CONVERTED_TO_RFQ")
            throw new LifecycleValidationException(
                "The RFQ Promotion lifecycle command can only set CONVERTED_TO_RFQ.");

        var lineageExists = await (from rfq in _db.Rfqs.AsNoTracking()
            join promotion in _db.Set<ERP_RFQ_Automation.CommercialCases.Promotion.RfqPromotion>().AsNoTracking()
                on new
                {
                    BusinessUnitId = rfq.BusinessUnitId,
                    PromotionId = rfq.PromotionId!.Value,
                    LeadId = rfq.LeadId!.Value,
                    LeadRevisionId = rfq.SourceLeadRevisionId!.Value,
                    ParticipationDecisionId = rfq.ParticipationDecisionId!.Value
                }
                equals new
                {
                    promotion.BusinessUnitId,
                    PromotionId = promotion.Id,
                    promotion.LeadId,
                    promotion.LeadRevisionId,
                    promotion.ParticipationDecisionId
                }
            where rfq.BusinessUnitId == businessUnitId && rfq.Id == rfqId && rfq.LeadId == leadId
                && rfq.PromotionId == promotionId && rfq.SourceLeadRevisionId == leadRevisionId
                && rfq.ParticipationDecisionId == participationDecisionId
            select rfq.Id).AnyAsync(ct);
        if (!lineageExists)
            throw new LifecycleValidationException(
                "The Lead can only be marked converted after RFQ Promotion persisted an exactly matching RFQ, revision, participation decision and promotion receipt.");

        var result = await TransitionLeadInCurrentTransactionCoreAsync(
            businessUnitId, leadId, actor, command, reopen: false, ct);
        await RecordLeadPromotedToRfqInCurrentTransactionAsync(
            businessUnitId, leadId, rfqId, actor, command.CorrelationId, ct);
        return result;
    }

    private async Task<LifecycleTransitionResult> TransitionLeadInCurrentTransactionCoreAsync(
        long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("An active database transaction is required.");
        ValidateInput(businessUnitId, leadId, actor, command);
        var requestHash = HashRequest(LeadAggregate, leadId, actor, command, reopen);
        return await ExecuteCoreAsync(LeadAggregate, businessUnitId, leadId, actor, command, reopen, requestHash, ct);
    }

    public async Task<LifecycleTransitionResult> TransitionQuoteInCurrentTransactionAsync(
        long businessUnitId, long quoteId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("An active database transaction is required.");
        ValidateInput(businessUnitId, quoteId, actor, command);
        var requestHash = HashRequest(QuoteAggregate, quoteId, actor, command, reopen);
        return await ExecuteCoreAsync(QuoteAggregate, businessUnitId, quoteId, actor, command, reopen, requestHash, ct);
    }

    /// <summary>
    /// Records the dedicated lead→RFQ promotion event alongside (never instead of) the generic
    /// CONVERTED_TO_RFQ status transition, in the caller's already-open transaction.
    ///
    /// <para>The generic <c>StatusTransitioned</c> event says only that a status changed; a reader
    /// that cares that an RFQ now exists for the lead had to parse reason strings to find out
    /// which RFQ. This event carries the facts of the promotion by name: the lead, the RFQ it
    /// became (<c>RequestReference = rfq-{id}</c>), the actor and the correlation id.</para>
    ///
    /// <para>It is written as its own <see cref="CommercialLifecycleEvent"/> row and therefore
    /// bumps the lead's LifecycleVersion again — the event stream is append-only and every
    /// appended event advances the aggregate version, which the unique
    /// (BusinessUnitId, AggregateType, AggregateId, AggregateVersion) index insists on.</para>
    ///
    /// <para>Idempotent per lead via the (BusinessUnitId, IdempotencyKey) unique index and the
    /// same read-then-return replay the transitions use: a lead is promoted at most once, so a
    /// replayed conversion finds the existing promotion event and writes nothing.</para>
    /// </summary>
    public async Task RecordLeadPromotedToRfqInCurrentTransactionAsync(
        long businessUnitId, long leadId, long rfqId, LifecycleActor actor, string correlationId, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("An active database transaction is required.");
        if (businessUnitId <= 0 || leadId <= 0 || rfqId <= 0)
            throw new LifecycleValidationException("Tenant, lead and RFQ identifiers are required.");
        Required(actor.ActorId, nameof(actor.ActorId), 255);
        Required(actor.ActorSource, nameof(actor.ActorSource), 50);
        Required(correlationId, nameof(correlationId), 100);

        var idempotencyKey = $"lead-promotion:{businessUnitId}:{leadId}";
        if (await FindReplayAsync(businessUnitId, idempotencyKey, ct) != null)
            return; // Promotion already recorded (retried conversion): nothing new happened.

        var lead = await _db.Leads.Include(x => x.LeadStatus)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
            ?? throw new LifecycleNotFoundException("Lead was not found.");
        if (!lead.LeadStatusId.HasValue)
            throw new LifecycleValidationException("A lead cannot be promoted before it has a lifecycle status.");
        var statusCode = LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue);

        var now = DateTime.UtcNow;
        lead.LifecycleVersion++;
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { businessUnitId, leadId, rfqId, eventType = "PromotedToRfq" })))).ToLowerInvariant();
        var lifecycleEvent = new CommercialLifecycleEvent
        {
            BusinessUnitId = businessUnitId,
            CommercialCaseId = lead.CommercialCaseId,
            CommercialCaseReference = lead.CommercialCaseReference,
            AggregateType = LeadAggregate,
            AggregateId = leadId,
            EventType = "PromotedToRfq",
            // Not a status change: the promotion happens AT the CONVERTED_TO_RFQ status the
            // preceding transition established. PreviousStatus stays NULL, deliberately —
            // the CK_lifecycle_events_StatusChanged constraint (PostgreSQL) insists that a
            // STATED previous status differs from the new one, and this event states none.
            PreviousStatusId = null,
            PreviousStatusCode = null,
            NewStatusId = lead.LeadStatusId.Value,
            NewStatusCode = statusCode,
            AggregateVersion = lead.LifecycleVersion,
            ActorId = actor.ActorId.Trim(),
            ActorSource = actor.ActorSource.Trim(),
            OccurredOn = now,
            PolicyVersion = LifecyclePolicy.Version,
            Source = "Api",
            CorrelationId = correlationId.Trim(),
            RequestReference = $"rfq-{rfqId}",
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash
        };
        _db.CommercialLifecycleEvents.Add(lifecycleEvent);
        await _db.SaveChangesAsync(ct);

        // No outbox row. lifecycle_outbox_messages never had a consumer — 71 rows sat pending in
        // production with AttemptCount 0 — so the producer was retired rather than a dispatcher
        // invented to mark them processed (docs/design/lifecycle-outbox.md). The
        // CommercialLifecycleEvent above IS the durable record of the promotion.
    }

    private async Task<LifecycleTransitionResult> ExecuteAsync(
        string aggregateType,
        long businessUnitId,
        long aggregateId,
        LifecycleActor actor,
        LifecycleTransitionCommand command,
        bool reopen,
        CancellationToken ct)
    {
        ValidateInput(businessUnitId, aggregateId, actor, command);
        var requestHash = HashRequest(aggregateType, aggregateId, actor, command, reopen);
        var strategy = _db.Database.CreateExecutionStrategy();

        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await ExecuteCoreAsync(
                    aggregateType, businessUnitId, aggregateId, actor, command, reopen, requestHash, ct);
                await transaction.CommitAsync(ct);
                return result;
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new LifecycleConflictException("The lifecycle state changed. Reload it and retry with the current version.");
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var replay = await FindReplayAsync(businessUnitId, command.IdempotencyKey, ct);
            if (replay != null)
                return VerifyReplay(replay, aggregateType, aggregateId, requestHash);
            throw;
        }
    }

    private async Task<LifecycleTransitionResult> ExecuteCoreAsync(
        string aggregateType, long businessUnitId, long aggregateId, LifecycleActor actor,
        LifecycleTransitionCommand command, bool reopen, string requestHash, CancellationToken ct)
    {
        var aggregate = await LoadAggregateAsync(aggregateType, businessUnitId, aggregateId, ct);
        var replay = await FindReplayAsync(businessUnitId, command.IdempotencyKey, ct);
        if (replay != null)
            return VerifyReplay(replay, aggregateType, aggregateId, requestHash);
        if (aggregate.Version != command.ExpectedVersion)
            throw new LifecycleConflictException("The lifecycle state changed. Reload it and retry with the current version.");

        var statuses = await LoadStatusesAsync(aggregateType, businessUnitId, ct);
        var target = ResolveTarget(statuses, aggregateType, command.TargetStatusCode);
        var currentCode = LifecyclePolicy.Canonicalize(aggregateType, aggregate.CurrentStatus?.SetupCode, aggregate.CurrentStatus?.SetupValue);
        var targetCode = LifecyclePolicy.Canonicalize(aggregateType, target.SetupCode, target.SetupValue);
        ValidateTransition(aggregateType, currentCode, targetCode, command.ReasonCode, reopen);
        if (aggregateType == LeadAggregate && targetCode == "QUALIFIED"
            && aggregate.RequiresCommercialReview && !aggregate.CommercialFactsVerified)
            throw new LifecycleValidationException(
                "AI-extracted commercial facts must be approved before the lead can be qualified.");
        if (aggregateType == LeadAggregate && targetCode == "QUALIFIED"
            && aggregate.HasUnresolvedCurrentQuantity)
            throw new LifecycleValidationException(
                "Every current Lead line must have a positive quantity before the lead can be qualified.");

        // A lead that ends before a quotation exists must say WHY, using the same governed
        // vocabulary a quote outcome uses. Resolved BEFORE anything is mutated, so an ungoverned
        // reason leaves the lead — and the audit trail — untouched.
        long? leadOutcomeReasonId = null;
        var recordsLeadLoss = !reopen && LifecyclePolicy.RecordsLeadLoss(aggregateType, targetCode);
        if (recordsLeadLoss)
        {
            var reasonCode = Clean(command.ReasonCode);
            leadOutcomeReasonId = await _leadOutcomeReasons.ResolveAsync(businessUnitId, reasonCode, ct)
                ?? throw new LifecycleValidationException(
                    $"'{reasonCode}' is not one of this business unit's governed outcome reasons. " +
                    "Choose a reason from the outcome-reason list.");
        }

        if (_db.Database.IsNpgsql())
        {
            var triggerReason = string.Join(": ", new[]
            {
                Clean(command.ReasonCode)?.ToUpperInvariant(),
                Clean(command.ReasonNotes)
            }.Where(value => value != null));
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('nexora.actor', {actor.ActorId.Trim()}, true),
                       set_config('nexora.actor_source', {actor.ActorSource.Trim()}, true),
                       set_config('nexora.reason', {triggerReason}, true),
                       set_config('nexora.lifecycle_command', 'true', true)
                """, ct);
        }

        aggregate.SetStatus(target.SetupId);
        aggregate.IncrementVersion();
        var now = DateTime.UtcNow;
        if (recordsLeadLoss)
            aggregate.RecordLeadOutcome(leadOutcomeReasonId, Truncate(Clean(command.ReasonNotes), 500), now);
        else if (reopen && aggregateType == LeadAggregate)
            // A reopened lead is no longer lost. Clearing the stamp keeps win/loss reporting honest;
            // the append-only lifecycle events still carry the full history of what happened.
            aggregate.RecordLeadOutcome(null, null, null);
        var lifecycleEvent = new CommercialLifecycleEvent
        {
            BusinessUnitId = businessUnitId,
            CommercialCaseId = aggregate.CommercialCaseId,
            CommercialCaseReference = aggregate.CommercialCaseReference,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = reopen ? "Reopened" : "StatusTransitioned",
            PreviousStatusId = aggregate.PreviousStatusId,
            PreviousStatusCode = currentCode,
            NewStatusId = target.SetupId,
            NewStatusCode = targetCode,
            AggregateVersion = aggregate.Version,
            ActorId = actor.ActorId.Trim(),
            ActorSource = actor.ActorSource.Trim(),
            OccurredOn = now,
            ReasonCode = Clean(command.ReasonCode)?.ToUpperInvariant(),
            ReasonNotes = Clean(command.ReasonNotes),
            PolicyVersion = LifecyclePolicy.Version,
            Source = command.Source.Trim(),
            CorrelationId = command.CorrelationId.Trim(),
            RequestReference = command.RequestReference.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            RequestHash = requestHash
        };
        _db.CommercialLifecycleEvents.Add(lifecycleEvent);
        await _db.SaveChangesAsync(ct);

        // No outbox row — see RecordLeadPromotedToRfqInCurrentTransactionAsync and
        // docs/design/lifecycle-outbox.md. The event row above is the record; nothing consumed
        // the second copy.
        return ToResult(lifecycleEvent, false);
    }

    private async Task<LifecycleAggregate> LoadAggregateAsync(string aggregateType, long businessUnitId, long aggregateId, CancellationToken ct)
    {
        if (aggregateType == LeadAggregate)
        {
            var lead = await _db.Leads.Include(x => x.LeadStatus).Include(x => x.LeadItems)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == aggregateId, ct)
                ?? throw new LifecycleNotFoundException("Lead was not found.");
            return LifecycleAggregate.ForLead(lead);
        }

        if (aggregateType == RfqAggregate)
        {
            var rfq = await _db.Rfqs.Include(x => x.Rfqstatus).Include(x => x.Lead)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == aggregateId, ct)
                ?? throw new LifecycleNotFoundException("RFQ was not found.");
            EnsureLinked(rfq);
            return LifecycleAggregate.ForRfq(rfq);
        }

        if (aggregateType == QuoteAggregate)
        {
            var quote = await _db.Quotes.Include(x => x.Status)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == aggregateId, ct)
                ?? throw new LifecycleNotFoundException("Quote was not found.");
            EnsureLinked(quote);
            return LifecycleAggregate.ForQuote(quote);
        }

        throw new LifecycleValidationException("Unsupported lifecycle aggregate type.");
    }

    private async Task<LifecycleStateView> BuildStateAsync(
        string aggregateType, long aggregateId, long caseId, string reference, long? currentStatusId,
        SetupMaster? currentStatus, int version, long businessUnitId, CancellationToken ct)
    {
        if (currentStatus != null && currentStatus.BusinessUnitId != businessUnitId)
            throw new LifecycleValidationException("The current lifecycle status does not belong to this tenant.");
        var currentCode = LifecyclePolicy.Canonicalize(aggregateType, currentStatus?.SetupCode, currentStatus?.SetupValue);
        var allowed = LifecyclePolicy.AllowedTargets(aggregateType, currentCode);
        var statuses = await LoadStatusesAsync(aggregateType, businessUnitId, ct);
        var options = statuses
            .Select(status => new
            {
                Status = status,
                Code = LifecyclePolicy.Canonicalize(aggregateType, status.SetupCode, status.SetupValue)
            })
            .Where(x => allowed.Contains(x.Code)
                && !(aggregateType == LeadAggregate && x.Code == "CONVERTED_TO_RFQ"))
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Status.SetupCode)).First())
            .OrderBy(x => x.Code)
            .Select(x => new LifecycleTransitionOption(x.Status.SetupId, x.Code, x.Status.SetupValue,
                LifecyclePolicy.RequiresReason(aggregateType, x.Code, false)))
            .ToArray();
        return new LifecycleStateView(aggregateType, aggregateId, caseId, reference, currentStatusId,
            currentCode, version, LifecyclePolicy.IsTerminal(aggregateType, currentCode), options);
    }

    private async Task<List<SetupMaster>> LoadStatusesAsync(string aggregateType, long businessUnitId, CancellationToken ct)
    {
        var expectedType = aggregateType switch
        {
            LeadAggregate => "leadstatus",
            RfqAggregate => "rfqstatus",
            QuoteAggregate => "quotestatus",
            _ => throw new LifecycleValidationException("Unsupported lifecycle aggregate type.")
        };
        return await _db.SetupMasters.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive != false)
            .Where(x => x.SetupType.ToLower().Replace(" ", "") == expectedType)
            .ToListAsync(ct);
    }

    private static SetupMaster ResolveTarget(IEnumerable<SetupMaster> statuses, string aggregateType, string requestedCode)
    {
        var requested = LifecyclePolicy.Canonicalize(aggregateType, requestedCode);
        var matches = statuses.Where(status =>
            LifecyclePolicy.Canonicalize(aggregateType, status.SetupCode, status.SetupValue) == requested).ToArray();
        if (matches.Length == 0)
            throw new LifecycleValidationException("The requested lifecycle status is not configured and active for this tenant.");
        return matches.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.SetupCode)).ThenBy(x => x.SetupId).First();
    }

    private static void ValidateTransition(string aggregateType, string current, string target, string? reason, bool reopen)
    {
        if (current == target)
            throw new LifecycleValidationException("The aggregate is already in the requested lifecycle state.");
        if (reopen)
        {
            if (!LifecyclePolicy.IsReopenable(aggregateType, current))
                throw new LifecycleValidationException("This terminal lifecycle state cannot use the ordinary reopen command.");
            if (target != LifecyclePolicy.ReopenTarget(aggregateType))
                throw new LifecycleValidationException($"A reopened {aggregateType} must return to {LifecyclePolicy.ReopenTarget(aggregateType)}.");
        }
        else
        {
            if (LifecyclePolicy.IsTerminal(aggregateType, current))
                throw new LifecycleValidationException("A terminal lifecycle state requires the authorized reopen command.");
            if (!LifecyclePolicy.AllowedTargets(aggregateType, current).Contains(target))
                throw new LifecycleValidationException($"Transition from {current} to {target} is not allowed.");
        }
        if (LifecyclePolicy.RequiresReason(aggregateType, target, reopen) && string.IsNullOrWhiteSpace(reason))
            throw new LifecycleValidationException("A reason is required for this lifecycle transition.");
    }

    private async Task<CommercialLifecycleEvent?> FindReplayAsync(long businessUnitId, string key, CancellationToken ct)
        => await _db.CommercialLifecycleEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key.Trim(), ct);

    private static LifecycleTransitionResult VerifyReplay(
        CommercialLifecycleEvent replay, string aggregateType, long aggregateId, string requestHash)
    {
        if (replay.AggregateType != aggregateType || replay.AggregateId != aggregateId || replay.RequestHash != requestHash)
            throw new LifecycleConflictException("The idempotency key was already used for a different lifecycle request.");
        return ToResult(replay, true);
    }

    private static LifecycleTransitionResult ToResult(CommercialLifecycleEvent item, bool replayed)
        => new(item.Id, item.CommercialCaseId, item.CommercialCaseReference, item.AggregateType, item.AggregateId,
            item.PreviousStatusId, item.PreviousStatusCode ?? string.Empty, item.NewStatusId, item.NewStatusCode,
            item.AggregateVersion, item.EventType, item.ActorId, item.OccurredOn, item.ReasonCode, item.ReasonNotes,
            item.CorrelationId, item.RequestReference, replayed);

    private static void ValidateInput(long businessUnitId, long aggregateId, LifecycleActor actor, LifecycleTransitionCommand command)
    {
        if (businessUnitId <= 0 || aggregateId <= 0) throw new LifecycleValidationException("Tenant and aggregate identifiers are required.");
        Required(actor.ActorId, nameof(actor.ActorId), 255);
        Required(actor.ActorSource, nameof(actor.ActorSource), 50);
        Required(command.TargetStatusCode, nameof(command.TargetStatusCode), 50);
        Required(command.Source, nameof(command.Source), 100);
        Required(command.CorrelationId, nameof(command.CorrelationId), 100);
        Required(command.RequestReference, nameof(command.RequestReference), 160);
        Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 160);
        if (command.IdempotencyKey.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
            throw new LifecycleValidationException("The legacy idempotency-key namespace is reserved.");
        if (command.ExpectedVersion < 1) throw new LifecycleValidationException("Expected version must be positive.");
        if (command.ReasonCode?.Trim().Length > 100) throw new LifecycleValidationException("Reason code cannot exceed 100 characters.");
        if (command.ReasonNotes?.Trim().Length > 1000) throw new LifecycleValidationException("Reason notes cannot exceed 1000 characters.");
    }

    private static void RejectReservedLeadPromotion(LifecycleTransitionCommand command)
    {
        if (LifecyclePolicy.Canonicalize(LeadAggregate, command.TargetStatusCode) == "CONVERTED_TO_RFQ")
            throw new LifecycleValidationException(
                "CONVERTED_TO_RFQ is reserved for RFQ Promotion after a committed current-revision participation decision.");
    }

    private static void Required(string value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LifecycleValidationException($"{name} is required.");
        if (value.Trim().Length > max) throw new LifecycleValidationException($"{name} cannot exceed {max} characters.");
    }

    private static string HashRequest(string aggregateType, long aggregateId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            aggregateType,
            aggregateId,
            actorId = actor.ActorId.Trim(),
            actorSource = actor.ActorSource.Trim(),
            target = LifecyclePolicy.Canonicalize(aggregateType, command.TargetStatusCode),
            command.ExpectedVersion,
            reasonCode = Clean(command.ReasonCode)?.ToUpperInvariant(),
            reasonNotes = Clean(command.ReasonNotes),
            source = command.Source.Trim(),
            reopen
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    private static void EnsureLinked(Rfq rfq)
    {
        if (rfq.Lead == null || rfq.Lead.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(rfq.Lead.CommercialCaseReference))
            throw new LifecycleValidationException("The RFQ must be linked to a commercial case before its lifecycle can change.");
    }

    private static void EnsureLinked(Quote quote)
    {
        if (!quote.Rfqid.HasValue || !quote.CommercialCaseId.HasValue || quote.CommercialCaseId <= 0
            || string.IsNullOrWhiteSpace(quote.NexoraSerial))
            throw new LifecycleValidationException("The Quote must be linked to an RFQ and Nexora Serial before its lifecycle can change.");
    }

    private sealed class LifecycleAggregate
    {
        private readonly Lead? _lead;
        private readonly Rfq? _rfq;
        private readonly Quote? _quote;
        private LifecycleAggregate(Lead lead)
        {
            if (lead.LeadStatus != null && lead.LeadStatus.BusinessUnitId != lead.BusinessUnitId)
                throw new LifecycleValidationException("The current lifecycle status does not belong to this tenant.");
            _lead = lead;
            AggregateId = lead.Id;
            CommercialCaseId = lead.CommercialCaseId;
            CommercialCaseReference = lead.CommercialCaseReference;
            CurrentStatus = lead.LeadStatus;
            PreviousStatusId = lead.LeadStatusId;
            Version = lead.LifecycleVersion;
            RequiresCommercialReview = lead.RequiresCommercialReview;
            CommercialFactsVerified = lead.CommercialFactsVerified;
            HasUnresolvedCurrentQuantity = lead.LeadItems.Any(item =>
                item.IsCurrentRevisionProjection && (item.Quantity is null or <= 0));
        }
        private LifecycleAggregate(Rfq rfq)
        {
            if (rfq.Rfqstatus != null && rfq.Rfqstatus.BusinessUnitId != rfq.BusinessUnitId)
                throw new LifecycleValidationException("The current lifecycle status does not belong to this tenant.");
            _rfq = rfq;
            AggregateId = rfq.Id;
            CommercialCaseId = rfq.Lead!.CommercialCaseId;
            CommercialCaseReference = rfq.Lead.CommercialCaseReference;
            CurrentStatus = rfq.Rfqstatus;
            PreviousStatusId = rfq.RfqstatusId;
            Version = rfq.LifecycleVersion;
        }
        private LifecycleAggregate(Quote quote)
        {
            if (quote.Status != null && quote.Status.BusinessUnitId != quote.BusinessUnitId)
                throw new LifecycleValidationException("The current lifecycle status does not belong to this tenant.");
            _quote = quote;
            AggregateId = quote.Id;
            CommercialCaseId = quote.CommercialCaseId!.Value;
            CommercialCaseReference = quote.NexoraSerial!;
            CurrentStatus = quote.Status;
            PreviousStatusId = quote.StatusId;
            Version = quote.LifecycleVersion;
        }
        public long AggregateId { get; }
        public long CommercialCaseId { get; }
        public string CommercialCaseReference { get; }
        public SetupMaster? CurrentStatus { get; }
        public long? PreviousStatusId { get; }
        public int Version { get; private set; }
        public bool RequiresCommercialReview { get; }
        public bool CommercialFactsVerified { get; }
        public bool HasUnresolvedCurrentQuantity { get; }
        public static LifecycleAggregate ForLead(Lead lead) => new(lead);
        public static LifecycleAggregate ForRfq(Rfq rfq) => new(rfq);
        public static LifecycleAggregate ForQuote(Quote quote) => new(quote);
        public void SetStatus(long statusId)
        {
            if (_lead != null) _lead.LeadStatusId = statusId;
            else if (_rfq != null) _rfq.RfqstatusId = statusId;
            else _quote!.StatusId = statusId;
        }
        public void IncrementVersion()
        {
            Version++;
            if (_lead != null) _lead.LifecycleVersion = Version;
            else if (_rfq != null) _rfq.LifecycleVersion = Version;
            else _quote!.LifecycleVersion = Version;
        }

        /// <summary>
        /// Stamps (or clears) the lead's terminal outcome. Deliberately the same three columns the
        /// quote carries — reason, note, date — written inside the very SaveChanges that records the
        /// lifecycle event, so a loss and its explanation are never persisted apart.
        /// </summary>
        public void RecordLeadOutcome(long? reasonId, string? note, DateTime? occurredOn)
        {
            if (_lead == null) return;
            _lead.OutcomeReasonId = reasonId;
            _lead.OutcomeNote = note;
            _lead.OutcomeOn = occurredOn;
        }
    }
}
