using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-intelligence")]
public sealed class CommercialIntelligenceController(
    ErpRfqAutomationContext db,
    ISalesApplicationService sales,
    ICommercialRoutingApplicationService routing,
    IRoleGate roleGate,
    IAccountTeamScopeResolver? accountScope = null) : ControllerBase
{
    [HttpGet("sales-today")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> SalesToday(CancellationToken ct)
    {
        var tenant = TenantId();
        var userId = UserId();
        var scope = await ResolveAccountScopeAsync(tenant, ct);
        if (!userId.HasValue || scope is null) return Forbid();
        var now = DateTime.UtcNow;
        var followUpQuery = db.FollowUpTasks.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.Status != FollowUpStatus.Completed && x.Status != FollowUpStatus.Cancelled);
        if (!scope.IsTenantWide)
            followUpQuery = followUpQuery.Where(x => scope.UserIds.Contains(x.AssignedToUserId));
        var followUps = await followUpQuery
            .OrderBy(x => x.DueAtUtc).Take(100).ToListAsync(ct);
        var unassigned = scope.Tier is AccountScopeTier.ManagedScope or AccountScopeTier.Tenant
            ? await db.Set<UnassignedWorkItem>().AsNoTracking()
                .Where(x => x.BusinessUnitId == tenant && x.Status == WorkItemStatus.Open).CountAsync(ct)
            : 0;
        var overdue = followUps.Count(x => x.DueAtUtc < now);
        var quoteIds = followUps.Where(x => x.AggregateType == "Quote").Select(x => x.AggregateId).Distinct().ToArray();
        var leadIds = followUps.Where(x => x.AggregateType == "Lead").Select(x => x.AggregateId).Distinct().ToArray();
        var quotes = await db.Quotes.AsNoTracking().Include(x => x.Customer).Include(x => x.Rfq).ThenInclude(x => x.Lead)
            .Where(x => x.BusinessUnitId == tenant && quoteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var leads = await db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && leadIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var leadCustomerIds = leads.Values.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value)
            .Distinct().ToArray();
        var leadCustomers = await db.Customers.AsNoTracking()
            .Where(x => x.Buid == tenant && leadCustomerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant).ToDictionaryAsync(x => x.Id, ct);
        var items = followUps.Select(x => new
        {
            quote = x.AggregateType == "Quote" && quotes.TryGetValue(x.AggregateId, out var value) ? value : null,
            lead = x.AggregateType == "Lead" && leads.TryGetValue(x.AggregateId, out var lead) ? lead : null,
            task = x
        }).Select(x => new {
            id = x.task.Id, recordType = x.task.AggregateType, recordId = x.task.AggregateId,
            nexoraSerial = x.quote?.NexoraSerial ?? x.lead?.CommercialCaseReference,
            reference = x.quote?.QuoteNo ?? x.lead?.Rfqno ?? $"{x.task.AggregateType} {x.task.AggregateId}",
            customerName = x.quote?.Customer?.Name ??
                (x.lead?.CustomerId is long customerId && leadCustomers.TryGetValue(customerId, out var customer)
                    ? customer.Name : null),
            ownerName = users.TryGetValue(x.task.AssignedToUserId, out var owner) ? Name(owner) : null,
            reason = x.task.PurposeCode, dueAt = (DateTime?)x.task.DueAtUtc,
            priority = x.task.DueAtUtc < now ? "Critical" : "Due"
        }).ToArray();
        return Ok(new { generatedAt = now, scope = ScopeWireName(scope), metrics = new[] {
            Metric("open-follow-ups", "Open follow-ups", followUps.Count),
            Metric("overdue-follow-ups", "Overdue follow-ups", overdue),
            Metric("unassigned-leads", "Unassigned leads", unassigned)
        }, attentionItems = items });
    }

    [HttpGet("team-overview")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> TeamOverview(CancellationToken ct)
    {
        var tenant = TenantId();
        var roleId = ClaimId("roleId");
        if (roleId <= 0 || !await roleGate.IsManagerOrAdminAsync(roleId, tenant))
            return Forbid();

        var scope = await ResolveAccountScopeAsync(tenant, ct);
        if (scope is null) return Forbid();
        var reps = await BuildRepSummaries(tenant, ct, scope);
        return Ok(new { generatedAt = DateTime.UtcNow, metrics = new[] {
            Metric("active-reps", "Active representatives", reps.Count),
            Metric("active-leads", "Active assigned leads", reps.Sum(x => x.ActiveLeads)),
            Metric("follow-ups-due", "Follow-ups due", reps.Sum(x => x.FollowUpsDue))
        }, representatives = reps });
    }

    [HttpGet("reps")]
    [RequireManagerRole]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult> Reps(CancellationToken ct)
    {
        var tenant = TenantId();
        var scope = await ResolveAccountScopeAsync(tenant, ct);
        return scope is null ? Forbid() : Ok(await BuildRepSummaries(tenant, ct, scope));
    }

    /// <summary>
    /// The read side of <c>POST reps/{userId}/routing-profile</c>.
    ///
    /// <para><b>Why this endpoint exists.</b> The write endpoint below demands an
    /// <c>ExpectedVersion</c> for optimistic concurrency, and nothing in the product could tell a
    /// caller what that version was: <c>reps</c> and <c>reps/{userId}</c> both return commercial
    /// performance and say nothing about routing. So the profile that decides whether the routing
    /// engine may assign anyone at all could be written but never read, which is why no
    /// maintenance screen existed for it.</para>
    ///
    /// <para>Every active user is listed, including those with no profile row. That is the whole
    /// point of the screen: <c>sales_rep_profiles</c> is fail-closed, so a tenant with an empty
    /// table has nobody the engine will accept, and the only way out is to see the people who are
    /// missing a profile and give them one.</para>
    ///
    /// <para>The stored row and the engine's live verdict are reported separately and both are
    /// needed. The stored row is what the editor writes back (including <c>version</c>); the
    /// verdict is what routing will actually do today, which can differ because a row outside its
    /// effective dates is invisible to the engine.</para>
    /// </summary>
    [HttpGet("reps/routing-profiles")]
    [RequireManagerRole]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult> RepRoutingProfiles(CancellationToken ct)
    {
        var tenant = TenantId();
        var now = DateTime.UtcNow;
        var users = await db.Users.AsNoTracking().Include(x => x.Role)
            .Where(x => x.Buid == tenant && x.IsActive != false)
            .OrderBy(x => x.FirstName).ThenBy(x => x.LastName).ToListAsync(ct);
        var profiles = await db.SalesRepProfiles.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant).ToDictionaryAsync(x => x.UserId, ct);
        var options = (await routing.GetOwnerOptionsAsync(tenant, ct)).ToDictionary(x => x.UserId);
        return Ok(users.Select(user =>
        {
            profiles.TryGetValue(user.Id, out var profile);
            options.TryGetValue(user.Id, out var option);
            var effectiveNow = profile != null && profile.EffectiveFromUtc <= now &&
                (!profile.EffectiveToUtc.HasValue || profile.EffectiveToUtc > now);
            return new
            {
                userId = user.Id, name = Name(user), email = user.Email, roleName = user.Role?.SetupValue,
                hasProfile = profile != null,
                profileEffectiveNow = effectiveNow,
                isRoutingEligible = profile?.IsRoutingEligible,
                capacityPercent = profile?.CapacityPercent,
                distributionWeight = profile?.DistributionWeight,
                territoryKeys = profile?.TerritoryKeys ?? Array.Empty<string>(),
                productCategoryKeys = profile?.ProductCategoryKeys ?? Array.Empty<string>(),
                effectiveFromUtc = profile?.EffectiveFromUtc,
                effectiveToUtc = profile?.EffectiveToUtc,
                // 0 is the create sentinel the write endpoint expects, so a user with no row can
                // be POSTed straight back without the client inventing a version.
                version = profile?.Version ?? 0,
                updatedAtUtc = profile?.UpdatedAtUtc, updatedBy = profile?.UpdatedBy,
                isAvailable = option?.IsAvailable ?? false,
                eligibilityReason = option?.EligibilityReason
                    ?? (profile == null
                        ? RoutingEligibilityReasons.ProfileRequired
                        : RoutingEligibilityReasons.ProfileNotEffective),
                measuredCapacityPercent = option?.CapacityPercent,
                workloadPoints = option?.Workload.WorkloadPoints,
                policyVersion = option?.PolicyVersion,
                measuredAtUtc = option?.MeasuredAtUtc
            };
        }));
    }

    /// <summary>
    /// Creates or updates a sales rep's routing profile.
    ///
    /// <para><b>Why this endpoint exists.</b> <c>SalesApplicationService.UpsertProfileAsync</c>
    /// was fully implemented — validation, optimistic concurrency, idempotent replay — and
    /// reachable from nothing. <c>sales_rep_profiles</c> therefore had zero rows, the routing
    /// engine's eligibility gate could never be satisfied, and 44 of 44 production leads routed
    /// to <c>NO_MATCH_EVIDENCE</c>/Unassigned. This is the missing write path, not a new
    /// capability: no engine, no rules table, no admin module.</para>
    ///
    /// <para>Placed on the controller that already owns <c>reps</c> and already injects the sales
    /// service, under the same <c>Users</c> permission plus manager role that rep administration
    /// already uses.</para>
    /// </summary>
    [HttpPost("reps/{userId:long}/routing-profile")]
    [RequireManagerRole]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public async Task<ActionResult> UpsertRepRoutingProfile(
        long userId, [FromBody] UpsertRepRoutingProfileRequest request, CancellationToken ct)
    {
        if (request is null) return BadRequest(new { error = "A routing profile body is required." });
        var tenant = TenantId();

        // The rep must be a real user in THIS tenant. Without this a manager could mint a
        // routing profile for any user id and have leads assigned to it.
        var exists = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.Buid == tenant, ct);
        if (!exists) return NotFound(new { error = $"User {userId} was not found in this business unit." });

        try
        {
            var profile = await sales.UpsertProfileAsync(tenant, new UpsertSalesRepProfileCommand(
                UserId: userId,
                IsRoutingEligible: request.IsRoutingEligible,
                CapacityPercent: request.CapacityPercent,
                DistributionWeight: request.DistributionWeight,
                TerritoryKeys: request.TerritoryKeys ?? Array.Empty<string>(),
                ProductCategoryKeys: request.ProductCategoryKeys ?? Array.Empty<string>(),
                // Floored to midnight UTC, not DateTime.UtcNow.
                //
                // Two reasons, found by driving this endpoint against a real backend. (1) An
                // effective-dated ownership record starts on a DAY — "eligible from 6 August",
                // not "from 14:12:55.441094". (2) A sub-second default defeats the idempotency
                // key: a genuine retry sends the same key with a different timestamp, the service
                // sees different content and answers 409, so no caller can safely retry.
                EffectiveFromUtc: request.EffectiveFromUtc ?? DateTime.UtcNow.Date,
                EffectiveToUtc: request.EffectiveToUtc,
                ExpectedVersion: request.ExpectedVersion,
                ActorId: User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity?.Name ?? "System",
                IdempotencyKey: IdempotencyKey()), ct);
            return Ok(profile);
        }
        catch (SalesNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (SalesConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (SalesValidationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("reps/{userId:long}")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult> Rep(long userId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var tenant = TenantId();
        var scope = await ResolveAccountScopeAsync(tenant, ct);
        if (scope is null || (!scope.IsTenantWide && !scope.UserIds.Contains(userId))) return Forbid();
        var summary = (await BuildRepSummaries(tenant, ct, scope)).SingleOrDefault(x => x.UserId == userId);
        if (summary == null) return NotFound();
        var accountCount = await db.Set<CustomerOwnership>().CountAsync(x => x.BusinessUnitId == tenant && x.PrimaryUserId == userId && x.IsActive, ct);
        var now = DateTime.UtcNow;
        var fromUtc = NormalizeUtc(from ?? now.AddDays(-90));
        var toUtc = NormalizeUtc(to ?? now.AddSeconds(1));
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(366))
            return BadRequest(new { error = "The performance period must be between 1 and 366 days." });
        var asOf = now < toUtc ? now : toUtc.AddTicks(-1);
        var performance = (await sales.GetPerformanceAsync(tenant,
            new SalesPerformanceQuery(userId, fromUtc, toUtc, asOf), ct)).SingleOrDefault();
        var activities = await db.CommercialActivities.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.SalesRepUserId == userId)
            .Where(x => x.OccurredAtUtc >= fromUtc && x.OccurredAtUtc < toUtc)
            .OrderByDescending(x => x.OccurredAtUtc).Take(20).ToListAsync(ct);
        var leadIds = activities.Where(x => x.AggregateType.Equals("Lead", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.AggregateId).Distinct().ToArray();
        var rfqIds = activities.Where(x => x.AggregateType.Equals("RFQ", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.AggregateId).Distinct().ToArray();
        var quoteIds = activities.Where(x => x.AggregateType.Equals("Quote", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.AggregateId).Distinct().ToArray();
        var orderIds = activities.Where(x => x.AggregateType.Equals("Order", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.AggregateId).Distinct().ToArray();
        var leads = await db.Leads.AsNoTracking().Where(x => x.BusinessUnitId == tenant && leadIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var rfqs = await db.Rfqs.AsNoTracking().Where(x => x.BusinessUnitId == tenant && rfqIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var quotes = await db.Quotes.AsNoTracking().Where(x => x.BusinessUnitId == tenant && quoteIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var orders = await db.Orders.AsNoTracking().Where(x => x.BusinessUnitId == tenant && orderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var customerIds = activities.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value)
            .Concat(leads.Values.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value))
            .Concat(rfqs.Values.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value))
            .Concat(quotes.Values.Where(x => x.CustomerId.HasValue).Select(x => x.CustomerId!.Value))
            .Concat(orders.Values.Select(x => x.CustomerId)).Distinct().ToArray();
        var customers = await db.Customers.AsNoTracking().Where(x => x.Buid == tenant && customerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var activity = activities.Select(item =>
        {
            var evidence = ResolveActivityEvidence(item, leads, rfqs, quotes, orders);
            var customerId = item.CustomerId ?? evidence.CustomerId;
            return new
            {
                id = item.Id, recordType = item.AggregateType, recordId = item.AggregateId,
                nexoraSerial = evidence.NexoraSerial,
                reference = evidence.Reference ?? $"{item.AggregateType} {item.AggregateId}",
                customerName = customerId.HasValue && customers.TryGetValue(customerId.Value, out var name)
                    ? name : evidence.CustomerName,
                reason = item.ActivityType.ToString(), dueAt = (DateTime?)item.OccurredAtUtc,
                priority = "Recorded", evidence.ActionRoute, evidence.RequiredModule
            };
        }).ToArray();
        return Ok(new {
            summary.UserId, summary.Name, summary.Email, summary.RoleName, summary.ActiveLeads,
            summary.OverdueLeads, summary.OpenRfqs, summary.DraftQuotes, summary.FollowUpsDue,
            summary.PipelineGroups, accountCount,
            wonValueGroups = performance?.RevenueByCurrency.Select(x => new CurrencyAmountGroup(x.CurrencyCode, x.WeightedRevenueAmount)).ToArray()
                ?? Array.Empty<CurrencyAmountGroup>(),
            decidedQuotes = (performance?.WonCount ?? 0) + (performance?.LostCount ?? 0),
            conversionEligible = (performance?.WonCount ?? 0) + (performance?.LostCount ?? 0) >= MinimumConversionSample,
            conversionRate = (performance?.WonCount ?? 0) + (performance?.LostCount ?? 0) >= MinimumConversionSample
                ? performance?.WinRatePercent : null,
            performanceFrom = fromUtc, performanceTo = toUtc, recentActivity = activity
        });
    }

    [HttpGet("account-ownership")]
    [RequireModulePermission("Customers", PermissionAction.View)]
    public async Task<ActionResult> AccountOwnership(
        [FromQuery] string? search,
        CancellationToken ct,
        [FromQuery] long? customerId = null)
    {
        var tenant = TenantId();
        var roleId = ClaimId("roleId");
        var userId = UserId();
        if (accountScope != null && (!userId.HasValue || roleId <= 0)) return Forbid();
        var scope = accountScope == null
            ? AccountTeamScope.TenantWide(userId ?? 1)
            : await accountScope.ResolveAsync(userId!.Value, roleId, tenant, DateTime.UtcNow, ct);
        var customerQuery = db.Customers.AsNoTracking().Where(x => x.Buid == tenant);
        if (!scope.IsTenantWide)
            customerQuery = customerQuery.InAccountScope(db, tenant, scope, DateTime.UtcNow);
        if (customerId.HasValue)
            customerQuery = customerQuery.Where(x => x.Id == customerId.Value);
        else if (!string.IsNullOrWhiteSpace(search))
            customerQuery = customerQuery.Where(x => EF.Functions.ILike(x.Name, $"%{search.Trim()}%"));
        var customers = await customerQuery.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Take(customerId.HasValue ? 1 : 250).ToListAsync(ct);
        var ids = customers.Select(x => x.Id).ToArray();
        var ownerships = await db.Set<CustomerOwnership>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && ids.Contains(x.CustomerId) && x.IsActive && x.EffectiveTo == null)
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.EffectiveFrom).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant).ToDictionaryAsync(x => x.Id, ct);
        var leadRows = await db.Leads.AsNoTracking().Include(x => x.LeadStatus)
            .Where(x => x.BusinessUnitId == tenant)
            .InCommercialScope(db, tenant, scope, DateTime.UtcNow)
            .Where(x => x.CustomerId.HasValue && ids.Contains(x.CustomerId.Value)).ToListAsync(ct);
        var quoteRows = await db.Quotes.AsNoTracking().Include(x => x.Status).Include(x => x.Currency)
            .Where(x => x.BusinessUnitId == tenant)
            .InCommercialScope(db, tenant, scope, DateTime.UtcNow)
            .Where(x => x.CustomerId.HasValue && ids.Contains(x.CustomerId.Value)).ToListAsync(ct);
        return Ok(customers.Select(customer => {
            var owner = ownerships.FirstOrDefault(x => x.CustomerId == customer.Id);
            users.TryGetValue(owner?.PrimaryUserId ?? 0, out var user);
            var customerLeads = leadRows.Where(x => x.CustomerId == customer.Id && !TerminalLead(x.LeadStatus?.SetupCode, x.LeadStatus?.SetupValue)).ToArray();
            var customerQuotes = quoteRows.Where(x => x.CustomerId == customer.Id && !TerminalQuote(x.Status?.SetupCode, x.Status?.SetupValue)).ToArray();
            return new { customerId = customer.Id, customerName = customer.Name,
                ownerUserId = owner?.PrimaryUserId, ownerName = user == null ? null : Name(user),
                openLeads = customerLeads.Length, openQuotes = customerQuotes.Length,
                pipelineGroups = PipelineGroups(customerQuotes),
                lastActivityAt = customer.ModifiedOn ?? customer.CreatedOn, version = owner?.Version ?? 0 };
        }));
    }

    [HttpPost("account-ownership/{customerId:long}/assign")]
    [RequireManagerRole]
    [RequireModulePermission("Customers", PermissionAction.Edit)]
    public async Task<ActionResult> AssignAccount(long customerId, AssignAccountRequest request, CancellationToken ct)
    {
        var tenant = TenantId();
        var idempotencyKey = IdempotencyKey();
        var reason = request.Reason?.Trim();
        var strategy = db.Database.CreateExecutionStrategy();
        (CustomerOwnership? Created, bool Conflict) outcome;
        try
        {
            outcome = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({$"account-owner:{tenant}:{customerId}"}, 0))", ct);
                var replay = await db.Set<CustomerOwnership>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == tenant && x.MutationIdempotencyKey == idempotencyKey, ct);
                if (replay is not null)
                {
                    if (replay.CustomerId != customerId || replay.PrimaryUserId != request.OwnerUserId
                        || replay.Scope != OwnershipScope.GeneralCustomer || replay.ScopeKey != null
                        || !string.Equals(replay.Reason, reason ?? "Initial account owner assigned from sales management", StringComparison.Ordinal))
                        throw new InvalidOperationException("The idempotency key was already used for a different ownership request.");
                    await transaction.CommitAsync(ct);
                    return (Created: replay, Conflict: false);
                }
                if (!await db.Customers.AnyAsync(x => x.Buid == tenant && x.Id == customerId, ct) ||
                    !await db.Users.AnyAsync(x => x.Buid == tenant && x.Id == request.OwnerUserId && x.IsActive != false, ct))
                    throw new RoutingNotFoundException("The customer or owner was not found in this tenant.");
                var ownerOption = (await routing.GetOwnerOptionsAsync(tenant, ct))
                    .SingleOrDefault(option => option.UserId == request.OwnerUserId);
                if (ownerOption == null || !ownerOption.IsAvailable)
                    throw new RoutingConflictException("The selected owner is not currently eligible for governed routing.");
                var current = await db.Set<CustomerOwnership>().Where(x => x.BusinessUnitId == tenant &&
                    x.CustomerId == customerId && x.Scope == OwnershipScope.GeneralCustomer &&
                    x.ScopeKey == null && x.IsActive && x.EffectiveTo == null).ToListAsync(ct);
                if (current.Count != 0 && current.Max(x => x.Version) != request.ExpectedVersion)
                    return (Created: (CustomerOwnership?)null, Conflict: true);
                if (current.Any(x => x.PrimaryUserId != request.OwnerUserId) && (reason?.Length ?? 0) < 5)
                    throw new InvalidOperationException("A reassignment reason of at least 5 characters is required.");
                var now = DateTime.UtcNow;
                var nextVersion = current.Count == 0 ? 1 : current.Max(value => value.Version) + 1;
                foreach (var value in current) { value.IsActive = false; value.EffectiveTo = now; value.Version++; }
                if (current.Count != 0) await db.SaveChangesAsync(ct);
                var replacement = new CustomerOwnership { BusinessUnitId = tenant, CustomerId = customerId,
                    PrimaryUserId = request.OwnerUserId, Scope = OwnershipScope.GeneralCustomer, Priority = 100,
                    EffectiveFrom = now, IsActive = true, Source = "MANUAL",
                    Reason = reason ?? "Initial account owner assigned from sales management",
                    MutationIdempotencyKey = idempotencyKey, Version = nextVersion };
                db.Add(replacement); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return (Created: (CustomerOwnership?)replacement, Conflict: false);
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (RoutingNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (RoutingConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        if (outcome.Conflict)
            return Conflict(new { error = "Account ownership changed. Refresh and retry." });
        var created = outcome.Created!;
        return Ok(new { customerId, ownerUserId = created.PrimaryUserId, version = created.Version });
    }

    [HttpGet("leads/{leadId:long}/assignment-history")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> AssignmentHistory(long leadId, CancellationToken ct)
    {
        var tenant = TenantId();
        if (!await db.Leads.AsNoTracking().AnyAsync(x => x.BusinessUnitId == tenant && x.Id == leadId, ct))
            return NotFound();
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant).ToDictionaryAsync(x => x.Id, ct);
        var rows = await db.Set<LeadAssignment>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.LeadId == leadId)
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Id).ToListAsync(ct);
        return Ok(rows.Select(x => new {
            x.Id, x.LeadId, previousOwnerUserId = x.FromUserId, ownerUserId = x.ToUserId,
            previousOwnerName = x.FromUserId.HasValue && users.TryGetValue(x.FromUserId.Value, out var previous) ? Name(previous) : null,
            ownerName = users.TryGetValue(x.ToUserId, out var owner) ? Name(owner) : null,
            scope = x.AssignmentScope.ToString(), x.ReasonCode, x.Comment, x.EffectiveFrom, x.EffectiveTo,
            x.CorrelationId, x.IdempotencyKey
        }));
    }

    [HttpGet("routing-queue")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> RoutingQueue([FromQuery] long? sourceId, CancellationToken ct)
    {
        var tenant = TenantId();
        var owners = await routing.GetOwnerOptionsAsync(tenant, ct);
        var ownerLookup = owners.ToDictionary(owner => owner.UserId);
        var rows = await (from item in db.Set<UnassignedWorkItem>().AsNoTracking()
            join lead in db.Leads.AsNoTracking() on item.LeadId equals lead.Id
            join decision in db.Set<LeadRoutingDecision>().AsNoTracking() on item.RoutingDecisionId equals decision.Id
            where item.BusinessUnitId == tenant &&
                  ((!sourceId.HasValue && (item.Status == WorkItemStatus.Open || item.Status == WorkItemStatus.Claimed)) ||
                   (sourceId.HasValue && item.Id == sourceId.Value))
            orderby item.Priority descending, item.EnteredOn
            select new { sourceId = item.Id, leadId = lead.Id, nexoraSerial = lead.CommercialCaseReference ?? $"LEAD-{lead.Id}",
                customerName = lead.BuyersName, receivedAt = lead.RecDate, dueAt = (DateTime?)item.SlaDueOn,
                reason = item.RequiredAction, recommendedOwnerUserId = item.SuggestedUserId,
                recommendationReason = item.ReasonCode, matchConfidence = item.MatchConfidence,
                policyVersion = decision.PolicyVersion, priority = item.Priority,
                claimedByUserId = item.ClaimedByUserId, claimedUntil = item.ClaimedUntil,
                status = item.Status.ToString(), version = item.Version }).Take(250).ToListAsync(ct);
        var claimantIds = rows.Where(row => row.claimedByUserId.HasValue)
            .Select(row => row.claimedByUserId!.Value).Distinct().ToArray();
        // Materialised before Name() is applied: Name() is a C# string concat that EF cannot
        // translate, so composing it inside the projection would fail at query time.
        var claimants = (claimantIds.Length == 0
                ? []
                : await db.Users.AsNoTracking().Where(x => x.Buid == tenant && claimantIds.Contains(x.Id))
                    .ToListAsync(ct))
            .ToDictionary(x => x.Id, Name);
        return Ok(rows.Select(row =>
        {
            ownerLookup.TryGetValue(row.recommendedOwnerUserId ?? 0, out var owner);
            return new
            {
                row.sourceId, row.leadId, row.nexoraSerial, row.customerName, row.receivedAt, row.dueAt,
                overdue = row.dueAt < DateTime.UtcNow, row.reason, row.recommendedOwnerUserId,
                recommendedOwnerName = owner?.Name, row.recommendationReason, row.matchConfidence,
                row.policyVersion, recommendationMeasuredAt = owner?.MeasuredAtUtc,
                recommendedOwnerAvailable = owner?.IsAvailable,
                recommendedOwnerCapacityPercent = owner?.CapacityPercent,
                recommendedOwnerWorkloadPoints = owner?.Workload.WorkloadPoints,
                row.claimedByUserId, row.claimedUntil,
                claimedByName = row.claimedByUserId.HasValue &&
                    claimants.TryGetValue(row.claimedByUserId.Value, out var claimant) ? claimant : null,
                // A lease that has run out is reported as expired rather than as still held.
                // Nothing ever flips Status back to Open when ClaimedUntil passes, so a queue
                // rendered from Status alone would show "Claimed by X" for ever.
                claimExpired = row.claimedUntil.HasValue && row.claimedUntil < DateTime.UtcNow,
                row.priority, row.status, row.version
            };
        }));
    }

    [HttpGet("routing-owner-options")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> RoutingOwnerOptions(CancellationToken ct) =>
        Ok(await routing.GetOwnerOptionsAsync(TenantId(), ct));

    [HttpGet("account-owner-options")]
    [RequireManagerRole]
    [RequireModulePermission("Customers", PermissionAction.Edit)]
    public async Task<ActionResult> AccountOwnerOptions(CancellationToken ct) =>
        Ok(await routing.GetOwnerOptionsAsync(TenantId(), ct));

    [HttpPost("routing-queue/{sourceId:long}/assign")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> AssignLead(long sourceId, AssignRoutingRequest request, CancellationToken ct)
    {
        var tenant = TenantId();
        var item = await db.Set<UnassignedWorkItem>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.Id == sourceId, ct);
        if (item == null) return NotFound();
        var reason = request.Reason?.Trim();
        if (item.SuggestedUserId.HasValue && item.SuggestedUserId != request.OwnerUserId && (reason?.Length ?? 0) < 5)
            return BadRequest(new { error = "A routing override reason of at least 5 characters is required." });
        try
        {
            var result = await routing.AssignQueueItemAsync(tenant, sourceId, new AssignQueueItemCommand(
                request.ExpectedVersion, request.OwnerUserId, UserId(), IdempotencyKey(),
                CorrelationId(), AssignmentScope.LeadOnly,
                reason ?? "Manager accepted the routing recommendation."), ct);
            return Ok(result);
        }
        catch (RoutingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (RoutingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("follow-ups")]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public async Task<ActionResult> FollowUps(
        [FromQuery] string? status,
        [FromQuery] long? customerId,
        [FromQuery] long? sourceId,
        CancellationToken ct)
    {
        var tenant = TenantId();
        var scope = await ResolveAccountScopeAsync(tenant, ct);
        if (scope is null) return Forbid();
        var query = db.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == tenant &&
            (string.IsNullOrWhiteSpace(status) || status != "open" || x.Status == FollowUpStatus.Open || x.Status == FollowUpStatus.InProgress));
        if (!scope.IsTenantWide)
            query = query.Where(x => scope.UserIds.Contains(x.AssignedToUserId));
        if (sourceId.HasValue) query = query.Where(x => x.Id == sourceId.Value);
        if (customerId.HasValue)
            query = query.Where(x => x.AggregateType == "Quote" && db.Quotes.Any(q =>
                q.BusinessUnitId == tenant && q.CustomerId == customerId.Value && q.Id == x.AggregateId));
        var rows = await query
            .OrderBy(x => x.DueAtUtc).Take(250).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant).ToDictionaryAsync(x => x.Id, ct);
        var quoteIds = rows.Where(x => x.AggregateType == "Quote").Select(x => x.AggregateId).Distinct().ToArray();
        var quotes = await db.Quotes.AsNoTracking().Include(x => x.Customer).Where(x => x.BusinessUnitId == tenant && quoteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return Ok(rows.Select(x => { quotes.TryGetValue(x.AggregateId, out var quote); return new { x.Id, quoteId = x.AggregateType.Equals("Quote", StringComparison.OrdinalIgnoreCase) ? x.AggregateId : 0,
            quoteNo = quote?.QuoteNo ?? $"{x.AggregateType} {x.AggregateId}", nexoraSerial = quote?.NexoraSerial,
            customerName = quote?.Customer?.Name ?? "Customer unresolved", ownerUserId = (long?)x.AssignedToUserId,
            ownerName = users.TryGetValue(x.AssignedToUserId, out var user) ? Name(user) : null,
            dueAt = x.DueAtUtc, status = x.Status.ToString(), reason = x.PurposeCode,
            daysSinceContact = quote?.SentOn == null ? (int?)null : Math.Max(0, (DateTime.UtcNow.Date - quote.SentOn.Value.Date).Days), version = x.Version }; }));
    }

    /// <summary>
    /// A follow-up the rep sets deliberately on one quote — "call them Thursday about the price".
    ///
    /// <para><c>CreateFollowUpAsync</c> existed with no HTTP endpoint: the only follow-ups in the
    /// product were the ones quote delivery created on its own. This is the smallest honest door:
    /// the caller is the assignee, the quote must belong to the caller's tenant, and the reason is
    /// stored in the follow-up's purpose field — the column the Follow-ups list already renders as
    /// "Reason" — so no schema changes. The 80-character bound is that column's.</para>
    /// </summary>
    [HttpPost("follow-ups")]
    [RequireModulePermission("Quotations", PermissionAction.Edit)]
    public async Task<ActionResult> CreateFollowUp(CreateQuoteFollowUpRequest request, CancellationToken ct)
    {
        var tenant = TenantId();
        var userId = UserId();
        if (!userId.HasValue) return Forbid();
        if (request.QuoteId <= 0) return BadRequest(new { error = "A quote is required." });
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length == 0) return BadRequest(new { error = "Say what the follow-up is for." });
        if (reason.Length > 80) return BadRequest(new { error = "The reason must be 80 characters or fewer." });

        var quote = await db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == tenant && q.Id == request.QuoteId)
            .Select(q => new { q.Id, q.CustomerId })
            .SingleOrDefaultAsync(ct);
        if (quote is null) return NotFound(new { error = "The quote was not found in this business unit." });

        // The service insists on a UTC instant. A value without an offset is taken as UTC rather
        // than as the server's local time, which is the one reading that is stable across hosts.
        var dueAt = request.DueAt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(request.DueAt, DateTimeKind.Utc)
            : request.DueAt.ToUniversalTime();

        // The correlation id is part of what the service compares on an idempotent replay, so it
        // must be the same on the retry as on the first attempt: derived from the key, never from
        // the per-request trace identifier.
        var idempotencyKey = IdempotencyKey();
        var correlationId = idempotencyKey.Length <= 100 ? idempotencyKey : idempotencyKey[..100];

        try
        {
            var task = await sales.CreateFollowUpAsync(tenant, new CreateFollowUpTaskCommand(
                userId.Value, "Quote", quote.Id,
                quote.CustomerId is > 0 ? quote.CustomerId : null,
                dueAt, 2, reason,
                User.FindFirst(ClaimTypes.Email)?.Value ?? userId.Value.ToString(),
                correlationId, idempotencyKey), ct);
            return CreatedAtAction(nameof(FollowUps), new { sourceId = task.Id }, new
            {
                task.Id, quoteId = task.AggregateId, dueAt = task.DueAtUtc, reason = task.PurposeCode,
                status = task.Status.ToString(), version = task.Version,
            });
        }
        catch (SalesNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (SalesConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (SalesValidationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("follow-ups/{id:long}/complete")]
    [RequireModulePermission("Quotations", PermissionAction.Edit)]
    public async Task<ActionResult> CompleteFollowUp(long id, CompleteFollowUpRequest request, CancellationToken ct)
    {
        var tenant = TenantId();
        var actorUserId = UserId();
        var scope = await ResolveAccountScopeAsync(tenant, ct);
        if (scope is null) return Forbid();
        if (!scope.IsTenantWide)
        {
            var assignedTo = await db.FollowUpTasks.AsNoTracking()
                .Where(x => x.BusinessUnitId == tenant && x.Id == id)
                .Select(x => (long?)x.AssignedToUserId)
                .SingleOrDefaultAsync(ct);
            if (!assignedTo.HasValue) return NotFound();
            if (!scope.UserIds.Contains(assignedTo.Value)) return Forbid();
        }
        await sales.TransitionFollowUpAsync(tenant, id, new TransitionFollowUpTaskCommand(
            FollowUpStatus.Completed, request.ExpectedVersion, UserId()?.ToString() ?? "authenticated-user",
            "Completed by user", HttpContext.TraceIdentifier, IdempotencyKey()), ct);
        return NoContent();
    }

    [HttpGet("performance")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public async Task<ActionResult> Performance([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
    {
        var tenant = TenantId();
        var scope = await ResolveAccountScopeAsync(tenant, ct);
        if (scope is null) return Forbid();
        var tenantWide = scope.IsTenantWide;
        var actorUserId = UserId();
        var fromUtc = NormalizeUtc(from);
        var toUtc = NormalizeUtc(to);
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(366))
            return BadRequest(new { error = "The performance period must be between 1 and 366 days." });
        var asOf = DateTime.UtcNow < toUtc ? DateTime.UtcNow : toUtc.AddTicks(-1);
        IReadOnlyList<SalesRepPerformance> results;
        if (tenantWide)
        {
            results = await sales.GetPerformanceAsync(tenant,
                new SalesPerformanceQuery(null, fromUtc, toUtc, asOf), ct);
        }
        else
        {
            // ISalesPersistence is request-scoped and shares this controller's DbContext.
            // EF Core forbids concurrent operations on one context, so managed-team queries
            // must be sequenced rather than fanned out with Task.WhenAll.
            var scopedResults = new List<SalesRepPerformance>();
            foreach (var userId in scope.UserIds)
            {
                scopedResults.AddRange(await sales.GetPerformanceAsync(tenant,
                    new SalesPerformanceQuery(userId, fromUtc, toUtc, asOf), ct));
            }
            results = scopedResults;
        }
        var attributedOutcomeIds = await db.CommercialActivities.AsNoTracking()
            .Where(activity => activity.BusinessUnitId == tenant && activity.AggregateType == "Quote" &&
                (activity.ActivityType == CommercialActivityType.Won || activity.ActivityType == CommercialActivityType.Lost) &&
                activity.OccurredAtUtc >= fromUtc && activity.OccurredAtUtc < toUtc &&
                (tenantWide || scope.UserIds.Contains(activity.SalesRepUserId)))
            .Select(activity => activity.AggregateId).Distinct().ToArrayAsync(ct);
        int? recordedOutcomeCount = null;
        int? unattributedOutcomeCount = null;
        decimal? attributionCompletenessPercent = null;
        if (tenantWide)
        {
            var recordedOutcomes = await db.Quotes.AsNoTracking().Include(quote => quote.Status)
                .Where(quote => quote.BusinessUnitId == tenant && quote.OutcomeOn >= fromUtc && quote.OutcomeOn < toUtc)
                .Select(quote => new { quote.Id, quote.Status!.SetupCode, quote.Status.SetupValue })
                .ToListAsync(ct);
            recordedOutcomeCount = recordedOutcomes.Count(quote =>
                Canonical(quote.SetupCode, quote.SetupValue) is "ACCEPTED" or "REJECTED");
            unattributedOutcomeCount = Math.Max(0, recordedOutcomeCount.Value - attributedOutcomeIds.Length);
            attributionCompletenessPercent = recordedOutcomeCount == 0
                ? null
                : Math.Round(attributedOutcomeIds.Length * 100m / recordedOutcomeCount.Value, 1);
        }
        var reps = await BuildRepSummaries(tenant, ct, scope);
        var rows = from rep in reps join result in results on rep.UserId equals result.SalesRepUserId into resultRows from result in resultRows.DefaultIfEmpty()
            select new { rep.UserId, rep.Name, rep.Email, rep.RoleName, rep.ActiveLeads, rep.OverdueLeads, rep.OpenRfqs,
                rep.DraftQuotes, rep.FollowUpsDue, rep.PipelineGroups,
                wonQuotes = result?.WonCount ?? 0, lostQuotes = result?.LostCount ?? 0,
                followUpsCreated = result?.FollowUpsCreated ?? 0,
                completedFollowUps = result?.FollowUpsCompleted ?? 0,
                followUpsCompletedOnTime = result?.FollowUpsCompletedOnTime ?? 0,
                openFollowUps = result?.OpenFollowUps ?? 0, overdueFollowUps = result?.OverdueFollowUps ?? 0,
                activityCount = result?.ActivityCount ?? 0,
                opportunities = result?.OpportunityCount ?? 0,
                quoteSent = result?.QuoteSentCount ?? 0,
                customerResponses = result?.CustomerResponseCount ?? 0,
                decidedQuotes = (result?.WonCount ?? 0) + (result?.LostCount ?? 0),
                conversionEligible = (result?.WonCount ?? 0) + (result?.LostCount ?? 0) >= MinimumConversionSample,
                conversionRate = (result?.WonCount ?? 0) + (result?.LostCount ?? 0) >= MinimumConversionSample
                    ? result?.WinRatePercent : null,
                averageResponseHours = result?.AverageResponseHours,
                revenueByCurrency = result?.RevenueByCurrency.Select(x => new CurrencyAmountGroup(x.CurrencyCode, x.WeightedRevenueAmount)).ToArray()
                    ?? Array.Empty<CurrencyAmountGroup>() };
        return Ok(new { generatedAt = DateTime.UtcNow, from = fromUtc, to = toUtc,
            scope = ScopeWireName(scope), minimumConversionSample = MinimumConversionSample,
            outcomeReconciliation = new {
                recordedOutcomes = recordedOutcomeCount,
                attributedOutcomes = attributedOutcomeIds.Length,
                unattributedOutcomes = unattributedOutcomeCount,
                completenessPercent = attributionCompletenessPercent,
                isTenantComplete = tenantWide
            },
            metrics = new[] { Metric("won", "Won", results.Sum(x => x.WonCount)), Metric("lost", "Lost", results.Sum(x => x.LostCount)),
                Metric("decided", "Decided outcomes", results.Sum(x => x.WonCount + x.LostCount)) }, representatives = rows });
    }

    private async Task<List<RepSummary>> BuildRepSummaries(
        long tenant, CancellationToken ct, AccountTeamScope? scope = null)
    {
        var allowedUserIds = scope is { IsTenantWide: false } ? scope.UserIds : null;
        var usersQuery = db.Users.AsNoTracking().Include(x => x.Role)
            .Where(x => x.Buid == tenant && x.IsActive != false);
        if (allowedUserIds is not null)
            usersQuery = usersQuery.Where(x => allowedUserIds.Contains(x.Id));
        var users = await usersQuery.OrderBy(x => x.FirstName).ThenBy(x => x.LastName).ToListAsync(ct);
        var assignmentsQuery = db.Set<LeadAssignment>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.EffectiveTo == null);
        var followUpsQuery = db.FollowUpTasks.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.Status != FollowUpStatus.Completed
                && x.Status != FollowUpStatus.Cancelled);
        if (allowedUserIds is not null)
        {
            assignmentsQuery = assignmentsQuery.Where(x => allowedUserIds.Contains(x.ToUserId));
            followUpsQuery = followUpsQuery.Where(x => allowedUserIds.Contains(x.AssignedToUserId));
        }
        var assignments = await assignmentsQuery.ToListAsync(ct);
        var followUps = await followUpsQuery.ToListAsync(ct);
        var leadIds = assignments.Select(x => x.LeadId).Distinct().ToArray();
        var leads = await db.Leads.AsNoTracking().Include(x => x.LeadStatus).Where(x => x.BusinessUnitId == tenant && leadIds.Contains(x.Id)).ToListAsync(ct);
        var rfqs = await db.Rfqs.AsNoTracking().Include(x => x.Rfqstatus).Where(x => x.BusinessUnitId == tenant && x.LeadId.HasValue && leadIds.Contains(x.LeadId.Value)).ToListAsync(ct);
        var rfqIds = rfqs.Select(x => x.Id).ToArray();
        var quotes = await db.Quotes.AsNoTracking().Include(x => x.Status).Include(x => x.Currency)
            .Where(x => x.BusinessUnitId == tenant && x.Rfqid.HasValue && rfqIds.Contains(x.Rfqid.Value)).ToListAsync(ct);
        return users.Select(user => { var ownedLeadIds = assignments.Where(x => x.ToUserId == user.Id).Select(x => x.LeadId).ToHashSet();
            var activeLeads = leads.Where(x => ownedLeadIds.Contains(x.Id) && !TerminalLead(x.LeadStatus?.SetupCode, x.LeadStatus?.SetupValue)).ToArray();
            var allOwnedRfqs = rfqs.Where(x => x.LeadId.HasValue && ownedLeadIds.Contains(x.LeadId.Value)).ToArray();
            var openRfqs = allOwnedRfqs.Where(x => !TerminalRfq(x.Rfqstatus?.SetupCode, x.Rfqstatus?.SetupValue)).ToArray();
            var allOwnedRfqIds = allOwnedRfqs.Select(x => x.Id).ToHashSet();
            var ownedQuotes = quotes.Where(x => x.Rfqid.HasValue && allOwnedRfqIds.Contains(x.Rfqid.Value) && !TerminalQuote(x.Status?.SetupCode, x.Status?.SetupValue)).ToArray();
            return new RepSummary(user.Id, Name(user), user.Email, user.Role?.SetupValue,
            activeLeads.Length, activeLeads.Count(x => x.BidClosingDate.HasValue && x.BidClosingDate < DateTime.UtcNow), openRfqs.Length,
            ownedQuotes.Count(x => Canonical(x.Status?.SetupCode, x.Status?.SetupValue) == "DRAFT"),
            followUps.Count(x => x.AssignedToUserId == user.Id && x.DueAtUtc <= DateTime.UtcNow.AddDays(1)),
            PipelineGroups(ownedQuotes)); }).ToList();
    }

    private static object Metric(string key, string label, decimal value) => new { key, label, value, unit = "count" };
    private const int MinimumConversionSample = 5;
    private static string Name(User user) => $"{user.FirstName} {user.LastName}".Trim();
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static ActivityEvidence ResolveActivityEvidence(
        CommercialActivity activity,
        IReadOnlyDictionary<long, Lead> leads,
        IReadOnlyDictionary<long, Rfq> rfqs,
        IReadOnlyDictionary<long, Quote> quotes,
        IReadOnlyDictionary<long, Order> orders)
    {
        if (activity.AggregateType.Equals("Lead", StringComparison.OrdinalIgnoreCase) &&
            leads.TryGetValue(activity.AggregateId, out var lead))
            return new(lead.CommercialCaseReference, lead.Rfqno, lead.BuyersName, lead.CustomerId,
                $"/procurement/leads/view/{lead.Id}", "Leads");
        if (activity.AggregateType.Equals("RFQ", StringComparison.OrdinalIgnoreCase) &&
            rfqs.TryGetValue(activity.AggregateId, out var rfq))
            return new(rfq.NexoraSerial, rfq.Rfqno, rfq.BuyersName, rfq.CustomerId,
                $"/procurement/rfqs/view/{rfq.Id}", "RFQ Management");
        if (activity.AggregateType.Equals("Quote", StringComparison.OrdinalIgnoreCase) &&
            quotes.TryGetValue(activity.AggregateId, out var quote))
            return new(quote.NexoraSerial, quote.QuoteNo, null, quote.CustomerId,
                $"/sales/quotes/view/{quote.Id}", "Quotations");
        if (activity.AggregateType.Equals("Order", StringComparison.OrdinalIgnoreCase) &&
            orders.TryGetValue(activity.AggregateId, out var order))
            return new(order.NexoraSerial, order.OrderNo, null, order.CustomerId,
                $"/sales/orders/{order.Id}", "Orders");
        return new(null, null, null, activity.CustomerId, null, null);
    }
    private static string Canonical(string? code, string? value) => ((string.IsNullOrWhiteSpace(code) ? value : code) ?? string.Empty).Trim().Replace(' ', '_').ToUpperInvariant();
    private static bool TerminalLead(string? code, string? value) => Canonical(code, value) is "DISQUALIFIED" or "CONVERTED_TO_RFQ" or "LOST" or "CANCELLED" or "COMPLETED" or "DUPLICATED";
    private static bool TerminalRfq(string? code, string? value) => Canonical(code, value) is "CANCELLED" or "CLOSED" or "QUOTE_SENT";
    private static bool TerminalQuote(string? code, string? value) => Canonical(code, value) is "ACCEPTED" or "REJECTED" or "EXPIRED" or "ORDERED" or "CANCELLED";
    private static IReadOnlyList<CurrencyPipelineGroup> PipelineGroups(IEnumerable<Quote> quotes) => quotes
        .GroupBy(x => new { x.CurrencyId, CurrencyCode = x.Currency?.Code })
        .Select(group => new CurrencyPipelineGroup(
            group.Key.CurrencyId,
            group.Key.CurrencyCode,
            group.Count(),
            group.Sum(x => x.TotalAmount ?? 0m),
            group.Sum(x => (x.TotalAmount ?? 0m) * (x.RespondedOn.HasValue ? .5m : x.SentOn.HasValue ? .3m : 0m))))
        .OrderBy(x => x.CurrencyCode ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(x => x.CurrencyId)
        .ToArray();
    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0 ? id : throw new SalesConflictException("Business Unit ID is required.");
    private long? UserId() => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id) ? id : null;
    private long ClaimId(string claimType) => long.TryParse(User.FindFirst(claimType)?.Value, out var id) ? id : 0;
    private async Task<AccountTeamScope?> ResolveAccountScopeAsync(long tenant, CancellationToken ct)
    {
        var userId = UserId();
        var roleId = ClaimId("roleId");
        if (!userId.HasValue || roleId <= 0) return null;
        if (accountScope is not null)
            return await accountScope.ResolveAsync(userId.Value, roleId, tenant, DateTime.UtcNow, ct);
        return await roleGate.IsManagerOrAdminAsync(roleId, tenant)
            ? AccountTeamScope.TenantWide(userId.Value)
            : new AccountTeamScope(AccountScopeTier.AssignedAccounts, userId.Value, [], [userId.Value]);
    }
    private static string ScopeWireName(AccountTeamScope scope) => scope.Tier switch
    {
        AccountScopeTier.Tenant => "tenant",
        AccountScopeTier.ManagedScope => "managed_scope",
        _ => "assigned_to_me"
    };
    private string IdempotencyKey() => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : throw new SalesValidationException("Idempotency-Key header is required.");
    private string CorrelationId() => Request.Headers.TryGetValue("X-Correlation-ID", out var value) && !string.IsNullOrWhiteSpace(value)
        ? value.ToString().Trim()
        : HttpContext.TraceIdentifier;
}

public sealed record AssignAccountRequest(long OwnerUserId, long ExpectedVersion, string? Reason = null);
public sealed record AssignRoutingRequest(long OwnerUserId, long ExpectedVersion, string? Reason = null);
public sealed record CompleteFollowUpRequest(long ExpectedVersion);
public sealed record CreateQuoteFollowUpRequest(long QuoteId, DateTime DueAt, string? Reason);
public sealed record RepSummary(long UserId, string Name, string? Email, string? RoleName, int ActiveLeads,
    int OverdueLeads, int OpenRfqs, int DraftQuotes, int FollowUpsDue, IReadOnlyList<CurrencyPipelineGroup> PipelineGroups);
public sealed record CurrencyPipelineGroup(long? CurrencyId, string? CurrencyCode, int QuoteCount,
    decimal PipelineValue, decimal WeightedPipeline);
public sealed record CurrencyAmountGroup(string CurrencyCode, decimal Value);
public sealed record ActivityEvidence(string? NexoraSerial, string? Reference, string? CustomerName,
    long? CustomerId, string? ActionRoute, string? RequiredModule);

/// <summary>
/// Body for <c>POST reps/{userId}/routing-profile</c>. Deliberately a thin projection of
/// <c>UpsertSalesRepProfileCommand</c>: the user id comes from the route, and ActorId and
/// IdempotencyKey are server-derived, so neither can be spoofed from the body.
/// </summary>
public sealed class UpsertRepRoutingProfileRequest
{
    /// <summary>Whether the routing engine may assign leads to this rep at all.</summary>
    public bool IsRoutingEligible { get; set; } = true;

    /// <summary>0-100. Feeds the workload-relief factor in the routing engine.</summary>
    public int CapacityPercent { get; set; } = 100;

    /// <summary>Relative share when several eligible reps tie. Must be > 0 and &lt;= 1000.</summary>
    public decimal DistributionWeight { get; set; } = 1m;

    public IReadOnlyCollection<string>? TerritoryKeys { get; set; }

    public IReadOnlyCollection<string>? ProductCategoryKeys { get; set; }

    /// <summary>Defaults to now (UTC) when omitted.</summary>
    public DateTime? EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    /// <summary>Optimistic concurrency. 0 creates; an existing profile requires its current
    /// Version, so two managers editing the same rep cannot silently overwrite each other.</summary>
    public long ExpectedVersion { get; set; }
}
