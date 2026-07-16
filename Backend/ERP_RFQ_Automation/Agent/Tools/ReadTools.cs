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
    public string Description => "Search inbound leads by RFQ number or buyer name.";
    public string InputJsonSchema => ToolSchemas.Search;
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var (page, size) = ToolSchemas.Paging(input);

        var query = _db.Set<Lead>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(l => (l.Rfqno != null && EF.Functions.ILike(l.Rfqno, $"%{q}%"))
                                     || (l.BuyersName != null && EF.Functions.ILike(l.BuyersName, $"%{q}%")));

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
                confidence = l.Aiconfidence,
                status = l.LeadStatus != null ? l.LeadStatus.SetupValue : null
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
    public string Description => "Search quotations by quote number. Returns totals and validity.";
    public string InputJsonSchema => ToolSchemas.Search;
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var q = input.GetStringOrNull("query");
        var (page, size) = ToolSchemas.Paging(input);

        var query = _db.Set<Quote>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => EF.Functions.ILike(x.QuoteNo, $"%{q}%"));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.QuoteDate)
            .Skip((page - 1) * size).Take(size)
            .Select(x => new
            {
                x.Id,
                quoteNo = x.QuoteNo,
                rfqId = x.Rfqid,
                totalAmount = x.TotalAmount,
                quoteDate = x.QuoteDate,
                validUntil = x.ValidUntil,
                status = x.Status != null ? x.Status.SetupValue : null
            })
            .ToListAsync(ct);

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
