using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Shared paging helpers + schema snippets for the read tools. All reads run
/// against the tenant-scoped <see cref="ErpRfqAutomationContext"/> whose global
/// query filter already constrains rows to the caller's business unit.
/// </summary>
internal static class ToolSchemas
{
    public const string Search =
        "{\"type\":\"object\",\"properties\":{" +
        "\"query\":{\"type\":\"string\",\"description\":\"Free-text filter\"}," +
        "\"page\":{\"type\":\"integer\",\"minimum\":1,\"default\":1}," +
        "\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":10}}}";

    public const string Empty = "{\"type\":\"object\",\"properties\":{}}";

    public static (int page, int size) Paging(JsonElement input)
    {
        var page = Math.Max(1, input.GetInt32("page", 1));
        var size = Math.Clamp(input.GetInt32("pageSize", 10), 1, 50);
        return (page, size);
    }

    /// <summary>Extraction sentinel floor — dates before this year are "unknown", never overdue.</summary>
    public const int SentinelYearFloor = 2000;

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// WP-B1: resolves an "assignedTo" name-or-email substring against the
    /// tenant's active users. Returns matched user ids plus the identity strings
    /// (email and "First Last") a Quote.CreatedBy may carry — the same owner
    /// convention as the SLA stale-quote digest.
    /// </summary>
    public static async Task<(List<long> UserIds, List<string> Identities)> ResolveAssigneesAsync(
        ErpRfqAutomationContext db, long businessUnitId, string term, CancellationToken ct)
    {
        var pattern = $"%{EscapeLike(term.Trim())}%";
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Buid == businessUnitId && u.IsActive != false)
            .Where(u => EF.Functions.ILike(u.FirstName + " " + u.LastName, pattern, "\\")
                        || EF.Functions.ILike(u.Email, pattern, "\\"))
            .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName })
            .ToListAsync(ct);

        var identities = new List<string>();
        foreach (var u in users)
        {
            if (!string.IsNullOrWhiteSpace(u.Email)) identities.Add(u.Email);
            identities.Add($"{u.FirstName} {u.LastName}".Trim());
        }
        return (users.Select(u => u.Id).ToList(), identities);
    }

    /// <summary>The BU's configured stale-quote threshold, or the SLA default.</summary>
    public static async Task<int> GetStaleQuoteDaysAsync(ErpRfqAutomationContext db, long businessUnitId, CancellationToken ct)
    {
        return await db.Set<ERP_RFQ_Automation.Sla.SlaPolicy>().AsNoTracking()
            .Where(p => p.BusinessUnitId == businessUnitId)
            .Select(p => (int?)p.StaleQuoteDays)
            .FirstOrDefaultAsync(ct)
            ?? ERP_RFQ_Automation.Sla.SlaPolicy.Default(businessUnitId).StaleQuoteDays;
    }

    /// <summary>
    /// SetupMaster ids for a status code (SetupType + SetupCode — never magic ids
    /// alone) + the documented legacy fallback id, mirroring
    /// SlaSweepWorker.GetStatusIdsAsync so pre-SetupMaster tenants still resolve.
    /// </summary>
    public static async Task<List<long>> GetStatusIdsAsync(
        ErpRfqAutomationContext db, string setupType, string code, long? legacyId, CancellationToken ct)
    {
        var ids = await db.SetupMasters.AsNoTracking()
            .Where(s => s.SetupType == setupType && s.SetupCode == code)
            .Select(s => s.SetupId)
            .ToListAsync(ct);
        if (legacyId.HasValue && !ids.Contains(legacyId.Value)) ids.Add(legacyId.Value);
        return ids;
    }
}

public sealed class SearchRfqsTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public SearchRfqsTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.SearchRfqs;
    public string Description => "Search this tenant's RFQs by number or buyer name. Returns a compact page of RFQs.";
    public string InputJsonSchema => ToolSchemas.Search;
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var (page, size) = ToolSchemas.Paging(input);

        var query = _db.Set<Rfq>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(r => EF.Functions.ILike(r.Rfqno, $"%{q}%")
                                     || (r.BuyersName != null && EF.Functions.ILike(r.BuyersName, $"%{q}%")));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(r => r.RecDate)
            .Skip((page - 1) * size).Take(size)
            .Select(r => new
            {
                r.Id,
                rfqNo = r.Rfqno,
                buyer = r.BuyersName,
                recDate = r.RecDate,
                bidClosingDate = r.BidClosingDate,
                lineItems = r.NoOfLineItems,
                status = r.Rfqstatus != null ? r.Rfqstatus.SetupValue : null
            })
            .ToListAsync(ct);

        return AgentToolResult.Ok(new { total, page, pageSize = size, items = rows });
    }
}

public sealed class GetRfqTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public GetRfqTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.GetRfq;
    public string Description => "Get one RFQ with its line items by RFQ id.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"rfqId\":{\"type\":\"integer\"}},\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        var rfq = await _db.Set<Rfq>().AsNoTracking()
            .Where(r => r.Id == rfqId.Value)
            .Select(r => new
            {
                r.Id,
                rfqNo = r.Rfqno,
                buyer = r.BuyersName,
                recDate = r.RecDate,
                bidClosingDate = r.BidClosingDate,
                status = r.Rfqstatus != null ? r.Rfqstatus.SetupValue : null,
                items = r.Rfqitems.Select(i => new
                {
                    i.Id,
                    product = i.ProductShortName,
                    i.Quantity,
                    i.UnitPrice,
                    supplierId = i.SupplierId,
                    leadTime = i.LeadTime
                }).ToList()
            })
            .FirstOrDefaultAsync(ct);

        return rfq is null ? AgentToolResult.Fail($"RFQ {rfqId} not found.") : AgentToolResult.Ok(rfq);
    }
}

public sealed class SearchSuppliersTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public SearchSuppliersTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.SearchSuppliers;
    public string Description => "Search suppliers by name, tags or contact email. Returns a compact page.";
    public string InputJsonSchema => ToolSchemas.Search;
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var (page, size) = ToolSchemas.Paging(input);

        var query = _db.Set<Supplier>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(s => EF.Functions.ILike(s.Name, $"%{q}%")
                                     || (s.Tags != null && EF.Functions.ILike(s.Tags, $"%{q}%"))
                                     || (s.ContactEmail != null && EF.Functions.ILike(s.ContactEmail, $"%{q}%")));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(s => s.SuccessRate)
            .Skip((page - 1) * size).Take(size)
            .Select(s => new
            {
                s.Id,
                s.Name,
                email = s.ContactEmail,
                s.SuccessRate,
                avgResponseTime = s.AvgResponseTime,
                s.Tags,
                isActive = s.IsActive
            })
            .ToListAsync(ct);

        return AgentToolResult.Ok(new { total, page, pageSize = size, items = rows });
    }
}

public sealed class SearchLeadsTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public SearchLeadsTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.SearchLeads;
    public string Description =>
        "Search inbound leads by RFQ number or buyer name. Optional filters: assignedTo " +
        "(a rep's name or email substring, e.g. \"Tomas\" — resolves who the lead is assigned to), " +
        "overdueOnly (bid closing date already passed), staleOnly (accepted leads that have produced " +
        "no RFQ for longer than the tenant's stale threshold).";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"query\":{\"type\":\"string\",\"description\":\"Free-text filter on RFQ number or buyer name\"}," +
        "\"assignedTo\":{\"type\":\"string\",\"description\":\"Rep name or email substring; only leads assigned to matching users\"}," +
        "\"overdueOnly\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Only leads whose bid closing date is already in the past (real dates only)\"}," +
        "\"staleOnly\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Only accepted leads with no RFQ created yet, older than the tenant's stale threshold\"}," +
        "\"page\":{\"type\":\"integer\",\"minimum\":1,\"default\":1}," +
        "\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":10}}}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var assignedTo = input.GetStringOrNull("assignedTo");
        var overdueOnly = input.GetBool("overdueOnly");
        var staleOnly = input.GetBool("staleOnly");
        var (page, size) = ToolSchemas.Paging(input);
        var now = DateTime.UtcNow;

        // The tenant-scoped context's global query filter constrains rows to the
        // caller's BU; the extra filters below only narrow further.
        var query = _db.Set<Lead>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(l => (l.Rfqno != null && EF.Functions.ILike(l.Rfqno, $"%{q}%"))
                                     || (l.BuyersName != null && EF.Functions.ILike(l.BuyersName, $"%{q}%")));

        if (!string.IsNullOrWhiteSpace(assignedTo))
        {
            var (userIds, _) = await ToolSchemas.ResolveAssigneesAsync(_db, ctx.BusinessUnitId, assignedTo, ct);
            if (userIds.Count == 0)
                return AgentToolResult.Ok(new
                {
                    total = 0, page, pageSize = size, items = Array.Empty<object>(),
                    note = $"No user in this tenant matched '{assignedTo}'."
                });
            query = query.Where(l => l.AssignTo != null && userIds.Contains(l.AssignTo.Value));
        }

        if (overdueOnly)
            query = query.Where(l => l.BidClosingDate != null
                                     && l.BidClosingDate.Value.Year >= ToolSchemas.SentinelYearFloor
                                     && l.BidClosingDate < now
                                     && l.LeadRejectedReasonId == null);

        if (staleOnly)
        {
            // Stale lead = accepted, never rejected, no RFQ produced yet, and
            // older than the tenant's stale threshold (age from assignment when
            // known, else last touch, else creation).
            var acceptedIds = await ToolSchemas.GetStatusIdsAsync(_db, "LeadStatus", "ACCEPTED", legacyId: 24, ct);
            var staleDays = await ToolSchemas.GetStaleQuoteDaysAsync(_db, ctx.BusinessUnitId, ct);
            var cutoff = now.AddDays(-staleDays);
            query = query.Where(l => l.LeadStatusId != null && acceptedIds.Contains(l.LeadStatusId.Value)
                                     && l.LeadRejectedReasonId == null
                                     && !l.Rfqs.Any()
                                     && (l.AssignOn ?? l.ModifiedDate ?? l.CreatedDate) < cutoff);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(l => l.RecDate)
            .Skip((page - 1) * size).Take(size)
            .Select(l => new
            {
                l.Id,
                rfqNo = l.Rfqno,
                buyer = l.BuyersName,
                source = l.LeadSource,
                recDate = l.RecDate,
                bidClosingDate = l.BidClosingDate,
                confidence = l.Aiconfidence,
                status = l.LeadStatus != null ? l.LeadStatus.SetupValue : null,
                assignedTo = l.AssignToNavigation != null
                    ? (l.AssignToNavigation.FirstName + " " + l.AssignToNavigation.LastName)
                    : null
            })
            .ToListAsync(ct);

        return AgentToolResult.Ok(new { total, page, pageSize = size, items = rows });
    }
}

public sealed class SearchQuotesTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public SearchQuotesTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.SearchQuotes;
    public string Description =>
        "Search quotations by quote number. Returns totals, validity and staleness. Optional filters: " +
        "assignedTo (a rep's name or email substring, e.g. \"Tomas\" — resolves the quote's owner), " +
        "overdueOnly (validity date passed with no outcome recorded), staleOnly (sent quotes the " +
        "customer has not answered for longer than the tenant's stale threshold).";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"query\":{\"type\":\"string\",\"description\":\"Free-text filter on quote number\"}," +
        "\"assignedTo\":{\"type\":\"string\",\"description\":\"Rep name or email substring; only quotes owned by matching users\"}," +
        "\"overdueOnly\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Only quotes whose validity date has passed with no outcome recorded\"}," +
        "\"staleOnly\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Only SENT quotes with no customer response past the tenant's stale threshold\"}," +
        "\"page\":{\"type\":\"integer\",\"minimum\":1,\"default\":1}," +
        "\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":10}}}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var assignedTo = input.GetStringOrNull("assignedTo");
        var overdueOnly = input.GetBool("overdueOnly");
        var staleOnly = input.GetBool("staleOnly");
        var (page, size) = ToolSchemas.Paging(input);
        var now = DateTime.UtcNow;

        var staleDays = await ToolSchemas.GetStaleQuoteDaysAsync(_db, ctx.BusinessUnitId, ct);

        // The tenant-scoped context's global query filter constrains rows to the
        // caller's BU; the extra filters below only narrow further.
        var query = _db.Set<Quote>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => EF.Functions.ILike(x.QuoteNo, $"%{q}%"));

        if (!string.IsNullOrWhiteSpace(assignedTo))
        {
            // Quote ownership is Quote.CreatedBy free text (email or "First Last"
            // — the SLA digest convention). Match resolved user identities exactly
            // plus the raw substring, so partially-typed names still resolve.
            var (_, identities) = await ToolSchemas.ResolveAssigneesAsync(_db, ctx.BusinessUnitId, assignedTo, ct);
            var pattern = $"%{assignedTo.Trim()}%";
            query = query.Where(x => identities.Contains(x.CreatedBy)
                                     || EF.Functions.ILike(x.CreatedBy, pattern));
        }

        if (overdueOnly)
            query = query.Where(x => x.ValidUntil != null && x.ValidUntil < now && x.OutcomeOn == null);

        if (staleOnly)
        {
            var sentIds = await ToolSchemas.GetStatusIdsAsync(_db, "QuoteStatus", "SENT", legacyId: 43, ct);
            var cutoff = now.AddDays(-staleDays);
            query = query.Where(x => x.StatusId != null && sentIds.Contains(x.StatusId.Value)
                                     && x.SentOn != null && x.RespondedOn == null
                                     && x.SentOn < cutoff);
        }

        var total = await query.CountAsync(ct);
        var fetched = await query
            .OrderByDescending(x => x.QuoteDate)
            .Skip((page - 1) * size).Take(size)
            .Select(x => new
            {
                x.Id,
                x.QuoteNo,
                x.Rfqid,
                x.TotalAmount,
                x.QuoteDate,
                x.ValidUntil,
                x.SentOn,
                x.RespondedOn,
                x.CreatedBy,
                StatusValue = x.Status != null ? x.Status.SetupValue : null,
                StatusCode = x.Status != null ? x.Status.SetupCode : null
            })
            .ToListAsync(ct);

        var rows = fetched.Select(x => new
        {
            id = x.Id,
            quoteNo = x.QuoteNo,
            rfqId = x.Rfqid,
            totalAmount = x.TotalAmount,
            quoteDate = x.QuoteDate,
            validUntil = x.ValidUntil,
            sentOn = x.SentOn,
            owner = x.CreatedBy,
            status = x.StatusValue,
            isStale = ERP_RFQ_Automation.Sla.SlaComputed.IsStale(x.StatusCode, x.SentOn, x.RespondedOn, staleDays, now),
            daysSinceSent = ERP_RFQ_Automation.Sla.SlaComputed.DaysSinceSent(x.SentOn, now)
        }).ToList();

        return AgentToolResult.Ok(new { total, page, pageSize = size, items = rows });
    }
}

public sealed class SearchOrdersTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public SearchOrdersTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.SearchOrders;
    public string Description => "Search purchase/sales orders by order number. Returns totals and dates.";
    public string InputJsonSchema => ToolSchemas.Search;
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var (page, size) = ToolSchemas.Paging(input);

        var query = _db.Set<Order>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(o => EF.Functions.ILike(o.OrderNo, $"%{q}%"));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * size).Take(size)
            .Select(o => new
            {
                o.Id,
                orderNo = o.OrderNo,
                quoteId = o.QuoteId,
                totalAmount = o.TotalAmount,
                orderDate = o.OrderDate,
                deliveryDate = o.DeliveryDate,
                status = o.Status != null ? o.Status.SetupValue : null
            })
            .ToListAsync(ct);

        return AgentToolResult.Ok(new { total, page, pageSize = size, items = rows });
    }
}

public sealed class GetDashboardSummaryTool : IAgentTool
{
    private readonly IDashboardRepository _dashboard;
    public GetDashboardSummaryTool(IDashboardRepository dashboard) => _dashboard = dashboard;

    public string Name => AgentToolNames.GetDashboardSummary;
    public string Description => "Get the tenant's dashboard KPI summary (leads, RFQs, quotes, orders, revenue).";
    public string InputJsonSchema => ToolSchemas.Empty;
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var data = await _dashboard.GetDashboardDataAsync(ctx.BusinessUnitId);
        return AgentToolResult.Ok(data);
    }
}
