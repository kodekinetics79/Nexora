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
    ICommercialRoutingApplicationService routing) : ControllerBase
{
    [HttpGet("sales-today")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> SalesToday(CancellationToken ct)
    {
        var tenant = TenantId();
        var now = DateTime.UtcNow;
        var followUps = await db.FollowUpTasks.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.Status != FollowUpStatus.Completed && x.Status != FollowUpStatus.Cancelled)
            .OrderBy(x => x.DueAtUtc).Take(100).ToListAsync(ct);
        var unassigned = await db.Set<UnassignedWorkItem>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.Status == WorkItemStatus.Open).CountAsync(ct);
        var overdue = followUps.Count(x => x.DueAtUtc < now);
        var items = followUps.Select(x => new
        {
            id = x.Id, recordType = x.AggregateType, recordId = x.AggregateId,
            nexoraSerial = (string?)null, reference = $"{x.AggregateType} {x.AggregateId}",
            customerName = (string?)null, ownerName = (string?)null,
            reason = x.PurposeCode, dueAt = (DateTime?)x.DueAtUtc,
            priority = x.DueAtUtc < now ? "Critical" : "Due"
        }).ToArray();
        return Ok(new { generatedAt = now, metrics = new[] {
            Metric("open-follow-ups", "Open follow-ups", followUps.Count),
            Metric("overdue-follow-ups", "Overdue follow-ups", overdue),
            Metric("unassigned-leads", "Unassigned leads", unassigned)
        }, attentionItems = items });
    }

    [HttpGet("team-overview")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> TeamOverview(CancellationToken ct)
    {
        var reps = await BuildRepSummaries(TenantId(), ct);
        return Ok(new { generatedAt = DateTime.UtcNow, metrics = new[] {
            Metric("active-reps", "Active representatives", reps.Count),
            Metric("active-leads", "Active assigned leads", reps.Sum(x => x.ActiveLeads)),
            Metric("follow-ups-due", "Follow-ups due", reps.Sum(x => x.FollowUpsDue))
        }, representatives = reps });
    }

    [HttpGet("reps")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult> Reps(CancellationToken ct) => Ok(await BuildRepSummaries(TenantId(), ct));

    [HttpGet("reps/{userId:long}")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult> Rep(long userId, CancellationToken ct)
    {
        var tenant = TenantId();
        var summary = (await BuildRepSummaries(tenant, ct)).SingleOrDefault(x => x.UserId == userId);
        if (summary == null) return NotFound();
        var accountCount = await db.Set<CustomerOwnership>().CountAsync(x => x.BusinessUnitId == tenant && x.PrimaryUserId == userId && x.IsActive, ct);
        var performance = (await sales.GetPerformanceAsync(tenant,
            new SalesPerformanceQuery(userId, DateTime.UtcNow.AddDays(-90), DateTime.UtcNow.AddSeconds(1), DateTime.UtcNow), ct)).SingleOrDefault();
        var activity = await db.CommercialActivities.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.SalesRepUserId == userId)
            .OrderByDescending(x => x.OccurredAtUtc).Take(20).Select(x => new {
                id = x.Id, recordType = x.AggregateType, recordId = x.AggregateId,
                reference = x.AggregateType + " " + x.AggregateId, reason = x.ActivityType.ToString(),
                dueAt = (DateTime?)x.OccurredAtUtc, priority = "Recorded"
            }).ToListAsync(ct);
        return Ok(new {
            summary.UserId, summary.Name, summary.Email, summary.RoleName, summary.ActiveLeads,
            summary.OverdueLeads, summary.OpenRfqs, summary.DraftQuotes, summary.FollowUpsDue,
            summary.WeightedPipeline, summary.CurrencyCode, accountCount,
            wonValue = performance?.RevenueByCurrency.Sum(x => x.WeightedRevenueAmount) ?? 0,
            conversionRate = performance?.WinRatePercent, recentActivity = activity
        });
    }

    [HttpGet("account-ownership")]
    [RequireModulePermission("Customers", PermissionAction.View)]
    public async Task<ActionResult> AccountOwnership([FromQuery] string? search, CancellationToken ct)
    {
        var tenant = TenantId();
        var customers = await db.Customers.AsNoTracking().Where(x => x.Buid == tenant &&
                (string.IsNullOrWhiteSpace(search) || EF.Functions.ILike(x.Name, $"%{search}%")))
            .OrderBy(x => x.Name).Take(250).ToListAsync(ct);
        var ids = customers.Select(x => x.Id).ToArray();
        var ownerships = await db.Set<CustomerOwnership>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && ids.Contains(x.CustomerId) && x.IsActive && x.EffectiveTo == null)
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.EffectiveFrom).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant).ToDictionaryAsync(x => x.Id, ct);
        var leads = await db.Leads.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.CustomerId.HasValue && ids.Contains(x.CustomerId.Value)).GroupBy(x => x.CustomerId!.Value).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var quotes = await db.Quotes.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.CustomerId.HasValue && ids.Contains(x.CustomerId.Value)).GroupBy(x => x.CustomerId!.Value).Select(x => new { Id = x.Key, Count = x.Count(), Value = x.Sum(q => q.TotalAmount ?? 0) }).ToDictionaryAsync(x => x.Id, ct);
        return Ok(customers.Select(customer => {
            var owner = ownerships.FirstOrDefault(x => x.CustomerId == customer.Id);
            users.TryGetValue(owner?.PrimaryUserId ?? 0, out var user);
            quotes.TryGetValue(customer.Id, out var quote);
            return new { customerId = customer.Id, customerName = customer.Name,
                ownerUserId = owner?.PrimaryUserId, ownerName = user == null ? null : Name(user),
                openLeads = leads.GetValueOrDefault(customer.Id), openQuotes = quote?.Count ?? 0,
                pipelineValue = quote?.Value ?? 0, currencyCode = (string?)null,
                lastActivityAt = customer.ModifiedOn ?? customer.CreatedOn, version = owner?.Version ?? 0 };
        }));
    }

    [HttpPost("account-ownership/{customerId:long}/assign")]
    [RequireManagerRole]
    [RequireModulePermission("Customers", PermissionAction.Edit)]
    public async Task<ActionResult> AssignAccount(long customerId, AssignAccountRequest request, CancellationToken ct)
    {
        var tenant = TenantId();
        if (!await db.Customers.AnyAsync(x => x.Buid == tenant && x.Id == customerId, ct) ||
            !await db.Users.AnyAsync(x => x.Buid == tenant && x.Id == request.OwnerUserId && x.IsActive != false, ct)) return NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Set<CustomerOwnership>().Where(x => x.BusinessUnitId == tenant && x.CustomerId == customerId && x.IsActive && x.EffectiveTo == null).ToListAsync(ct);
        if (current.Count != 0 && current.Max(x => x.Version) != request.ExpectedVersion) return Conflict(new { error = "Account ownership changed. Refresh and retry." });
        var now = DateTime.UtcNow;
        foreach (var value in current) { value.IsActive = false; value.EffectiveTo = now; value.Version++; }
        var created = new CustomerOwnership { BusinessUnitId = tenant, CustomerId = customerId, PrimaryUserId = request.OwnerUserId,
            Scope = OwnershipScope.GeneralCustomer, Priority = 100, EffectiveFrom = now, IsActive = true,
            Source = "MANUAL", Reason = "Assigned from sales management", Version = 1 };
        db.Add(created); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Ok(new { customerId, ownerUserId = created.PrimaryUserId, version = created.Version });
    }

    [HttpGet("routing-queue")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> RoutingQueue(CancellationToken ct)
    {
        var tenant = TenantId();
        var rows = await (from item in db.Set<UnassignedWorkItem>().AsNoTracking()
            join lead in db.Leads.AsNoTracking() on item.LeadId equals lead.Id
            where item.BusinessUnitId == tenant && item.Status == WorkItemStatus.Open
            orderby item.Priority descending, item.EnteredOn
            select new { leadId = lead.Id, nexoraSerial = lead.CommercialCaseReference ?? $"LEAD-{lead.Id}",
                customerName = lead.BuyersName, receivedAt = lead.RecDate, dueAt = (DateTime?)item.SlaDueOn,
                reason = item.RequiredAction, recommendedOwnerUserId = item.SuggestedUserId,
                recommendedOwnerName = (string?)null, recommendationReason = item.ReasonCode, version = item.Version }).Take(250).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("routing-queue/{leadId:long}/assign")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> AssignLead(long leadId, AssignRoutingRequest request, CancellationToken ct)
    {
        await routing.AssignLeadAsync(TenantId(), new ManualAssignLeadCommand(leadId, request.OwnerUserId, UserId(),
            IdempotencyKey(), HttpContext.TraceIdentifier, AssignmentScope.LeadOnly, "Assigned from routing queue", false, null), ct);
        return NoContent();
    }

    [HttpGet("follow-ups")]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public async Task<ActionResult> FollowUps([FromQuery] string? status, CancellationToken ct)
    {
        var tenant = TenantId();
        var rows = await db.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == tenant &&
                (string.IsNullOrWhiteSpace(status) || status != "open" || x.Status == FollowUpStatus.Open || x.Status == FollowUpStatus.InProgress))
            .OrderBy(x => x.DueAtUtc).Take(250).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant).ToDictionaryAsync(x => x.Id, ct);
        return Ok(rows.Select(x => new { x.Id, quoteId = x.AggregateType.Equals("Quote", StringComparison.OrdinalIgnoreCase) ? x.AggregateId : 0,
            quoteNo = $"{x.AggregateType} {x.AggregateId}", customerName = "Customer", ownerUserId = (long?)x.AssignedToUserId,
            ownerName = users.TryGetValue(x.AssignedToUserId, out var user) ? Name(user) : null,
            dueAt = x.DueAtUtc, status = x.Status.ToString(), reason = x.PurposeCode,
            daysSinceContact = (int?)null, version = x.Version }));
    }

    [HttpPost("follow-ups/{id:long}/complete")]
    [RequireModulePermission("Quotations", PermissionAction.Edit)]
    public async Task<ActionResult> CompleteFollowUp(long id, CompleteFollowUpRequest request, CancellationToken ct)
    {
        await sales.TransitionFollowUpAsync(TenantId(), id, new TransitionFollowUpTaskCommand(
            FollowUpStatus.Completed, request.ExpectedVersion, UserId()?.ToString() ?? "authenticated-user",
            "Completed by user", HttpContext.TraceIdentifier, IdempotencyKey()), ct);
        return NoContent();
    }

    [HttpGet("performance")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public async Task<ActionResult> Performance([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
    {
        var tenant = TenantId();
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        var results = await sales.GetPerformanceAsync(tenant, new SalesPerformanceQuery(null, fromUtc, toUtc, DateTime.UtcNow < toUtc ? DateTime.UtcNow : toUtc.AddTicks(-1)), ct);
        var reps = await BuildRepSummaries(tenant, ct);
        var rows = from rep in reps join result in results on rep.UserId equals result.SalesRepUserId into resultRows from result in resultRows.DefaultIfEmpty()
            select new { rep.UserId, rep.Name, rep.Email, rep.RoleName, rep.ActiveLeads, rep.OverdueLeads, rep.OpenRfqs,
                rep.DraftQuotes, rep.FollowUpsDue, rep.WeightedPipeline, rep.CurrencyCode,
                wonQuotes = result?.WonCount ?? 0, lostQuotes = result?.LostCount ?? 0, conversionRate = result?.WinRatePercent };
        return Ok(new { generatedAt = DateTime.UtcNow, from = fromUtc, to = toUtc,
            metrics = new[] { Metric("won", "Won", results.Sum(x => x.WonCount)), Metric("lost", "Lost", results.Sum(x => x.LostCount)) }, representatives = rows });
    }

    private async Task<List<RepSummary>> BuildRepSummaries(long tenant, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(x => x.Buid == tenant && x.IsActive != false).OrderBy(x => x.FirstName).ThenBy(x => x.LastName).ToListAsync(ct);
        var assignments = await db.Set<LeadAssignment>().AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.EffectiveTo == null).ToListAsync(ct);
        var followUps = await db.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.Status != FollowUpStatus.Completed && x.Status != FollowUpStatus.Cancelled).ToListAsync(ct);
        return users.Select(user => new RepSummary(user.Id, Name(user), user.Email, null,
            assignments.Count(x => x.ToUserId == user.Id), 0, 0, 0,
            followUps.Count(x => x.AssignedToUserId == user.Id && x.DueAtUtc <= DateTime.UtcNow.AddDays(1)),
            0, null)).ToList();
    }

    private static object Metric(string key, string label, decimal value) => new { key, label, value, unit = "count" };
    private static string Name(User user) => $"{user.FirstName} {user.LastName}".Trim();
    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0 ? id : throw new SalesConflictException("Business Unit ID is required.");
    private long? UserId() => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id) ? id : null;
    private string IdempotencyKey() => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : throw new SalesValidationException("Idempotency-Key header is required.");
}

public sealed record AssignAccountRequest(long OwnerUserId, long ExpectedVersion);
public sealed record AssignRoutingRequest(long OwnerUserId, long ExpectedVersion);
public sealed record CompleteFollowUpRequest(long ExpectedVersion);
public sealed record RepSummary(long UserId, string Name, string? Email, string? RoleName, int ActiveLeads,
    int OverdueLeads, int OpenRfqs, int DraftQuotes, int FollowUpsDue, decimal WeightedPipeline, string? CurrencyCode);
