using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ERP_RFQ_Automation.CommercialRouting;

public interface ICommercialRoutingApplicationService
{
    Task<RoutingDecisionResponse> RouteLeadAsync(long businessUnitId, RouteLeadCommand command, CancellationToken ct);
    Task<RoutingDecisionResponse> AssignLeadAsync(long businessUnitId, ManualAssignLeadCommand command, CancellationToken ct);
    Task<LeadOwnershipResponse> ChangeLeadOwnershipAsync(long businessUnitId, ChangeLeadOwnershipCommand command, CancellationToken ct);
    Task<IReadOnlyList<RoutingOwnerOptionResponse>> GetOwnerOptionsAsync(long businessUnitId, CancellationToken ct);
    Task<QueuePageResponse> GetQueueAsync(long businessUnitId, WorkItemStatus? status, string? search,
        bool overdueOnly, int pageNumber, int pageSize, CancellationToken ct);
    Task<UnassignedQueueItemResponse> ClaimAsync(long businessUnitId, long workItemId, QueueLeaseCommand command, CancellationToken ct);
    Task<UnassignedQueueItemResponse> ReleaseAsync(long businessUnitId, long workItemId, QueueReleaseCommand command, CancellationToken ct);
    Task<RoutingDecisionResponse> AssignQueueItemAsync(long businessUnitId, long workItemId, AssignQueueItemCommand command, CancellationToken ct);
    Task<IReadOnlyList<BulkQueueAssignmentResult>> BulkAssignQueueAsync(long businessUnitId, BulkAssignQueueCommand command, CancellationToken ct);
    Task<CustomerIdentifier> UpsertIdentifierAsync(long businessUnitId, UpsertCustomerIdentifierCommand command, CancellationToken ct);
    Task<CustomerOwnership> CreateOwnershipAsync(long businessUnitId, CreateCustomerOwnershipCommand command, CancellationToken ct);
    Task<CustomerRoutingProfileResponse?> GetCustomerProfileAsync(long businessUnitId, long customerId, CancellationToken ct);
    Task<DefaultLeadOwnerResponse> GetDefaultOwnerAsync(long businessUnitId, CancellationToken ct);
    Task<DefaultLeadOwnerResponse> SetDefaultOwnerAsync(long businessUnitId, SetDefaultLeadOwnerCommand command, CancellationToken ct);
}

public sealed class CommercialRoutingApplicationService : ICommercialRoutingApplicationService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly DeterministicRoutingEngine _engine;
    private readonly RoutingPolicy _policy;
    private readonly INotificationService? _notifications;
    private readonly ILogger<CommercialRoutingApplicationService>? _logger;

    /// <summary>
    /// The resolver's own policy, read here for the one rule routing must share with it: how long an
    /// account number must be before it names a customer. The singleton the resolver uses when it is
    /// registered; the shipped defaults otherwise, which are what that singleton holds.
    /// </summary>
    private readonly CustomerResolution.CustomerResolutionPolicy _identityPolicy;

    public CommercialRoutingApplicationService(
        ErpRfqAutomationContext db,
        DeterministicRoutingEngine engine,
        RoutingPolicy policy,
        INotificationService? notifications = null,
        ILogger<CommercialRoutingApplicationService>? logger = null,
        CustomerResolution.CustomerResolutionPolicy? identityPolicy = null)
    {
        _db = db;
        _engine = engine;
        _policy = policy;
        _notifications = notifications;
        _logger = logger;
        _identityPolicy = identityPolicy ?? new CustomerResolution.CustomerResolutionPolicy();
    }

    public async Task<RoutingDecisionResponse> RouteLeadAsync(
        long businessUnitId, RouteLeadCommand command, CancellationToken ct)
    {
        ValidateKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));
        var requestHash = RoutingRequestFingerprint.ForRoute(businessUnitId, command);

        var existing = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
        if (existing != null) return existing;

        // What "us" means for a sender address, from the same loader the resolver and the learner
        // use, so routing can never treat a colleague's forward as a customer's mail when they
        // would not. Read BEFORE the serializable transaction on purpose: it is tenant setup
        // (mailboxes and staff), not part of the decision's consistency, and putting the tenant's
        // whole user list into a serializable read set only buys spurious retries when someone
        // edits a user while a lead is being routed.
        var selfDomains = await CustomerResolution.TenantSelfIdentity.LoadSelfDomainsAsync(_db, businessUnitId, ct);
        // The tenant's own name, for the second test of "ours": a domain that spells it. The learner
        // and the resolver both refuse such a domain, so routing must too, or a colleague with no
        // Nexora login forwarding from it is routed on a legacy row the other two ignore. Tenant setup,
        // read before the transaction for the same reason as the domains above.
        var tenantName = await _db.BusinessUnits.AsNoTracking()
            .Where(unit => unit.Id == businessUnitId)
            .Select(unit => unit.BusinessUnitName)
            .SingleOrDefaultAsync(ct);

        try
        {
            var routed = await InTransactionAsync(async () =>
            {
                var lead = await _db.Leads
                    .Include(l => l.LeadItems)
                    .SingleOrDefaultAsync(l => l.BusinessUnitId == businessUnitId && l.Id == command.LeadId, ct)
                    ?? throw new RoutingNotFoundException($"Lead {command.LeadId} was not found.");

                if (lead.ManualAssignmentOverride)
                    throw new RoutingConflictException("Automatic routing is paused by a manual ownership override. Return the lead to automatic routing first.");
                if (lead.AssignTo.HasValue)
                    throw new RoutingConflictException("Lead already has an owner. Use an explicit reassignment command.");

                // Our names for the "ours" test on a sender: the tenant's own, and this document's vendor
                // block only where it spells the tenant's. The vendor block is an extracted value and can
                // hold the buyer's name: a Marafiq print read as vendor "MARAFIQ" made marafiq.com.sa ours,
                // and Marafiq's registered address and domain were dropped here, so the lead routed
                // NO_MATCH_EVIDENCE to the unassigned queue. The same names the resolver uses.
                var selfNames = CustomerResolution.TenantSelfIdentity.DomainSelfNames([tenantName], lead.SupplierNameOnDocument);
                var evidence = BuildEvidence(lead, selfDomains, selfNames, _identityPolicy.MinimumErpAccountLength);
                var identifiers = await LoadMatchingIdentifiersAsync(
                    businessUnitId, evidence, selfDomains, selfNames,
                    CustomerResolution.IdentityDomainGuard.IsSystemMailbox(lead.Clientemail), ct);

                // A customer a HUMAN has already confirmed on this lead is the strongest evidence
                // that exists — stronger than any inferred email/domain identifier. It was being
                // ignored: the engine only ever looked at customer_identifiers, so a lead whose
                // customer was confirmed still routed NO_MATCH_EVIDENCE and landed in the
                // unassigned queue. That is why 44 of 44 production leads were unassigned even
                // where ownership existed.
                //
                // Modelled as a top-precedence ErpAccount candidate at full confidence rather
                // than as a bypass, so ownership precedence, workload relief, the ambiguity
                // margin and the audit trail all still apply exactly as they do for a matched
                // identifier. IdentifierId 0 marks it as derived from the lead rather than from
                // a customer_identifiers row.
                var confirmedCustomerId =
                    LeadCustomerMatchStatuses.IsHumanDecided(lead.CustomerMatchStatus) && lead.CustomerId.HasValue
                        ? lead.CustomerId
                        : null;

                var customerIds = identifiers.Select(i => i.CustomerId)
                    .Concat(confirmedCustomerId.HasValue ? [confirmedCustomerId.Value] : Array.Empty<long>())
                    .Distinct().ToArray();
                var ownerships = await _db.Set<CustomerOwnership>()
                    .Where(o => o.BusinessUnitId == businessUnitId && customerIds.Contains(o.CustomerId))
                    .AsNoTracking()
                    .ToListAsync(ct);
                // The tenant's one customer-set fallback owner. Read HERE, in the application
                // service, and handed to the engine as data — the engine stays pure and never
                // learns that a BusinessUnits table exists. Its availability is loaded in the same
                // batch as the ownership candidates', because the engine tests the fallback with
                // exactly the same IsAvailable predicate and a missing availability row would
                // silently make an eligible fallback owner look unavailable.
                var defaultOwnerUserId = await _db.BusinessUnits.AsNoTracking()
                    .Where(unit => unit.Id == businessUnitId)
                    .Select(unit => unit.DefaultLeadOwnerUserId)
                    .SingleOrDefaultAsync(ct);
                var userIds = ownerships.SelectMany(o => new long?[] { o.PrimaryUserId, o.BackupUserId })
                    .Concat([defaultOwnerUserId])
                    .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
                var occurredOn = DateTime.UtcNow;
                var availability = await LoadUserAvailabilityAsync(businessUnitId, userIds, occurredOn, ct);

                var candidates = identifiers.Select(i => new CustomerMatchCandidate(
                        i.BusinessUnitId, i.CustomerId, i.Id, i.IdentifierType, i.Confidence, i.IsVerified))
                    .Concat(confirmedCustomerId.HasValue
                        ? [new CustomerMatchCandidate(businessUnitId, confirmedCustomerId.Value, 0,
                            CustomerIdentifierType.ErpAccount, 1m, IsVerified: true)]
                        : Array.Empty<CustomerMatchCandidate>())
                    .ToArray();
                var derivations = command.ScopeKeys is { } supplied
                    ? RoutingScopeKeys.FromSuppliedKeys(supplied)
                    : await BuildScopeKeyDerivationsAsync(businessUnitId, lead, ct);
                // Only scopes with a real key are offered to the engine. An underived scope is
                // absent rather than present-and-empty, so its rules match nothing at all.
                var scopeKeys = derivations
                    .Where(derivation => derivation.IsDerived)
                    .ToDictionary(derivation => derivation.Scope, derivation => derivation.Key);
                var request = new RoutingRequest(
                    businessUnitId,
                    lead.Id,
                    command.IdempotencyKey.Trim(),
                    command.CorrelationId.Trim(),
                    occurredOn,
                    candidates,
                    ownerships,
                    availability,
                    scopeKeys,
                    RequestHash: requestHash,
                    ScopeKeyDerivations: derivations,
                    DefaultOwnerUserId: defaultOwnerUserId);
                var result = _engine.Route(request, _policy);

                // The engine has always proven customer matches and persisted them onto
                // LeadRoutingDecision.CustomerId — while the Lead itself stayed unlinked.
                // Write the link through (same transaction) when, and only when, the match
                // is exact-identifier grade; see WriteCustomerThroughToLead.
                WriteCustomerThroughToLead(lead, result.Decision, identifiers);
                await PersistRoutingResultAsync(lead, result, ct);
                return ToResponse(result.Decision, result.Assignment?.Id, result.WorkItem?.Id);
            }, ct);
            await TryNotifyAssignmentAsync(businessUnitId, routed, ct);
            return routed;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrentResult = await FindDecisionByKeyAsync(
                businessUnitId, command.IdempotencyKey, requestHash, ct);
            if (concurrentResult != null) return concurrentResult;
            throw;
        }
    }

    public async Task<RoutingDecisionResponse> AssignLeadAsync(
        long businessUnitId, ManualAssignLeadCommand command, CancellationToken ct)
    {
        ValidateKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));
        var requestHash = RoutingRequestFingerprint.ForManualAssignment(businessUnitId, command);
        var existing = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
        if (existing != null) return existing;

        RoutingDecisionResponse assigned;
        try
        {
            assigned = await InTransactionAsync(async () =>
            {
                var lead = await _db.Leads.SingleOrDefaultAsync(
                    l => l.BusinessUnitId == businessUnitId && l.Id == command.LeadId, ct)
                    ?? throw new RoutingNotFoundException($"Lead {command.LeadId} was not found.");
                return await AssignCoreAsync(businessUnitId, lead, command, null, requestHash, ct);
            }, ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrentResult = await FindDecisionByKeyAsync(
                businessUnitId, command.IdempotencyKey, requestHash, ct);
            if (concurrentResult == null)
                throw new RoutingConflictException("Queue assignment changed concurrently. Refresh and retry.");
            assigned = concurrentResult;
        }
        await TryNotifyAssignmentAsync(businessUnitId, assigned, ct);
        return assigned;
    }

    public async Task<LeadOwnershipResponse> ChangeLeadOwnershipAsync(
        long businessUnitId, ChangeLeadOwnershipCommand command, CancellationToken ct)
    {
        ValidateKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateKey(command.CorrelationId, nameof(command.CorrelationId));
        if (command.ExpectedAssignmentVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.ExpectedAssignmentVersion));

        if (command.Action == LeadOwnershipAction.Assign)
        {
            if (!command.AssignedToUserId.HasValue)
                throw new ArgumentException("An owner is required for assignment.");
            // Read-only authority check before the write path. The release actions below repeat it
            // INSIDE their transaction; Assign cannot, because its write path is AssignLeadAsync,
            // which opens its own. The gap that leaves is closed by the version fence rather than
            // by a lock: ExpectedAssignmentVersion travels into AssignLeadAsync, and any ownership
            // change between this read and that write bumps AssignmentVersion, so the assignment is
            // refused as a conflict instead of proceeding on an authority decision made about an
            // owner who has since changed.
            EnsureOwnershipChangeIsPermitted(await LoadLeadAsync(businessUnitId, command.LeadId, ct), command);
            var assigned = await AssignLeadAsync(businessUnitId, new ManualAssignLeadCommand(
                command.LeadId, command.AssignedToUserId.Value, command.AssignedByUserId,
                command.IdempotencyKey, command.CorrelationId, AssignmentScope.LeadOnly,
                command.Comment, false, null, command.ExpectedAssignmentVersion), ct);
            var current = await _db.Leads.AsNoTracking().SingleAsync(
                x => x.BusinessUnitId == businessUnitId && x.Id == command.LeadId, ct);
            return Ownership(current, assigned);
        }

        var decision = await InTransactionAsync(async () =>
        {
            var lead = await _db.Leads.SingleOrDefaultAsync(
                x => x.BusinessUnitId == businessUnitId && x.Id == command.LeadId, ct)
                ?? throw new RoutingNotFoundException($"Lead {command.LeadId} was not found.");
            EnsureOwnershipChangeIsPermitted(lead, command);
            if (lead.AssignmentVersion != command.ExpectedAssignmentVersion)
                throw new RoutingConflictException("Lead assignment changed since it was loaded. Refresh and retry.");

            var now = DateTime.UtcNow;
            var active = await _db.Set<LeadAssignment>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.LeadId == lead.Id && x.EffectiveTo == null, ct);
            if (active != null) active.EffectiveTo = now;
            var returnsToAutomatic = command.Action == LeadOwnershipAction.ReturnToAutomatic;
            var code = returnsToAutomatic
                ? "RETURNED_TO_AUTOMATIC_ROUTING" : DeterministicRoutingEngine.ManuallyUnassignedCode;
            var routingDecision = new LeadRoutingDecision
            {
                BusinessUnitId = businessUnitId,
                LeadId = lead.Id,
                MatchStatus = CustomerMatchStatus.NoEvidence,
                Outcome = RoutingOutcome.Unassigned,
                MatchConfidence = 0,
                DecisionCode = code,
                Explanation = JsonSerializer.Serialize(new
                {
                    source = returnsToAutomatic ? "automatic" : "manual",
                    action = command.Action.ToString(),
                    requestHash = $"ownership:{businessUnitId}:{command.LeadId}:{command.Action}:{command.ExpectedAssignmentVersion}"
                }),
                PolicyVersion = _policy.Version,
                CorrelationId = command.CorrelationId.Trim(),
                IdempotencyKey = command.IdempotencyKey.Trim(),
                CreatedOn = now
            };
            lead.AssignTo = null;
            lead.AssignOn = now;
            lead.AssignComment = command.Comment?.Trim();
            lead.AssignedByUserId = command.AssignedByUserId;
            lead.AssignmentMethod = returnsToAutomatic
                ? LeadAssignmentMethods.Automatic : LeadAssignmentMethods.Manual;

            // MANUAL OVERRIDE IS CLEARED BY BOTH RELEASE ACTIONS, and that is a deliberate change.
            //
            // ManualAssignmentOverride has exactly one behaviour: RouteLeadAsync refuses to run
            // while it is set. It exists to stop the router overwriting an owner a human chose. An
            // unassign destroys the assignment — after it there is no human choice left to protect,
            // so leaving the fence up protected nothing and cost everything: the lead had no owner,
            // no queue row, was invisible to the reconciliation worker (which only reconsiders
            // leads with NO routing decision at all), and could not even be re-routed by hand
            // because the fence threw. "Return this to the pool" quietly meant "delete this from
            // the business".
            //
            // Clearing it does NOT make Unassign the same action as ReturnToAutomatic. Unassign
            // still does not run the router: it parks the lead on the queue for a human, and only
            // stops BLOCKING the router should someone later ask for it. ReturnToAutomatic hands
            // it straight back to the engine in the same call. One waits for a person; the other
            // does not.
            //
            // AssignmentMethod stays MANUAL for an unassign, because it is a different fact: it
            // records that the last ownership decision on this lead was a human's, which is true
            // and is what the lead record and its history should say.
            lead.ManualAssignmentOverride = false;
            lead.AssignmentVersion++;
            lead.ModifiedDate = now;
            _db.Add(routingDecision);

            // A released lead lands on the queue a human can actually see. Built by the engine's
            // own factory rather than a second hand-written copy, so a manually released lead is
            // indistinguishable in shape from any other unplaced one and every screen, sweeper and
            // SLA that already reads that table picks it up for free.
            //
            // ReturnToAutomatic is excluded: it re-routes immediately below, and RouteLeadAsync
            // writes whatever queue row that decision calls for. Queuing here as well would create
            // a row and cancel it microseconds later.
            if (!returnsToAutomatic)
            {
                await SupersedeActiveQueueItemsAsync(businessUnitId, lead.Id, now, ct);
                var workItem = DeterministicRoutingEngine.WorkItemFor(routingDecision, _policy);
                workItem.Version = 1;
                _db.Add(workItem);
                await _db.SaveChangesAsync(ct);
                return ToResponse(routingDecision, null, workItem.Id);
            }

            await _db.SaveChangesAsync(ct);
            return ToResponse(routingDecision, null, null);
        }, ct);

        if (command.Action == LeadOwnershipAction.ReturnToAutomatic)
        {
            var routeKey = $"{command.IdempotencyKey.Trim()}:route";
            decision = await RouteLeadAsync(businessUnitId, new RouteLeadCommand(
                command.LeadId, routeKey, command.CorrelationId), ct);
        }
        var refreshed = await _db.Leads.AsNoTracking().SingleAsync(
            x => x.BusinessUnitId == businessUnitId && x.Id == command.LeadId, ct);
        return Ownership(refreshed, decision);
    }

    private async Task<Lead> LoadLeadAsync(long businessUnitId, long leadId, CancellationToken ct) =>
        await _db.Leads.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
        ?? throw new RoutingNotFoundException($"Lead {leadId} was not found.");

    /// <summary>
    /// Who may change a lead's owner from the lead-detail screen.
    ///
    /// <para>THE RULE: a person can take work that is nobody's, and can put down work that is
    /// theirs. Moving somebody else's work is a manager's decision.</para>
    ///
    /// <para>It lives in the application service, not on the endpoint, because the authority
    /// depends on the LEAD — on who currently owns it — and an <c>[Authorize]</c> attribute cannot
    /// see a database row. That is exactly how the hole opened: the four assignment endpoints that
    /// could be expressed as "managers only" carried <c>[RequireManagerRole]</c>, while
    /// <c>PUT leads/{id}/owner</c> — the one the lead detail screen actually calls — could not be,
    /// so it carried nothing at all, and a rep who was refused at the manager-only routing queue
    /// could reassign any lead in the tenant from the detail page instead.</para>
    ///
    /// <para>The queue endpoints are deliberately NOT relaxed to match. Claim and release already
    /// give a rep the pull lane there, and a queue assignment is a manager confirming a routing
    /// recommendation, which is a different act from a rep picking up an unowned inquiry.</para>
    /// </summary>
    private static void EnsureOwnershipChangeIsPermitted(Lead lead, ChangeLeadOwnershipCommand command)
    {
        if (command.ActorIsManager) return;

        // Fail closed. Without a resolved caller there is no "own work" to compare against, so the
        // rule cannot be evaluated and must not be assumed to pass.
        if (command.AssignedByUserId is not long actorId)
            throw new RoutingForbiddenException(
                "Nexora could not tell who is making this change, so it cannot check whether you are "
                + "allowed to make it. Sign in again and retry.");

        if (lead.AssignTo.HasValue && lead.AssignTo.Value != actorId)
            throw new RoutingForbiddenException(
                "This lead belongs to someone else. Only a manager can move it. You can take a lead "
                + "that has no owner, or hand back one of your own.");

        if (command.Action == LeadOwnershipAction.Assign && command.AssignedToUserId != actorId)
            throw new RoutingForbiddenException(
                "Only a manager can hand a lead to someone else. You can take this lead yourself, or "
                + "ask a manager to assign it.");
    }

    /// <summary>
    /// Cancels any open or claimed queue row for a lead that is about to get a NEW one, so the
    /// single-active-row-per-lead index can never be violated and no reader is left choosing
    /// between two live rows. Same shape and same resolution code the routing path uses when a
    /// re-evaluation replaces an earlier queue row.
    /// </summary>
    private async Task SupersedeActiveQueueItemsAsync(
        long businessUnitId, long leadId, DateTime now, CancellationToken ct)
    {
        var active = await _db.Set<UnassignedWorkItem>().Where(w =>
                w.BusinessUnitId == businessUnitId && w.LeadId == leadId &&
                (w.Status == WorkItemStatus.Open || w.Status == WorkItemStatus.Claimed))
            .ToListAsync(ct);
        foreach (var item in active)
        {
            item.Status = WorkItemStatus.Cancelled;
            item.ResolvedOn = now;
            item.ResolutionCode = "SUPERSEDED_BY_REEVALUATION";
            item.ClaimedByUserId = null;
            item.ClaimedUntil = null;
            item.Version++;
        }
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

    public async Task<IReadOnlyList<RoutingOwnerOptionResponse>> GetOwnerOptionsAsync(
        long businessUnitId, CancellationToken ct)
    {
        var measuredAt = DateTime.UtcNow;
        var profiledUserIds = _db.SalesRepProfiles.AsNoTracking()
            .Where(profile => profile.BusinessUnitId == businessUnitId &&
                profile.EffectiveFromUtc <= measuredAt &&
                (!profile.EffectiveToUtc.HasValue || profile.EffectiveToUtc > measuredAt))
            .Select(profile => profile.UserId);
        var teamUserIds = _db.SalesTeamMemberships.AsNoTracking()
            .Where(membership => membership.BusinessUnitId == businessUnitId &&
                membership.EffectiveFromUtc <= measuredAt &&
                (!membership.EffectiveToUtc.HasValue || membership.EffectiveToUtc > measuredAt))
            .Select(membership => membership.UserId);
        var activeOwnerships = _db.Set<CustomerOwnership>().AsNoTracking()
            .Where(ownership => ownership.BusinessUnitId == businessUnitId && ownership.IsActive &&
                ownership.EffectiveFrom <= measuredAt &&
                (!ownership.EffectiveTo.HasValue || ownership.EffectiveTo > measuredAt));
        var ownershipUserIds = activeOwnerships.Select(ownership => ownership.PrimaryUserId)
            .Concat(activeOwnerships.Where(ownership => ownership.BackupUserId.HasValue)
                .Select(ownership => ownership.BackupUserId!.Value));
        var assignmentUserIds = _db.Set<LeadAssignment>().AsNoTracking()
            .Where(assignment => assignment.BusinessUnitId == businessUnitId && assignment.EffectiveTo == null)
            .Select(assignment => assignment.ToUserId);
        var candidateUserIds = await profiledUserIds.Concat(teamUserIds)
            .Concat(ownershipUserIds).Concat(assignmentUserIds).Distinct().ToArrayAsync(ct);
        if (candidateUserIds.Length == 0) return Array.Empty<RoutingOwnerOptionResponse>();
        var users = await _db.Users.AsNoTracking()
            .Where(user => user.Buid == businessUnitId && user.IsActive == true && candidateUserIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                Name = (user.FirstName + " " + user.LastName).Trim(),
                user.Email,
                RoleName = user.Role == null ? null : user.Role.SetupValue
            })
            .ToListAsync(ct);
        var availability = (await LoadUserAvailabilityAsync(
                businessUnitId, users.Select(user => user.Id).ToArray(), measuredAt, ct))
            .ToDictionary(value => value.UserId);

        return users.Select(user =>
            {
                var current = availability[user.Id];
                return new RoutingOwnerOptionResponse(
                    user.Id, user.Name, user.Email, user.RoleName, current.IsAvailable,
                    current.CapacityPercent, current.Workload!, current.HasGovernedProfile,
                    current.EligibilityReason, measuredAt, _policy.Version);
            })
            .OrderByDescending(user => user.IsAvailable)
            .ThenBy(user => user.Workload.WorkloadPoints)
            .ThenBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.UserId)
            .ToArray();
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
        var requestHash = RoutingRequestFingerprint.ForQueueAssignment(businessUnitId, workItemId, command);
        var replay = await FindDecisionByKeyAsync(businessUnitId, command.IdempotencyKey, requestHash, ct);
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
                return await AssignCoreAsync(businessUnitId, lead, assign, item, requestHash, ct);
            }, ct);
        }
        catch (Exception ex)
        {
            if (!IsQueueAssignmentConflict(ex))
                throw;

            _db.ChangeTracker.Clear();
            var concurrentResult = await FindDecisionByKeyAsync(
                businessUnitId, command.IdempotencyKey, requestHash, ct);
            if (concurrentResult == null)
                throw new RoutingConflictException("Queue assignment changed concurrently. Refresh and retry.");
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

        try
        {
            return await InTransactionAsync(async () =>
            {
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({$"customer-owner:{businessUnitId}:{command.CustomerId}:{command.Scope}:{command.ScopeKey?.Trim() ?? string.Empty}"}, 0))", ct);

                var customerExists = await _db.Customers.AnyAsync(
                    c => c.Id == command.CustomerId && c.Buid == businessUnitId, ct);
                var users = new long?[] { command.PrimaryUserId, command.BackupUserId }
                    .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
                var validUsers = await _db.Users.CountAsync(
                    u => u.Buid == businessUnitId && users.Contains(u.Id) && u.IsActive == true, ct);
                if (!customerExists) throw new RoutingNotFoundException($"Customer {command.CustomerId} was not found.");
                if (validUsers != users.Length)
                    throw new RoutingConflictException("Every owner must be an active user in the same tenant.");

                var scopeKey = command.ScopeKey?.Trim();
                var starts = command.EffectiveFrom.ToUniversalTime();
                var ends = command.EffectiveTo?.ToUniversalTime();
                var overlaps = await _db.Set<CustomerOwnership>().AnyAsync(o =>
                    o.BusinessUnitId == businessUnitId && o.CustomerId == command.CustomerId &&
                    o.Scope == command.Scope && o.ScopeKey == scopeKey && o.IsActive &&
                    (ends == null || o.EffectiveFrom < ends) &&
                    (o.EffectiveTo == null || o.EffectiveTo > starts), ct);
                if (overlaps)
                    throw new RoutingConflictException("An effective owner already exists for this customer scope.");

                var ownership = new CustomerOwnership
                {
                    BusinessUnitId = businessUnitId, CustomerId = command.CustomerId,
                    PrimaryUserId = command.PrimaryUserId, BackupUserId = command.BackupUserId,
                    Scope = command.Scope, ScopeKey = scopeKey, Priority = command.Priority,
                    EffectiveFrom = starts, EffectiveTo = ends, IsActive = true,
                    Source = command.Source.Trim(), Reason = command.Reason?.Trim(), Version = 1
                };
                _db.Add(ownership);
                await _db.SaveChangesAsync(ct);
                return ownership;
            }, ct, IsolationLevel.ReadCommitted);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains(
            "UX_customer_ownerships_single_active", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new RoutingConflictException("An effective owner already exists for this customer scope.");
        }
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

    /// <summary>
    /// Reads the tenant's fallback lead owner, with the verdict on whether routing will actually
    /// use them. The setting and its usefulness are two different facts and the setup screen needs
    /// both: a name can be saved and still be inert, and the only honest way to say so is in the
    /// routing engine's own words.
    /// </summary>
    public async Task<DefaultLeadOwnerResponse> GetDefaultOwnerAsync(
        long businessUnitId, CancellationToken ct)
    {
        var unit = await _db.BusinessUnits.AsNoTracking()
            .Where(x => x.Id == businessUnitId)
            .Select(x => new
            {
                x.DefaultLeadOwnerUserId,
                x.DefaultLeadOwnerSetByUserId,
                x.DefaultLeadOwnerSetOn
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new RoutingNotFoundException($"Business unit {businessUnitId} was not found.");

        if (unit.DefaultLeadOwnerUserId is not long ownerId)
            return new DefaultLeadOwnerResponse(null, null, null, false, NoDefaultOwnerReason,
                unit.DefaultLeadOwnerSetByUserId, unit.DefaultLeadOwnerSetOn);

        var user = await _db.Users.AsNoTracking()
            .Where(x => x.Id == ownerId && x.Buid == businessUnitId)
            .Select(x => new { Name = (x.FirstName + " " + x.LastName).Trim(), x.Email })
            .SingleOrDefaultAsync(ct);
        // A saved id whose user has since left the tenant reads exactly as an ineligible one, in
        // the same sentence the routing engine would use, because the routing outcome is the same:
        // the inquiry goes to the queue.
        if (user is null)
            return new DefaultLeadOwnerResponse(ownerId, null, null, false,
                RoutingEligibilityReasons.UserInactive,
                unit.DefaultLeadOwnerSetByUserId, unit.DefaultLeadOwnerSetOn);

        var availability = (await LoadUserAvailabilityAsync(
            businessUnitId, [ownerId], DateTime.UtcNow, ct)).SingleOrDefault();
        return new DefaultLeadOwnerResponse(
            ownerId, user.Name, user.Email,
            availability?.IsAvailable ?? false,
            availability?.EligibilityReason ?? RoutingEligibilityReasons.UserInactive,
            unit.DefaultLeadOwnerSetByUserId, unit.DefaultLeadOwnerSetOn);
    }

    /// <summary>
    /// Sets or clears the tenant's fallback lead owner. Null clears it, which restores the
    /// behaviour of parking every unplaceable inquiry on the routing queue.
    ///
    /// <para>Accepts any ACTIVE user in the tenant, including one who is not currently routing
    /// eligible. Refusing an ineligible name would block a tenant from configuring routing in the
    /// order it actually configures it — the fallback owner is usually chosen before anybody has
    /// written a governed Sales Rep profile — and the setting is already fail-safe at routing time.
    /// The verdict is returned instead, so the screen can say what will happen rather than
    /// pretending the choice was impossible.</para>
    /// </summary>
    public async Task<DefaultLeadOwnerResponse> SetDefaultOwnerAsync(
        long businessUnitId, SetDefaultLeadOwnerCommand command, CancellationToken ct)
    {
        var unit = await _db.BusinessUnits.SingleOrDefaultAsync(x => x.Id == businessUnitId, ct)
            ?? throw new RoutingNotFoundException($"Business unit {businessUnitId} was not found.");

        if (command.DefaultOwnerUserId is long ownerId)
        {
            if (ownerId <= 0) throw new ArgumentException("Choose a person to receive unmatched inquiries.");
            var exists = await _db.Users.AnyAsync(
                x => x.Id == ownerId && x.Buid == businessUnitId && x.IsActive == true, ct);
            if (!exists)
                throw new RoutingConflictException(
                    "The person you chose is not an active user in this business unit.");
        }

        unit.DefaultLeadOwnerUserId = command.DefaultOwnerUserId;
        unit.DefaultLeadOwnerSetByUserId = command.SetByUserId;
        unit.DefaultLeadOwnerSetOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetDefaultOwnerAsync(businessUnitId, ct);
    }

    /// <summary>What the setup screen shows when no fallback owner has been chosen. Plain English,
    /// because it is read by a person deciding whether to choose one.</summary>
    private const string NoDefaultOwnerReason =
        "No fallback owner is set. Inquiries Nexora cannot match are held on the routing queue "
        + "until someone assigns them.";

    private async Task<RoutingDecisionResponse> AssignCoreAsync(
        long businessUnitId, Lead lead, ManualAssignLeadCommand command,
        UnassignedWorkItem? queueItem, string requestHash, CancellationToken ct)
    {
        if (command.EnforceExpectedAssignee && lead.AssignTo != command.ExpectedAssigneeId)
            throw new RoutingConflictException("Lead assignment changed since it was loaded. Refresh and retry.");
        if (command.ExpectedAssignmentVersion.HasValue
            && lead.AssignmentVersion != command.ExpectedAssignmentVersion.Value)
            throw new RoutingConflictException("Lead assignment changed since it was loaded. Refresh and retry.");

        // TAKING A LEAD OFF ITS CURRENT OWNER NEEDS A REASON.
        //
        // Every assignment already appends a LeadAssignment row with a Comment column, and the
        // routing queue already refuses a manager's override without one. The lead-detail screen
        // sends no comment at all, so every reassignment made from the screen reps actually use
        // stored Comment = null — the assignment history existed and was empty exactly where it
        // mattered, on the moves that took work away from a named person.
        //
        // The rule is deliberately narrow: it fires only when a DIFFERENT person already holds the
        // lead. Picking up an unowned inquiry, or re-confirming the owner it already has, demands
        // nothing, because there is no conflict to explain and friction there would only teach
        // people to type "x" five times.
        //
        // Guarded here rather than at the endpoints: this method is the single write path for
        // every manual assignment — lead detail, the unassigned-lead dialog, the routing queue and
        // bulk assign all funnel through it — so no caller can be added later that forgets.
        if (lead.AssignTo.HasValue && lead.AssignTo.Value != command.AssignedToUserId
            && (command.Comment?.Trim().Length ?? 0) < MinimumReassignmentReasonLength)
            throw new ArgumentException(
                "Moving this lead away from its current owner needs a reason of at least "
                + $"{MinimumReassignmentReasonLength} characters. Say why it is changing hands.");

        var assigneeExists = await _db.Users.AnyAsync(u =>
            u.Id == command.AssignedToUserId && u.Buid == businessUnitId && u.IsActive == true, ct);
        if (!assigneeExists) throw new RoutingConflictException("Assignee must be an active user in the same tenant.");
        var ownerOption = (await GetOwnerOptionsAsync(businessUnitId, ct))
            .SingleOrDefault(option => option.UserId == command.AssignedToUserId);
        if (ownerOption == null || !ownerOption.IsAvailable)
            throw new RoutingConflictException("Assignee is not currently eligible for governed routing.");

        var now = DateTime.UtcNow;
        // The header command fences on AssignmentVersion.  Legacy queue callers still fence on
        // ExpectedAssigneeId, so both paths remain backward compatible.
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
            Explanation = JsonSerializer.Serialize(new
            {
                source = "manual",
                scope = command.AssignmentScope.ToString(),
                requestHash
            }),
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
            AssignmentMethod = LeadAssignmentMethods.Manual,
            IsManualOverride = true,
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim()
        };
        lead.AssignTo = command.AssignedToUserId;
        lead.AssignOn = now;
        lead.AssignComment = command.Comment?.Trim();
        lead.AssignedByUserId = command.AssignedByUserId;
        lead.AssignmentMethod = LeadAssignmentMethods.Manual;
        lead.ManualAssignmentOverride = true;
        lead.AssignmentVersion++;
        lead.ModifiedDate = now;
        await ResolveActiveQueueItemsAsync(businessUnitId, lead.Id, now, "MANUALLY_ASSIGNED", queueItem, ct);
        _db.Add(decision);
        _db.Add(assignment);
        await _db.SaveChangesAsync(ct);
        return ToResponse(decision, assignment.Id, queueItem?.Id);
    }

    /// <summary>
    /// Identifier grades trusted to LINK a lead to a customer (Lead.CustomerId), not merely
    /// to route it. Exact, singular identifiers only. Deliberately excluded:
    ///   * CustomerName / Alias / HistoricalInference — name similarity routes well, but a
    ///     wrong client on a lead is worse than an unresolved one;
    ///   * Phone — switchboard numbers are shared across organisations;
    ///   * Portal / PortalAccount / RfqNumberPattern — learned, suggestion-grade evidence.
    /// </summary>
    private static readonly HashSet<CustomerIdentifierType> CustomerLinkGradeIdentifiers =
    [
        CustomerIdentifierType.ErpAccount,
        CustomerIdentifierType.TaxRegistration,
        CustomerIdentifierType.Email,
        CustomerIdentifierType.Domain
    ];

    /// <summary>
    /// Writes the routed customer through to <c>Lead.CustomerId</c>. RouteLeadAsync has
    /// been proving customer matches and persisting them onto
    /// <see cref="LeadRoutingDecision.CustomerId"/> while the Lead stayed unlinked — the
    /// matching engine "worked" while zero production leads carried a customer. This runs
    /// inside the routing transaction, i.e. exactly where routing already runs: after
    /// identity reconciliation and after ingestion-time client resolution, so the dedup
    /// corpus scoping that keys on <c>customer:{Id}</c> is never re-keyed retroactively.
    ///
    /// It is deliberately NARROWER than the routing match itself:
    ///   * only an unambiguous engine match at its highest authority
    ///     (<see cref="CustomerMatchStatus.Matched"/> — verified identifier, above
    ///     threshold, no ambiguity) qualifies;
    ///   * only when that match came from an exact-identifier-grade signal
    ///     (<see cref="CustomerLinkGradeIdentifiers"/>) — never fuzzy name similarity;
    ///   * a lead that already carries a customer (human or machine linked) is never
    ///     rewritten, and a human decision is never touched.
    /// The link is recorded through the Lead's governed mutator so the reason, confidence
    /// and explanation are stored with it (CustomerID⇔status invariant preserved).
    /// </summary>
    private static void WriteCustomerThroughToLead(
        Lead lead, LeadRoutingDecision decision, IReadOnlyList<CustomerIdentifier> identifiers)
    {
        if (lead.CustomerId.HasValue) return;                                    // never overwrite
        if (LeadCustomerMatchStatuses.IsHumanDecided(lead.CustomerMatchStatus)) return;
        if (decision.MatchStatus != CustomerMatchStatus.Matched) return;         // highest authority only
        if (decision.CustomerId is not > 0 || decision.MatchedIdentifierId is null) return;

        var matched = identifiers.FirstOrDefault(i => i.Id == decision.MatchedIdentifierId.Value);
        if (matched is null || !matched.IsVerified) return;
        if (matched.CustomerId != decision.CustomerId.Value) return;
        if (!CustomerLinkGradeIdentifiers.Contains(matched.IdentifierType)) return;

        var reasonCode = matched.IdentifierType switch
        {
            CustomerIdentifierType.ErpAccount => CustomerResolution.CustomerMatchReasonCodes.ErpAccountExact,
            CustomerIdentifierType.TaxRegistration => CustomerResolution.CustomerMatchReasonCodes.TaxRegExact,
            CustomerIdentifierType.Email => CustomerResolution.CustomerMatchReasonCodes.SenderEmailExact,
            _ => CustomerResolution.CustomerMatchReasonCodes.SenderDomain
        };
        lead.AutoResolveCommercialIdentity(
            decision.CustomerId.Value,
            contactId: null,
            reasonCode,
            decision.MatchConfidence,
            $"Routing matched verified {matched.IdentifierType} identifier "
            + $"'{matched.DisplayValue}' ({decision.DecisionCode}).",
            decision.CreatedOn);
        lead.ModifiedDate = decision.CreatedOn;
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
            lead.AssignedByUserId = null;
            lead.AssignmentMethod = LeadAssignmentMethods.Automatic;
            lead.ManualAssignmentOverride = false;
            lead.AssignmentVersion++;
            result.Assignment.AssignmentMethod = LeadAssignmentMethods.Automatic;
            result.Assignment.IsManualOverride = false;
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

    private async Task<List<RoutingUserAvailability>> LoadUserAvailabilityAsync(
        long businessUnitId, long[] userIds, DateTime measuredOn, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];

        var statusRows = await _db.SetupMasters.AsNoTracking()
            .Where(status => status.BusinessUnitId == businessUnitId && status.IsActive != false)
            .Where(status => status.SetupType.ToLower().Contains("status"))
            .Select(status => new RoutingStatusRow(
                status.SetupId, status.SetupType, status.SetupCode, status.SetupValue))
            .ToListAsync(ct);
        var inactiveLeadStatusIds = StatusIds(statusRows, "lead",
            "DISQUALIFIED", "CONVERTED_TO_RFQ", "QUOTED", "NEGOTIATION", "AWARDED",
            "PARTIALLY_AWARDED", "LOST", "CANCELLED", "COMPLETED", "DUPLICATED");
        var terminalRfqStatusIds = StatusIds(statusRows, "rfq",
            "AWARDED", "PARTIALLY_AWARDED", "LOST", "EXPIRED", "CANCELLED");
        var terminalQuoteStatusIds = StatusIds(statusRows, "quote",
            "WON", "ACCEPTED", "LOST", "REJECTED", "EXPIRED", "CANCELLED", "VOID", "VOIDED");

        var users = await _db.Users.AsNoTracking()
            .Where(user => user.Buid == businessUnitId && userIds.Contains(user.Id))
            .Select(user => new { user.Id, IsActive = user.IsActive == true })
            .ToListAsync(ct);
        var profiles = await _db.SalesRepProfiles.AsNoTracking()
            .Where(profile => profile.BusinessUnitId == businessUnitId && userIds.Contains(profile.UserId) &&
                profile.EffectiveFromUtc <= measuredOn &&
                (!profile.EffectiveToUtc.HasValue || profile.EffectiveToUtc > measuredOn))
            .ToDictionaryAsync(profile => profile.UserId, ct);
        var journeys = await _db.Leads.AsNoTracking()
            .Where(lead => lead.BusinessUnitId == businessUnitId &&
                lead.AssignTo.HasValue && userIds.Contains(lead.AssignTo.Value))
            .Select(lead => new
            {
                UserId = lead.AssignTo!.Value,
                IsActiveLead = !lead.LeadStatusId.HasValue || !inactiveLeadStatusIds.Contains(lead.LeadStatusId.Value),
                LeadLineCount = lead.LeadItems.Count,
                lead.BidClosingDate,
                OpenRfqCount = lead.Rfqs.Count(rfq =>
                    !rfq.RfqstatusId.HasValue || !terminalRfqStatusIds.Contains(rfq.RfqstatusId.Value)),
                OpenQuoteCount = lead.Rfqs.SelectMany(rfq => rfq.Quotes).Count(quote =>
                    !quote.OutcomeOn.HasValue &&
                    (!quote.StatusId.HasValue || !terminalQuoteStatusIds.Contains(quote.StatusId.Value))),
                FollowUpCount = lead.Rfqs.SelectMany(rfq => rfq.Quotes).Count(quote =>
                    quote.SentOn.HasValue && !quote.RespondedOn.HasValue && !quote.OutcomeOn.HasValue)
            })
            .ToListAsync(ct);

        return users.OrderBy(user => user.Id).Select(user =>
        {
            var assigned = journeys.Where(journey => journey.UserId == user.Id).ToArray();
            var active = assigned.Where(journey =>
                journey.IsActiveLead || journey.OpenRfqCount > 0 ||
                journey.OpenQuoteCount > 0 || journey.FollowUpCount > 0).ToArray();
            var deadlines = active.Where(journey => journey.BidClosingDate.HasValue)
                .Select(journey => journey.BidClosingDate!.Value).ToArray();
            var overdue = deadlines.Count(deadline => deadline < measuredOn);
            var urgent = deadlines.Count(deadline => deadline >= measuredOn && deadline <= measuredOn.AddHours(24));
            var approaching = deadlines.Count(deadline => deadline > measuredOn.AddHours(24) &&
                deadline <= measuredOn.AddDays(3));
            var activeLeadCount = active.Count(journey => journey.IsActiveLead);
            var lineCount = active.Sum(journey => journey.LeadLineCount);
            var linePoints = active.Sum(journey => Math.Min(
                _policy.MaximumLinePointsPerJourney,
                journey.LeadLineCount * _policy.LeadLineWeight));
            var openRfqs = active.Sum(journey => journey.OpenRfqCount);
            var openQuotes = active.Sum(journey => journey.OpenQuoteCount);
            var followUps = active.Sum(journey => journey.FollowUpCount);
            var points = activeLeadCount * _policy.ActiveLeadWeight + linePoints +
                overdue * _policy.OverdueDeadlineWeight + urgent * _policy.UrgentDeadlineWeight +
                approaching * _policy.ApproachingDeadlineWeight + openRfqs * _policy.OpenRfqWeight +
                openQuotes * _policy.OpenQuoteWeight + followUps * _policy.FollowUpWeight;
            var boundedPoints = Math.Max(0, points);
            var measuredCapacity = Math.Max(0, 100 - (int)Math.Ceiling(
                boundedPoints * 100m / _policy.MaximumWorkloadPoints));
            profiles.TryGetValue(user.Id, out var profile);
            var configuredCapacity = profile == null ? 100 : Math.Clamp(profile.CapacityPercent, 0, 100);
            var capacity = Math.Min(measuredCapacity, configuredCapacity);
            var profileEligible = profile?.IsRoutingEligible == true;
            var workload = new RoutingWorkloadSnapshot(
                activeLeadCount, lineCount, overdue, urgent, approaching,
                openRfqs, openQuotes, followUps, boundedPoints);
            return new RoutingUserAvailability(
                businessUnitId, user.Id, user.IsActive,
                user.IsActive && profileEligible && boundedPoints < _policy.MaximumWorkloadPoints && capacity > 0,
                user.IsActive ? capacity : 0,
                workload,
                profile != null,
                !user.IsActive ? RoutingEligibilityReasons.UserInactive
                    : profile == null ? RoutingEligibilityReasons.ProfileRequired
                    : !profileEligible ? RoutingEligibilityReasons.ProfileNotEligible
                    : capacity <= 0 ? RoutingEligibilityReasons.CapacityExhausted
                    : RoutingEligibilityReasons.Eligible);
        }).ToList();
    }

    private static long[] StatusIds(
        IEnumerable<RoutingStatusRow> rows, string typeFragment, params string[] terminalCodes)
    {
        var accepted = terminalCodes.ToHashSet(StringComparer.Ordinal);
        return rows.Where(row =>
                row.SetupType.Contains(typeFragment, StringComparison.OrdinalIgnoreCase))
            .Where(row => accepted.Contains(NormalizeStatusCode(row.SetupCode ?? row.SetupValue)))
            .Select(row => row.SetupId)
            .ToArray();
    }

    private static string NormalizeStatusCode(string? value) => string.Join('_',
        (value ?? string.Empty).Trim().ToUpperInvariant()
            .Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries));

    private sealed record RoutingStatusRow(
        long SetupId, string SetupType, string? SetupCode, string SetupValue);

    /// <summary>
    /// Derives a scope key for EVERY scope the routing policy ranks, each with the provenance of
    /// what it was read from.
    ///
    /// <para>This used to return Branch and ProductCategory only. Territory and KeyAccountTeam
    /// rules were therefore unmatchable by construction — the engine looks the scope up in this
    /// dictionary, found nothing, and skipped the rule — so half of FR-RFQ-07's "customer,
    /// product-category or region" was dead. Territory derives from the delivery location and the
    /// region masters; KeyAccountTeam now derives from <c>Customer.AccountTeamId</c> (FR-CST-02),
    /// which is the customers-to-teams edge that did not exist when this method was written, and
    /// reports <see cref="RoutingScopeKeys.KeyAccountTeamUnavailable"/> per RFQ when the lead names
    /// no customer or that customer is in no account team.</para>
    ///
    /// <para>Tenant isolation: every read below is filtered on <paramref name="businessUnitId"/>
    /// explicitly, on top of the context's query filters, so a territory can never be derived
    /// from another tenant's customer row or region master.</para>
    /// </summary>
    private async Task<IReadOnlyList<ScopeKeyDerivation>> BuildScopeKeyDerivationsAsync(
        long businessUnitId, Lead lead, CancellationToken ct)
    {
        var branch = await _db.BusinessUnits.Where(b => b.Id == businessUnitId)
            .Select(b => b.BusinessUnitCode).SingleAsync(ct);
        var category = lead.LeadItems.Select(i => i.CommodityProduct).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        // The customer RECORDED on the lead — never one of the routing candidates, which are
        // still unproven at this point. Reading a candidate's address would let a guess the
        // engine has not yet accepted (and may reject as ambiguous) choose the territory.
        var customerRow = lead.CustomerId is long customerId
            ? await _db.Customers.AsNoTracking()
                .Where(c => c.Id == customerId && c.Buid == businessUnitId)
                .Select(c => new
                {
                    Region = new CustomerRegionEvidence(
                        c.ShippingState, c.ShippingCity, c.BillingState, c.BillingCity),
                    // FR-CST-02's account team, read under the same tenant predicate as the region.
                    // The join is restated on BusinessUnitId rather than trusted to the foreign key:
                    // Customer.AccountTeamId is a single-column key and cannot itself express that
                    // the team belongs to this tenant.
                    AccountTeamName = _db.Teams
                        .Where(t => t.Id == c.AccountTeamId && t.BusinessUnitId == businessUnitId)
                        .Select(t => t.TeamName)
                        .FirstOrDefault()
                })
                .SingleOrDefaultAsync(ct)
            : null;
        var customer = customerRow?.Region;

        var states = await _db.SetStates.AsNoTracking()
            .Where(s => s.Buid == businessUnitId && s.IsActive)
            .Select(s => new { s.StateId, s.StateCode, s.StateName })
            .ToListAsync(ct);
        var cities = await _db.SetCities.AsNoTracking()
            .Where(c => c.Buid == businessUnitId && c.IsActive)
            .Select(c => new { c.CityName, c.StateId })
            .ToListAsync(ct);

        // A tenant's region vocabulary, flattened to "this wording means this region": a state
        // answers to its code and to its name, and a city answers for the state it sits in.
        var regionByStateId = states
            .GroupBy(s => s.StateId)
            .ToDictionary(group => group.Key, group => group.First().StateName);
        var stateAliases = states
            .SelectMany(s => new[]
            {
                new TerritoryRegionAlias(s.StateCode, s.StateName),
                new TerritoryRegionAlias(s.StateName, s.StateName)
            })
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Alias) && !string.IsNullOrWhiteSpace(alias.RegionName))
            .ToList();
        var cityAliases = cities
            .Where(c => regionByStateId.ContainsKey(c.StateId))
            .Select(c => new TerritoryRegionAlias(c.CityName, regionByStateId[c.StateId]))
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Alias) && !string.IsNullOrWhiteSpace(alias.RegionName))
            .ToList();

        return
        [
            RoutingScopeKeys.Branch(branch),
            RoutingScopeKeys.ProductCategory(category),
            RoutingScopeKeys.Territory(lead.DeliveryLocation, customer, stateAliases, cityAliases),
            RoutingScopeKeys.KeyAccountTeam(customerRow?.AccountTeamName)
        ];
    }

    /// <summary>
    /// What this lead says about who sent it, as routing lookup keys.
    ///
    /// <para>THE SENDER IS GUARDED EXACTLY AS THE RESOLVER GUARDS IT. This method used to add
    /// the sender's domain with no check at all, and that quietly undid the relay fix. The
    /// resolver refuses bidnet.com as a Domain because every buyer's RFQ arrives from it, but a
    /// Domain row bidnet.com → Saudi Aramco that was learned before that fix is still in the
    /// store. Routing looked it up, the engine called it a verified 0.95 match, and
    /// <see cref="WriteCustomerThroughToLead"/> linked the lead to Aramco. The extraction worker
    /// routes straight after resolution, so a lead the resolver had rightly left without a
    /// customer got one anyway, and Aramco's owner got another buyer's inquiry.</para>
    ///
    /// <para>The rules, which are the resolver's rules:</para>
    /// <list type="bullet">
    /// <item>An address on Nexora's own placeholder domains (extraction@pipeline.local), or on
    /// the tenant's own domains, says nothing about a customer. It is dropped as an Email and as a
    /// Domain. A salesman forwarding a bid from ahmed@alquraishi.com.sa is us, not SEC.</item>
    /// <item>A free-mail or portal-relay address is still an exact Email. One gmail mailbox is
    /// one person, and an administrator can register it on a customer. Its DOMAIN is dropped,
    /// because gmail.com and bidnet.com are shared by everybody.</item>
    /// </list>
    /// </summary>
    private static Dictionary<CustomerIdentifierType, HashSet<string>> BuildEvidence(
        Lead lead, IReadOnlySet<string> selfDomains, IReadOnlyCollection<string?> selfNames,
        int minimumErpAccountLength)
    {
        var evidence = new Dictionary<CustomerIdentifierType, HashSet<string>>();
        if (IsSenderAddressEvidence(lead.Clientemail, selfDomains, selfNames))
        {
            Add(CustomerIdentifierType.Email, lead.Clientemail);
            var senderDomain = RoutingValueNormalizer.DomainFromEmail(lead.Clientemail);
            if (IsSenderDomainEvidence(senderDomain, selfDomains, selfNames))
                Add(CustomerIdentifierType.Domain, senderDomain);
        }
        Add(CustomerIdentifierType.CustomerName, lead.BuyersName);
        // AN ACCOUNT NUMBER NAMES A CUSTOMER ONLY BY THE RESOLVER'S RULE. ERP account is routing's
        // strongest identifier, and every line's CompanyRef was read as one. On an SAP print that
        // field is the company code, 1000 or SA01, shared by every affiliate of a group, and the
        // migration backfill stored every customer's DocId as an ERP account: a customer numbered
        // 1000 outranked SEC's own registered address on SEC's own print, took the lead's owner and,
        // where resolution had not linked it, was written onto the lead. The resolver has refused
        // such a code since it was found, and refuses OUR vendor code at the customer too (2004414
        // is us at SEC, never SEC). Routing refused neither, so it undid both one step later.
        var ourVendorCode = string.IsNullOrWhiteSpace(lead.SupplierAccountRefOnDocument)
            ? null
            : RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, lead.SupplierAccountRefOnDocument);
        foreach (var item in lead.LeadItems)
        {
            AddAccount(item.CustomerAccountPortalId);
            AddAccount(item.CompanyRef);
        }
        return evidence;

        void AddAccount(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var normalized = RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, value);
            if (normalized.Length < minimumErpAccountLength) return;
            if (ourVendorCode is not null && string.Equals(normalized, ourVendorCode, StringComparison.Ordinal)) return;
            Add(CustomerIdentifierType.ErpAccount, value);
        }

        void Add(CustomerIdentifierType type, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var normalized = RoutingValueNormalizer.Normalize(type, value);
            if (!evidence.TryGetValue(type, out var values)) evidence[type] = values = [];
            values.Add(normalized);
        }
    }

    /// <summary>
    /// True when a sender address may be looked up at all: it has a domain, the domain is not one
    /// of Nexora's placeholders, and it is not ours: one of the tenant's domains, a host under one,
    /// or a domain whose name spells the tenant's (<see cref="CustomerResolution.TenantSelfIdentity.IsOurs(string?, IEnumerable{string}?, IEnumerable{string?}?)"/>).
    /// </summary>
    private static bool IsSenderAddressEvidence(
        string? address, IReadOnlySet<string> selfDomains, IReadOnlyCollection<string?> selfNames) =>
        !string.IsNullOrWhiteSpace(RoutingValueNormalizer.DomainFromEmail(address))
        // DomainOf, not DomainFromEmail: it also reads "Extraction <extraction@pipeline.local>",
        // where DomainFromEmail keeps the closing bracket and the placeholder check says no.
        && !CustomerResolution.SyntheticIdentityGuard.IsSyntheticDomain(
            CustomerResolution.IdentityDomainGuard.DomainOf(address))
        && !CustomerResolution.TenantSelfIdentity.IsOurs(address, selfDomains, selfNames);

    /// <summary>
    /// True when a sender domain can belong to one organisation that is not us: the Foundations
    /// guard and the shared "ours" test, so routing and the resolver cannot disagree about which
    /// domains name a customer.
    /// </summary>
    private static bool IsSenderDomainEvidence(
        string? domain, IReadOnlySet<string> selfDomains, IReadOnlyCollection<string?> selfNames) =>
        CustomerResolution.IdentityDomainGuard.IsOrganisationDomain(domain, selfDomains)
        && !CustomerResolution.TenantSelfIdentity.IsOurs(domain, selfDomains, selfNames);

    /// <summary>
    /// The live identifier rows the evidence points at.
    ///
    /// <para>The sender guard is applied again here rather than trusted to the caller. This is the
    /// query that turns a value into a customer, so it is the one place a value that names nobody
    /// must not get through, whoever built the evidence.</para>
    ///
    /// <para>A row a reviewer confirmed but the learner would not trust
    /// (<see cref="CustomerResolution.CustomerIdentifierSources.LeadReviewUnverified"/>) is never
    /// loaded. It exists so a person can look at it. The engine drops unverified rows, but it reads
    /// only the IsVerified flag, and the flag and the source are separate columns that do drift
    /// apart: when a later confirmation re-proposes the same value, the learner's reinforcement
    /// path sets IsVerified to true and leaves Source alone. Such a row would otherwise route the
    /// lead to that customer's owner and write the customer through at identifier grade.</para>
    /// </summary>
    /// <param name="senderIsSystemMailbox">
    /// The lead's sender is a mailbox no person reads (<see cref="CustomerResolution.IdentityDomainGuard.IsSystemMailbox"/>).
    /// Its domain then matches only a Domain row a person entered, exactly as in the resolver's domain tier: a learned
    /// etimad.gov.sa or coupa.com row on Saudi Aramco otherwise routed every SEC tender the portal carried to Aramco's owner.
    /// </param>
    private async Task<List<CustomerIdentifier>> LoadMatchingIdentifiersAsync(
        long businessUnitId, Dictionary<CustomerIdentifierType, HashSet<string>> evidence,
        IReadOnlySet<string> selfDomains, IReadOnlyCollection<string?> selfNames, bool senderIsSystemMailbox, CancellationToken ct)
    {
        var emails = Values(CustomerIdentifierType.Email)
            .Where(email => IsSenderAddressEvidence(email, selfDomains, selfNames)).ToArray();
        var domains = Values(CustomerIdentifierType.Domain)
            .Where(domain => IsSenderDomainEvidence(domain, selfDomains, selfNames)).ToArray();
        // The length rule again, for the same reason the sender guards are re-applied above.
        var accounts = Values(CustomerIdentifierType.ErpAccount)
            .Where(account => account.Length >= _identityPolicy.MinimumErpAccountLength).ToArray();
        var names = Values(CustomerIdentifierType.CustomerName);
        var unverifiedSource = CustomerResolution.CustomerIdentifierSources.LeadReviewUnverified;
        var rows = await _db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == businessUnitId && i.EffectiveTo == null &&
                i.Source != unverifiedSource &&
                _db.Customers.Any(c => c.Buid == businessUnitId && c.Id == i.CustomerId && c.IsActive != false) &&
                ((i.IdentifierType == CustomerIdentifierType.Email && emails.Contains(i.NormalizedValue)) ||
                 (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue)) ||
                 (i.IdentifierType == CustomerIdentifierType.ErpAccount && accounts.Contains(i.NormalizedValue)) ||
                 ((i.IdentifierType == CustomerIdentifierType.CustomerName || i.IdentifierType == CustomerIdentifierType.Alias) && names.Contains(i.NormalizedValue))))
            .ToListAsync(ct);

        // A RELAY'S OWN SENDING ADDRESS NAMES A BUYER ONLY WHERE A PERSON SAID SO. A relay address is
        // still let through as exact evidence above, because one mailbox a person registered is one
        // buyer. But the old learner minted ordersender-prod@ansmtp.ariba.com as Saudi Aramco's Email
        // from one confirmation, and with that row in the store every Ariba buyer's RFQ went to Aramco's
        // owner and had Aramco written onto the lead. The resolver's exact tier asks the same predicate.
        return rows
            .Where(i => i.IdentifierType != CustomerIdentifierType.Email
                        || CustomerResolution.IdentityDomainGuard.MayMatchExactAddress(i.NormalizedValue, i.Source))
            // A system mailbox's domain names a buyer only where a person entered the Domain row (the resolver's S2).
            .Where(i => i.IdentifierType != CustomerIdentifierType.Domain
                        || !senderIsSystemMailbox
                        || CustomerResolution.CustomerIdentifierSources.EnteredByAPerson.Contains(i.Source, StringComparer.Ordinal))
            .ToList();

        string[] Values(CustomerIdentifierType type) => evidence.TryGetValue(type, out var values) ? values.ToArray() : [];
    }

    private async Task<RoutingDecisionResponse?> FindDecisionByKeyAsync(
        long businessUnitId, string key, string expectedRequestHash, CancellationToken ct)
    {
        var decision = await _db.Set<LeadRoutingDecision>().AsNoTracking().SingleOrDefaultAsync(
            d => d.BusinessUnitId == businessUnitId && d.IdempotencyKey == key.Trim(), ct);
        if (decision == null) return null;
        var storedRequestHash = RoutingRequestFingerprint.ReadFromExplanation(decision.Explanation);
        if (!string.Equals(storedRequestHash, expectedRequestHash, StringComparison.Ordinal))
            throw new RoutingConflictException(
                "The idempotency key was already used for different routing request content.");
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

    private static LeadOwnershipResponse Ownership(Lead lead, RoutingDecisionResponse decision) => new(
        lead.Id, lead.AssignTo, lead.AssignmentMethod, lead.ManualAssignmentOverride,
        lead.AssignmentVersion, lead.AssignOn, decision);

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct,
        IsolationLevel isolationLevel = IsolationLevel.Serializable)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(isolationLevel, ct);
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

    private static bool IsQueueAssignmentConflict(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
            return true;

        if (exception is PostgresException postgresException)
        {
            return postgresException.SqlState is
                PostgresErrorCodes.SerializationFailure or
                PostgresErrorCodes.DeadlockDetected or
                PostgresErrorCodes.UniqueViolation;
        }

        return exception.InnerException != null && IsQueueAssignmentConflict(exception.InnerException);
    }

    /// <summary>
    /// The shortest reassignment reason that is worth storing. Matches the routing queue's existing
    /// override rule so the two surfaces cannot disagree about what counts as an explanation.
    /// </summary>
    public const int MinimumReassignmentReasonLength = 5;

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
