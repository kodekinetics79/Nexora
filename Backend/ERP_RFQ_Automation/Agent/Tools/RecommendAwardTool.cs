using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Advisory (read-only) multi-criteria award recommendation for an RFQ. Compares
/// the suppliers quoted against the RFQ's line items on total price, average lead
/// time and historical success rate, then recommends the best-scoring supplier.
/// Purely advisory — records nothing — so it is NOT a mutation and skips the
/// guardrail. Acting on the recommendation happens via create_order_from_quote /
/// dispatch_rfq_to_supplier, which ARE guarded.
/// </summary>
public sealed class RecommendAwardTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public RecommendAwardTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.RecommendAward;
    public string Description =>
        "Recommend which supplier to award an RFQ to, using a weighted comparison of total price, " +
        "lead time and supplier success rate. Advisory only — does not place any order.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"rfqId\":{\"type\":\"integer\"}},\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        // Line items that carry a supplier + price form the candidate bids.
        var items = await _db.Set<Rfqitem>().AsNoTracking()
            .Where(i => i.Rfqid == rfqId.Value && i.SupplierId != null && i.UnitPrice != null)
            .Select(i => new
            {
                i.SupplierId,
                SupplierName = i.Supplier != null ? i.Supplier.Name : null,
                SuccessRate = i.Supplier != null ? i.Supplier.SuccessRate : null,
                i.UnitPrice,
                i.Quantity,
                i.LeadTime
            })
            .ToListAsync(ct);

        if (items.Count == 0)
            return AgentToolResult.Fail($"No priced supplier bids found for RFQ {rfqId}. Dispatch the RFQ to suppliers first.");

        var bySupplier = items
            .GroupBy(i => new { i.SupplierId, i.SupplierName, i.SuccessRate })
            .Select(g => new SupplierBid
            {
                SupplierId = g.Key.SupplierId!.Value,
                SupplierName = g.Key.SupplierName ?? $"Supplier {g.Key.SupplierId}",
                TotalPrice = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity),
                AvgLeadTime = g.Where(x => x.LeadTime.HasValue).Select(x => (double)x.LeadTime!.Value).DefaultIfEmpty(0).Average(),
                SuccessRate = (double)(g.Key.SuccessRate ?? 0m),
                LineCount = g.Count()
            })
            .ToList();

        // Normalize each criterion to [0,1] (lower price/lead better; higher success better).
        var minPrice = bySupplier.Min(b => b.TotalPrice);
        var maxPrice = bySupplier.Max(b => b.TotalPrice);
        var minLead = bySupplier.Min(b => b.AvgLeadTime);
        var maxLead = bySupplier.Max(b => b.AvgLeadTime);
        var maxSuccess = bySupplier.Max(b => b.SuccessRate);

        const double wPrice = 0.5, wLead = 0.25, wSuccess = 0.25;

        foreach (var b in bySupplier)
        {
            var priceScore = maxPrice == minPrice ? 1.0 : 1.0 - (double)((b.TotalPrice - minPrice) / (maxPrice - minPrice));
            var leadScore = maxLead == minLead ? 1.0 : 1.0 - ((b.AvgLeadTime - minLead) / (maxLead - minLead));
            var successScore = maxSuccess <= 0 ? 0.0 : b.SuccessRate / maxSuccess;
            b.Score = Math.Round(wPrice * priceScore + wLead * leadScore + wSuccess * successScore, 4);
        }

        var ranked = bySupplier.OrderByDescending(b => b.Score).ToList();
        var winner = ranked[0];

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            recommendedSupplierId = winner.SupplierId,
            recommendedSupplierName = winner.SupplierName,
            rationale =
                $"Best weighted score {winner.Score:0.###} (price 50%, lead time 25%, success rate 25%). " +
                $"Total price {winner.TotalPrice:0.##}, avg lead time {winner.AvgLeadTime:0.#} days, success rate {winner.SuccessRate:0.#}.",
            weights = new { price = wPrice, leadTime = wLead, successRate = wSuccess },
            comparison = ranked.Select(b => new
            {
                b.SupplierId,
                b.SupplierName,
                b.TotalPrice,
                b.AvgLeadTime,
                b.SuccessRate,
                b.LineCount,
                b.Score
            })
        });
    }

    private sealed class SupplierBid
    {
        public long SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public decimal TotalPrice { get; set; }
        public double AvgLeadTime { get; set; }
        public double SuccessRate { get; set; }
        public int LineCount { get; set; }
        public double Score { get; set; }
    }
}
