using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public interface ILifecycleApplicationService
{
    Task<LifecycleStateView> GetLeadStateAsync(long businessUnitId, long leadId, CancellationToken ct);
    Task<LifecycleStateView> GetRfqStateAsync(long businessUnitId, long rfqId, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionLeadAsync(long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
    Task<LifecycleTransitionResult> TransitionRfqAsync(long businessUnitId, long rfqId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct);
}

public sealed class LifecycleApplicationService : ILifecycleApplicationService
{
    private const string LeadAggregate = "Lead";
    private const string RfqAggregate = "Rfq";
    private readonly ErpRfqAutomationContext _db;

    public LifecycleApplicationService(ErpRfqAutomationContext db) => _db = db;

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

    public Task<LifecycleTransitionResult> TransitionLeadAsync(
        long businessUnitId, long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
        => ExecuteAsync(LeadAggregate, businessUnitId, leadId, actor, command, reopen, ct);

    public Task<LifecycleTransitionResult> TransitionRfqAsync(
        long businessUnitId, long rfqId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct)
        => ExecuteAsync(RfqAggregate, businessUnitId, rfqId, actor, command, reopen, ct);

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

                var payload = JsonSerializer.Serialize(new
                {
                    lifecycleEvent.Id,
                    lifecycleEvent.BusinessUnitId,
                    lifecycleEvent.CommercialCaseId,
                    lifecycleEvent.CommercialCaseReference,
                    lifecycleEvent.AggregateType,
                    lifecycleEvent.AggregateId,
                    lifecycleEvent.EventType,
                    lifecycleEvent.PreviousStatusCode,
                    lifecycleEvent.NewStatusCode,
                    lifecycleEvent.AggregateVersion,
                    lifecycleEvent.ActorId,
                    lifecycleEvent.ActorSource,
                    lifecycleEvent.OccurredOn,
                    lifecycleEvent.ReasonCode,
                    lifecycleEvent.ReasonNotes,
                    lifecycleEvent.PolicyVersion,
                    lifecycleEvent.Source,
                    lifecycleEvent.CorrelationId,
                    lifecycleEvent.RequestReference
                });
                _db.LifecycleOutboxMessages.Add(new LifecycleOutboxMessage
                {
                    BusinessUnitId = businessUnitId,
                    LifecycleEvent = lifecycleEvent,
                    EventType = $"commercial-case.{aggregateType.ToLowerInvariant()}.{lifecycleEvent.EventType.ToLowerInvariant()}",
                    Payload = payload,
                    SchemaVersion = 1,
                    OccurredOn = now,
                    AvailableOn = now
                });
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return ToResult(lifecycleEvent, false);
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

    private async Task<LifecycleAggregate> LoadAggregateAsync(string aggregateType, long businessUnitId, long aggregateId, CancellationToken ct)
    {
        if (aggregateType == LeadAggregate)
        {
            var lead = await _db.Leads.Include(x => x.LeadStatus)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == aggregateId, ct)
                ?? throw new LifecycleNotFoundException("Lead was not found.");
            return LifecycleAggregate.ForLead(lead);
        }

        var rfq = await _db.Rfqs.Include(x => x.Rfqstatus).Include(x => x.Lead)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == aggregateId, ct)
            ?? throw new LifecycleNotFoundException("RFQ was not found.");
        EnsureLinked(rfq);
        return LifecycleAggregate.ForRfq(rfq);
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
            .Where(x => allowed.Contains(x.Code))
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
        var expectedType = aggregateType == LeadAggregate ? "leadstatus" : "rfqstatus";
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

    private static void EnsureLinked(Rfq rfq)
    {
        if (rfq.Lead == null || rfq.Lead.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(rfq.Lead.CommercialCaseReference))
            throw new LifecycleValidationException("The RFQ must be linked to a commercial case before its lifecycle can change.");
    }

    private sealed class LifecycleAggregate
    {
        private readonly Lead? _lead;
        private readonly Rfq? _rfq;
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
        public long AggregateId { get; }
        public long CommercialCaseId { get; }
        public string CommercialCaseReference { get; }
        public SetupMaster? CurrentStatus { get; }
        public long? PreviousStatusId { get; }
        public int Version { get; private set; }
        public bool RequiresCommercialReview { get; }
        public bool CommercialFactsVerified { get; }
        public static LifecycleAggregate ForLead(Lead lead) => new(lead);
        public static LifecycleAggregate ForRfq(Rfq rfq) => new(rfq);
        public void SetStatus(long statusId)
        {
            if (_lead != null) _lead.LeadStatusId = statusId;
            else _rfq!.RfqstatusId = statusId;
        }
        public void IncrementVersion()
        {
            Version++;
            if (_lead != null) _lead.LifecycleVersion = Version;
            else _rfq!.LifecycleVersion = Version;
        }
    }
}
