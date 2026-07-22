using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.CommercialRouting;

public interface ICommercialRoutingApplicationService
{
    Task<RoutingDecisionResponse> RouteLeadAsync(long businessUnitId, RouteLeadCommand command, CancellationToken ct);
    Task<RoutingDecisionResponse> AssignLeadAsync(long businessUnitId, ManualAssignLeadCommand command, CancellationToken ct);
    Task<QueuePageResponse> GetQueueAsync(long businessUnitId, WorkItemStatus? status, string? search,
        bool overdueOnly, int pageNumber, int pageSize, CancellationToken ct);
    Task<UnassignedQueueItemResponse> ClaimAsync(long businessUnitId, long workItemId, QueueLeaseCommand command, CancellationToken ct);
    Task<UnassignedQueueItemResponse> ReleaseAsync(long businessUnitId, long workItemId, QueueReleaseCommand command, CancellationToken ct);
    Task<RoutingDecisionResponse> AssignQueueItemAsync(long businessUnitId, long workItemId, AssignQueueItemCommand command, CancellationToken ct);
    Task<IReadOnlyList<BulkQueueAssignmentResult>> BulkAssignQueueAsync(long businessUnitId, BulkAssignQueueCommand command, CancellationToken ct);
    Task<CustomerIdentifier> UpsertIdentifierAsync(long businessUnitId, UpsertCustomerIdentifierCommand command, CancellationToken ct);
    Task<CustomerOwnership> CreateOwnershipAsync(long businessUnitId, CreateCustomerOwnershipCommand command, CancellationToken ct);
    Task<CustomerRoutingProfileResponse?> GetCustomerProfileAsync(long businessUnitId, long customerId, CancellationToken ct);
}

public sealed class CommercialRoutingApplicationService : ICommercialRoutingApplicationService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly DeterministicRoutingEngine _engine;
    private readonly RoutingPolicy _policy;
    private readonly INotificationService? _notifications;
    private readonly ILogger<CommercialRoutingApplicationService>? _logger;

    public CommercialRoutingApplicationService(
        ErpRfqAutomationContext db,
        DeterministicRoutingEngine engine,
        RoutingPolicy policy,
        INotificationService? notifications = null,
        ILogger<CommercialRoutingApplicationService>? logger = null)
    {
        _db = db;
        _engine = engine;
        _policy = policy;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<RoutingDecisionResponse> RouteLeadAsync(
        long businessUnitId, RouteLeadCommand command, CancellationToken ct)
    {
        ValidateKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));

        var existing = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
        if (existing != null) return existing;

        try
        {
            var routed = await InTransactionAsync(async () =>
            {
                var lead = await _db.Leads
                    .Include(l => l.LeadItems)
                    .SingleOrDefaultAsync(l => l.BusinessUnitId == businessUnitId && l.Id == command.LeadId, ct)
                    ?? throw new RoutingNotFoundException($"Lead {command.LeadId} was not found.");

                if (lead.AssignTo.HasValue)
                    throw new RoutingConflictException("Lead already has an owner. Use an explicit reassignment command.");

                var evidence = BuildEvidence(lead);
                var identifiers = await LoadMatchingIdentifiersAsync(businessUnitId, evidence, ct);
                var customerIds = identifiers.Select(i => i.CustomerId).Distinct().ToArray();
                var ownerships = await _db.Set<CustomerOwnership>()
                    .Where(o => o.BusinessUnitId == businessUnitId && customerIds.Contains(o.CustomerId))
                    .AsNoTracking()
                    .ToListAsync(ct);
                var userIds = ownerships.SelectMany(o => new long?[] { o.PrimaryUserId, o.BackupUserId })
                    .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
                var availability = await _db.Users.AsNoTracking()
                    .Where(u => u.Buid == businessUnitId && userIds.Contains(u.Id))
                    .Select(u => new RoutingUserAvailability(
                        businessUnitId, u.Id, u.IsActive == true, u.IsActive == true, u.IsActive == true ? 100 : 0))
                    .ToListAsync(ct);

                var candidates = identifiers.Select(i => new CustomerMatchCandidate(
                    i.BusinessUnitId, i.CustomerId, i.Id, i.IdentifierType, i.Confidence, i.IsVerified)).ToArray();
                var scopeKeys = command.ScopeKeys ?? await BuildScopeKeysAsync(businessUnitId, lead, ct);
                var request = new RoutingRequest(
                    businessUnitId,
                    lead.Id,
                    command.IdempotencyKey.Trim(),
                    command.CorrelationId.Trim(),
                    DateTime.UtcNow,
                    candidates,
                    ownerships,
                    availability,
                    scopeKeys);
                var result = _engine.Route(request, _policy);

                await PersistRoutingResultAsync(lead, result, ct);
                return ToResponse(result.Decision, result.Assignment?.Id, result.WorkItem?.Id);
            }, ct);
            await TryNotifyAssignmentAsync(businessUnitId, routed, ct);
            return routed;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrentResult = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
            if (concurrentResult != null) return concurrentResult;
            throw;
        }
    }

    public async Task<RoutingDecisionResponse> AssignLeadAsync(
        long businessUnitId, ManualAssignLeadCommand command, CancellationToken ct)
    {
        ValidateKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));
        var existing = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
        if (existing != null) return existing;

        RoutingDecisionResponse assigned;
        try
        {
            assigned = await InTransactionAsync(async () =>
            {
                var lead = await _db.Leads.SingleOrDefaultAsync(
                    l => l.BusinessUnitId == businessUnitId && l.Id == command.LeadId, ct)
                    ?? throw new RoutingNotFoundException($"Lead {command.LeadId} was not found.");
                return await AssignCoreAsync(businessUnitId, lead, command, null, ct);
            }, ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrentResult = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
            if (concurrentResult == null) throw;
            assigned = concurrentResult;
        }
        await TryNotifyAssignmentAsync(businessUnitId, assigned, ct);
        return assigned;
    }

    public async Task<QueuePageResponse> GetQueueAsync(
        long businessUnitId, WorkItemStatus? status, string? search, bool overdueOnly,
        int pageNumber, int pageSize, CancellationToken ct)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var now = DateTime.UtcNow;
        var query = _db.Set<UnassignedWorkItem>().AsNoTracking()
            .Where(w => w.BusinessUnitId == businessUnitId);
        query = status.HasValue
            ? query.Where(w => w.Status == status.Value)
            : query.Where(w => w.Status == WorkItemStatus.Open || w.Status == WorkItemStatus.Claimed);
        if (overdueOnly) query = query.Where(w => w.SlaDueOn < now);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(w => _db.Leads.Any(l => l.Id == w.LeadId &&
                (l.CommercialCaseReference.ToLower().Contains(term) ||
                 (l.Rfqno != null && l.Rfqno.ToLower().Contains(term)) ||
                 (l.BuyersName != null && l.BuyersName.ToLower().Contains(term)))));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(w => w.Priority)
            .ThenBy(w => w.SlaDueOn)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new UnassignedQueueItemResponse(
                w.Id,
                w.LeadId,
                _db.Leads.Where(l => l.Id == w.LeadId).Select(l => l.CommercialCaseReference).First(),
                _db.Leads.Where(l => l.Id == w.LeadId).Select(l => l.Rfqno).FirstOrDefault(),
                _db.Leads.Where(l => l.Id == w.LeadId).Select(l => l.BuyersName).FirstOrDefault(),
                w.ReasonCode,
                w.Status,
                w.Priority,
                w.EnteredOn,
                w.SlaDueOn,
                w.SlaDueOn < now,
                w.SuggestedCustomerId,
                w.SuggestedUserId,
                w.MatchConfidence,
                w.RequiredAction,
                w.ClaimedByUserId,
                w.ClaimedUntil,
                w.Version))
            .ToListAsync(ct);
        return new QueuePageResponse(rows, total, pageNumber, pageSize);
    }

    public Task<UnassignedQueueItemResponse> ClaimAsync(
        long businessUnitId, long workItemId, QueueLeaseCommand command, CancellationToken ct) =>
        MutateLeaseAsync(businessUnitId, workItemId, command.ExpectedVersion, command.UserId, true,
            Math.Clamp(command.LeaseMinutes, 1, 120), ct);

    public Task<UnassignedQueueItemResponse> ReleaseAsync(
        long businessUnitId, long workItemId, QueueReleaseCommand command, CancellationToken ct) =>
        MutateLeaseAsync(businessUnitId, workItemId, command.ExpectedVersion, command.UserId, false, 0, ct);

    public async Task<RoutingDecisionResponse> AssignQueueItemAsync(
        long businessUnitId, long workItemId, AssignQueueItemCommand command, CancellationToken ct)
    {
        ValidateKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));
        var replay = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
        if (replay != null) return replay;

        RoutingDecisionResponse assigned;
        try
        {
            assigned = await InTransactionAsync(async () =>
            {
                var item = await _db.Set<UnassignedWorkItem>().SingleOrDefaultAsync(
                    w => w.BusinessUnitId == businessUnitId && w.Id == workItemId, ct)
                    ?? throw new RoutingNotFoundException($"Queue item {workItemId} was not found.");
                EnsureQueueVersion(item, command.ExpectedVersion);
                if (item.Status is WorkItemStatus.Resolved or WorkItemStatus.Cancelled)
                    throw new RoutingConflictException("Queue item is no longer active.");

                var lead = await _db.Leads.SingleAsync(l => l.BusinessUnitId == businessUnitId && l.Id == item.LeadId, ct);
                var assign = new ManualAssignLeadCommand(
                    lead.Id, command.AssignedToUserId, command.AssignedByUserId,
                    command.IdempotencyKey, command.CorrelationId, command.AssignmentScope,
                    command.Comment, true, lead.AssignTo);
                return await AssignCoreAsync(businessUnitId, lead, assign, item, ct);
            }, ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrentResult = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, ct);
            if (concurrentResult == null) throw;
            assigned = concurrentResult;
        }
        await TryNotifyAssignmentAsync(businessUnitId, assigned, ct);
        return assigned;
    }

    public async Task<IReadOnlyList<BulkQueueAssignmentResult>> BulkAssignQueueAsync(
        long businessUnitId, BulkAssignQueueCommand command, CancellationToken ct)
    {
        if (command.Items.Count is < 1 or > 200)
            throw new ArgumentException("Bulk assignment requires between 1 and 200 queue items.");
        ValidateKey(command.IdempotencyKeyPrefix, nameof(command.IdempotencyKeyPrefix));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));
        if (command.Items.Select(i => i.WorkItemId).Distinct().Count() != command.Items.Count)
            throw new ArgumentException("Bulk assignment cannot contain duplicate queue item IDs.");

        var results = new List<BulkQueueAssignmentResult>(command.Items.Count);
        foreach (var item in command.Items)
        {
            try
            {
                var key = $"{command.IdempotencyKeyPrefix}:{item.WorkItemId}";
                if (key.Length > 160) throw new ArgumentException("Derived idempotency key exceeds 160 characters.");
                var assigned = await AssignQueueItemAsync(businessUnitId, item.WorkItemId,
                    new AssignQueueItemCommand(
                        item.ExpectedVersion, command.AssignedToUserId, command.AssignedByUserId,
                        key, command.CorrelationId, command.AssignmentScope, command.Comment), ct);
                results.Add(new BulkQueueAssignmentResult(item.WorkItemId, true, assigned.DecisionId, null));
            }
            catch (Exception ex) when (ex is RoutingConflictException or RoutingNotFoundException or ArgumentException)
            {
                results.Add(new BulkQueueAssignmentResult(item.WorkItemId, false, null, ex.Message));
            }
        }
        return results;
    }

    public async Task<CustomerIdentifier> UpsertIdentifierAsync(
        long businessUnitId, UpsertCustomerIdentifierCommand command, CancellationToken ct)
    {
        if (command.Confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(command.Confidence));
        if (string.IsNullOrWhiteSpace(command.Source)) throw new ArgumentException("Identifier source is required.");
        var normalized = RoutingValueNormalizer.Normalize(command.IdentifierType, command.Value);
        var customerExists = await _db.Customers.AnyAsync(c => c.Id == command.CustomerId && c.Buid == businessUnitId, ct);
        if (!customerExists) throw new RoutingNotFoundException($"Customer {command.CustomerId} was not found.");

        var current = await _db.Set<CustomerIdentifier>().SingleOrDefaultAsync(i =>
            i.BusinessUnitId == businessUnitId && i.IdentifierType == command.IdentifierType &&
            i.NormalizedValue == normalized && i.CustomerId == command.CustomerId && i.EffectiveTo == null, ct);
        if (IsAuthoritative(command.IdentifierType) && await _db.Set<CustomerIdentifier>().AnyAsync(i =>
                i.BusinessUnitId == businessUnitId && i.IdentifierType == command.IdentifierType &&
                i.NormalizedValue == normalized && i.CustomerId != command.CustomerId && i.EffectiveTo == null, ct))
            throw new RoutingConflictException("Authoritative identifier is already owned by another customer.");
        if (current != null)
        {
            current.DisplayValue = command.Value.Trim();
            current.IsVerified = command.IsVerified;
            current.Confidence = command.Confidence;
            current.Source = command.Source.Trim();
        }
        else
        {
            current = new CustomerIdentifier
            {
                BusinessUnitId = businessUnitId,
                CustomerId = command.CustomerId,
                IdentifierType = command.IdentifierType,
                NormalizedValue = normalized,
                DisplayValue = command.Value.Trim(),
                IsVerified = command.IsVerified,
                Confidence = command.Confidence,
                Source = command.Source.Trim(),
                EffectiveFrom = DateTime.UtcNow
            };
            _db.Add(current);
        }
        await _db.SaveChangesAsync(ct);
        return current;
    }

    public async Task<CustomerOwnership> CreateOwnershipAsync(
        long businessUnitId, CreateCustomerOwnershipCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Source)) throw new ArgumentException("Ownership source is required.");
        if (command.EffectiveTo <= command.EffectiveFrom)
            throw new ArgumentException("EffectiveTo must be after EffectiveFrom.");
        if (command.Scope is not (OwnershipScope.CustomerException or OwnershipScope.GeneralCustomer)
            && string.IsNullOrWhiteSpace(command.ScopeKey))
            throw new ArgumentException("ScopeKey is required for scoped ownership.");

        var customerExists = await _db.Customers.AnyAsync(c => c.Id == command.CustomerId && c.Buid == businessUnitId, ct);
        var users = new long?[] { command.PrimaryUserId, command.BackupUserId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var validUsers = await _db.Users.CountAsync(u => u.Buid == businessUnitId && users.Contains(u.Id) && u.IsActive == true, ct);
        if (!customerExists) throw new RoutingNotFoundException($"Customer {command.CustomerId} was not found.");
        if (validUsers != users.Length) throw new RoutingConflictException("Every owner must be an active user in the same tenant.");

        var ownership = new CustomerOwnership
        {
            BusinessUnitId = businessUnitId,
            CustomerId = command.CustomerId,
            PrimaryUserId = command.PrimaryUserId,
            BackupUserId = command.BackupUserId,
            Scope = command.Scope,
            ScopeKey = command.ScopeKey?.Trim(),
            Priority = command.Priority,
            EffectiveFrom = command.EffectiveFrom.ToUniversalTime(),
            EffectiveTo = command.EffectiveTo?.ToUniversalTime(),
            IsActive = true,
            Source = command.Source.Trim(),
            Reason = command.Reason?.Trim(),
            Version = 1
        };
        _db.Add(ownership);
        await _db.SaveChangesAsync(ct);
        return ownership;
    }

    public async Task<CustomerRoutingProfileResponse?> GetCustomerProfileAsync(
        long businessUnitId, long customerId, CancellationToken ct)
    {
        if (!await _db.Customers.AnyAsync(c => c.Id == customerId && c.Buid == businessUnitId, ct)) return null;
        var identifiers = await _db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == businessUnitId && i.CustomerId == customerId)
            .OrderBy(i => i.IdentifierType).ThenBy(i => i.NormalizedValue).ToListAsync(ct);
        var ownerships = await _db.Set<CustomerOwnership>().AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId && o.CustomerId == customerId)
            .OrderBy(o => o.Scope).ThenByDescending(o => o.Priority).ToListAsync(ct);
        return new CustomerRoutingProfileResponse(customerId, identifiers, ownerships);
    }

    private async Task<RoutingDecisionResponse> AssignCoreAsync(
        long businessUnitId, Lead lead, ManualAssignLeadCommand command,
        UnassignedWorkItem? queueItem, CancellationToken ct)
    {
        if (command.EnforceExpectedAssignee && lead.AssignTo != command.ExpectedAssigneeId)
            throw new RoutingConflictException("Lead assignment changed since it was loaded. Refresh and retry.");
        var assigneeExists = await _db.Users.AnyAsync(u =>
            u.Id == command.AssignedToUserId && u.Buid == businessUnitId && u.IsActive == true, ct);
        if (!assigneeExists) throw new RoutingConflictException("Assignee must be an active user in the same tenant.");

        var now = DateTime.UtcNow;
        var previous = await _db.Set<LeadAssignment>().SingleOrDefaultAsync(a =>
            a.BusinessUnitId == businessUnitId && a.LeadId == lead.Id && a.EffectiveTo == null, ct);
        if (previous != null) previous.EffectiveTo = now;

        var decision = new LeadRoutingDecision
        {
            BusinessUnitId = businessUnitId,
            LeadId = lead.Id,
            SuggestedUserId = command.AssignedToUserId,
            SelectedUserId = command.AssignedToUserId,
            MatchStatus = CustomerMatchStatus.NoEvidence,
            Outcome = RoutingOutcome.AssignedPrimary,
            MatchConfidence = 0,
            DecisionCode = "MANUAL_ASSIGNMENT",
            Explanation = JsonSerializer.Serialize(new { source = "manual", scope = command.AssignmentScope.ToString() }),
            PolicyVersion = _policy.Version,
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            CreatedOn = now
        };
        var assignment = new LeadAssignment
        {
            BusinessUnitId = businessUnitId,
            LeadId = lead.Id,
            FromUserId = lead.AssignTo,
            ToUserId = command.AssignedToUserId,
            AssignmentScope = command.AssignmentScope,
            RoutingDecision = decision,
            ReasonCode = decision.DecisionCode,
            Comment = command.Comment?.Trim(),
            EffectiveFrom = now,
            AssignedByUserId = command.AssignedByUserId,
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim()
        };
        lead.AssignTo = command.AssignedToUserId;
        lead.AssignOn = now;
        lead.AssignComment = command.Comment?.Trim();
        lead.ModifiedDate = now;
        await ResolveActiveQueueItemsAsync(businessUnitId, lead.Id, now, "MANUALLY_ASSIGNED", queueItem, ct);
        _db.Add(decision);
        _db.Add(assignment);
        await _db.SaveChangesAsync(ct);
        return ToResponse(decision, assignment.Id, queueItem?.Id);
    }

    private async Task PersistRoutingResultAsync(Lead lead, RoutingResult result, CancellationToken ct)
    {
        _db.Add(result.Decision);
        if (result.Assignment != null)
        {
            var previous = await _db.Set<LeadAssignment>().SingleOrDefaultAsync(a =>
                a.BusinessUnitId == lead.BusinessUnitId && a.LeadId == lead.Id && a.EffectiveTo == null, ct);
            if (previous != null) previous.EffectiveTo = result.Decision.CreatedOn;
            result.Assignment.FromUserId = lead.AssignTo;
            lead.AssignTo = result.Assignment.ToUserId;
            lead.AssignOn = result.Assignment.EffectiveFrom;
            lead.AssignComment = result.Assignment.ReasonCode;
            lead.ModifiedDate = result.Assignment.EffectiveFrom;
            await ResolveActiveQueueItemsAsync(
                lead.BusinessUnitId, lead.Id, result.Assignment.EffectiveFrom, "AUTO_ASSIGNED", null, ct);
            _db.Add(result.Assignment);
        }
        else if (result.WorkItem != null)
        {
            var active = await _db.Set<UnassignedWorkItem>().SingleOrDefaultAsync(w =>
                w.BusinessUnitId == lead.BusinessUnitId && w.LeadId == lead.Id &&
                (w.Status == WorkItemStatus.Open || w.Status == WorkItemStatus.Claimed), ct);
            if (active != null)
            {
                active.Status = WorkItemStatus.Cancelled;
                active.ResolvedOn = result.Decision.CreatedOn;
                active.ResolutionCode = "SUPERSEDED_BY_REEVALUATION";
                active.Version++;
            }
            result.WorkItem.Version = 1;
            _db.Add(result.WorkItem);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ResolveActiveQueueItemsAsync(
        long businessUnitId, long leadId, DateTime now, string resolution,
        UnassignedWorkItem? known, CancellationToken ct)
    {
        var active = await _db.Set<UnassignedWorkItem>().Where(w =>
                w.BusinessUnitId == businessUnitId && w.LeadId == leadId &&
                (w.Status == WorkItemStatus.Open || w.Status == WorkItemStatus.Claimed))
            .ToListAsync(ct);
        if (known != null && !active.Contains(known)) active.Add(known);
        foreach (var item in active)
        {
            item.Status = WorkItemStatus.Resolved;
            item.ResolvedOn = now;
            item.ResolutionCode = resolution;
            item.ClaimedByUserId = null;
            item.ClaimedUntil = null;
            item.Version++;
        }
    }

    private async Task<UnassignedQueueItemResponse> MutateLeaseAsync(
        long businessUnitId, long workItemId, long expectedVersion, long userId,
        bool claim, int leaseMinutes, CancellationToken ct)
    {
        return await InTransactionAsync(async () =>
        {
            var validUser = await _db.Users.AnyAsync(u => u.Id == userId && u.Buid == businessUnitId && u.IsActive == true, ct);
            if (!validUser) throw new RoutingConflictException("Queue user must be active in the same tenant.");
            var item = await _db.Set<UnassignedWorkItem>().SingleOrDefaultAsync(w =>
                w.BusinessUnitId == businessUnitId && w.Id == workItemId, ct)
                ?? throw new RoutingNotFoundException($"Queue item {workItemId} was not found.");
            EnsureQueueVersion(item, expectedVersion);
            var now = DateTime.UtcNow;
            if (item.Status is WorkItemStatus.Resolved or WorkItemStatus.Cancelled)
                throw new RoutingConflictException("Queue item is no longer active.");
            if (claim && item.Status == WorkItemStatus.Claimed && item.ClaimedUntil > now && item.ClaimedByUserId != userId)
                throw new RoutingConflictException("Queue item is leased by another user.");
            if (!claim && item.ClaimedByUserId != userId)
                throw new RoutingConflictException("Only the current claimant can release this queue item.");

            item.Status = claim ? WorkItemStatus.Claimed : WorkItemStatus.Open;
            item.ClaimedByUserId = claim ? userId : null;
            item.ClaimedUntil = claim ? now.AddMinutes(leaseMinutes) : null;
            item.Version++;
            await _db.SaveChangesAsync(ct);
            return await ProjectQueueItemAsync(item, now, ct);
        }, ct);
    }

    private async Task<UnassignedQueueItemResponse> ProjectQueueItemAsync(UnassignedWorkItem item, DateTime now, CancellationToken ct)
    {
        var lead = await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == item.LeadId, ct);
        return new UnassignedQueueItemResponse(
            item.Id, item.LeadId, lead.CommercialCaseReference, lead.Rfqno, lead.BuyersName,
            item.ReasonCode, item.Status, item.Priority, item.EnteredOn, item.SlaDueOn,
            item.SlaDueOn < now, item.SuggestedCustomerId, item.SuggestedUserId,
            item.MatchConfidence, item.RequiredAction, item.ClaimedByUserId, item.ClaimedUntil, item.Version);
    }

    private async Task<IReadOnlyDictionary<OwnershipScope, string?>> BuildScopeKeysAsync(
        long businessUnitId, Lead lead, CancellationToken ct)
    {
        var branch = await _db.BusinessUnits.Where(b => b.Id == businessUnitId)
            .Select(b => b.BusinessUnitCode).SingleAsync(ct);
        var category = lead.LeadItems.Select(i => i.CommodityProduct).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return new Dictionary<OwnershipScope, string?>
        {
            [OwnershipScope.Branch] = branch,
            [OwnershipScope.ProductCategory] = category
        };
    }

    private static Dictionary<CustomerIdentifierType, HashSet<string>> BuildEvidence(Lead lead)
    {
        var evidence = new Dictionary<CustomerIdentifierType, HashSet<string>>();
        Add(CustomerIdentifierType.Email, lead.Clientemail);
        Add(CustomerIdentifierType.Domain, RoutingValueNormalizer.DomainFromEmail(lead.Clientemail));
        Add(CustomerIdentifierType.CustomerName, lead.BuyersName);
        foreach (var item in lead.LeadItems)
        {
            Add(CustomerIdentifierType.ErpAccount, item.CustomerAccountPortalId);
            Add(CustomerIdentifierType.ErpAccount, item.CompanyRef);
        }
        return evidence;

        void Add(CustomerIdentifierType type, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var normalized = RoutingValueNormalizer.Normalize(type, value);
            if (!evidence.TryGetValue(type, out var values)) evidence[type] = values = [];
            values.Add(normalized);
        }
    }

    private async Task<List<CustomerIdentifier>> LoadMatchingIdentifiersAsync(
        long businessUnitId, Dictionary<CustomerIdentifierType, HashSet<string>> evidence, CancellationToken ct)
    {
        var emails = Values(CustomerIdentifierType.Email);
        var domains = Values(CustomerIdentifierType.Domain);
        var accounts = Values(CustomerIdentifierType.ErpAccount);
        var names = Values(CustomerIdentifierType.CustomerName);
        return await _db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == businessUnitId && i.EffectiveTo == null &&
                ((i.IdentifierType == CustomerIdentifierType.Email && emails.Contains(i.NormalizedValue)) ||
                 (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue)) ||
                 (i.IdentifierType == CustomerIdentifierType.ErpAccount && accounts.Contains(i.NormalizedValue)) ||
                 ((i.IdentifierType == CustomerIdentifierType.CustomerName || i.IdentifierType == CustomerIdentifierType.Alias) && names.Contains(i.NormalizedValue))))
            .ToListAsync(ct);

        string[] Values(CustomerIdentifierType type) => evidence.TryGetValue(type, out var values) ? values.ToArray() : [];
    }

    private async Task<RoutingDecisionResponse?> FindDecisionByKeyAsync(long businessUnitId, string key, CancellationToken ct)
    {
        var decision = await _db.Set<LeadRoutingDecision>().AsNoTracking().SingleOrDefaultAsync(
            d => d.BusinessUnitId == businessUnitId && d.IdempotencyKey == key.Trim(), ct);
        if (decision == null) return null;
        var assignmentId = await _db.Set<LeadAssignment>().Where(a => a.RoutingDecisionId == decision.Id)
            .Select(a => (long?)a.Id).SingleOrDefaultAsync(ct);
        var workItemId = await _db.Set<UnassignedWorkItem>().Where(w => w.RoutingDecisionId == decision.Id)
            .Select(w => (long?)w.Id).SingleOrDefaultAsync(ct);
        return ToResponse(decision, assignmentId, workItemId);
    }

    private async Task TryNotifyAssignmentAsync(
        long businessUnitId, RoutingDecisionResponse response, CancellationToken ct)
    {
        if (_notifications == null || response.AssignmentId == null) return;
        try
        {
            var assignment = await _db.Set<LeadAssignment>().AsNoTracking()
                .SingleAsync(a => a.BusinessUnitId == businessUnitId && a.Id == response.AssignmentId, ct);
            var lead = await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == assignment.LeadId, ct);
            var assignee = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == assignment.ToUserId, ct);
            var assignedBy = assignment.AssignedByUserId.HasValue
                ? await _db.Users.AsNoTracking().Where(u => u.Id == assignment.AssignedByUserId)
                    .Select(u => u.Email).SingleOrDefaultAsync(ct)
                : null;
            await _notifications.NotifyLeadAssignedAsync(new LeadAssignedNotification
            {
                ToEmail = assignee.Email,
                ToName = $"{assignee.FirstName} {assignee.LastName}".Trim(),
                AssigneeName = assignee.FirstName,
                AssignedBy = assignedBy,
                RfqNumber = lead.Rfqno ?? $"#{lead.Id}",
                BuyerName = lead.BuyersName ?? "Unknown buyer",
                Deadline = lead.BidClosingDate?.ToString("dd MMM yyyy") ?? "Not set",
                Comment = assignment.Comment,
                BusinessUnitId = businessUnitId.ToString(),
                CtaPath = $"/procurement/leads/view/{lead.Id}"
            }, ct);

            if (assignment.FromUserId.HasValue && assignment.FromUserId != assignment.ToUserId)
            {
                var previous = await _db.Users.AsNoTracking()
                    .SingleOrDefaultAsync(u => u.Id == assignment.FromUserId, ct);
                if (previous != null && !string.IsNullOrWhiteSpace(previous.Email))
                {
                    await _notifications.NotifyLeadReassignedAwayAsync(new LeadReassignedAwayNotification
                    {
                        ToEmail = previous.Email,
                        ToName = $"{previous.FirstName} {previous.LastName}".Trim(),
                        PreviousAssigneeName = previous.FirstName,
                        NewAssigneeName = $"{assignee.FirstName} {assignee.LastName}".Trim(),
                        RfqNumber = lead.Rfqno ?? $"#{lead.Id}",
                        BuyerName = lead.BuyersName ?? "Unknown buyer",
                        BusinessUnitId = businessUnitId.ToString()
                    }, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Assignment notification failed for lead {LeadId}; the governed assignment remains committed.",
                response.LeadId);
        }
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private static void EnsureQueueVersion(UnassignedWorkItem item, long expectedVersion)
    {
        if (item.Version != expectedVersion)
            throw new RoutingConflictException("Queue item changed since it was loaded. Refresh and retry.");
    }

    private static void ValidateKey(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160)
            throw new ArgumentException("A non-empty key of at most 160 characters is required.", name);
    }

    private static bool IsAuthoritative(CustomerIdentifierType type) => type is
        CustomerIdentifierType.ErpAccount or CustomerIdentifierType.TaxRegistration or
        CustomerIdentifierType.Email or CustomerIdentifierType.Phone;

    private static RoutingDecisionResponse ToResponse(LeadRoutingDecision d, long? assignmentId, long? workItemId) =>
        new(d.Id, d.LeadId, d.CustomerId, d.SelectedUserId, d.MatchStatus, d.Outcome,
            d.MatchConfidence, d.DecisionCode, d.Explanation, d.PolicyVersion,
            d.CorrelationId, d.CreatedOn, assignmentId, workItemId);
}
