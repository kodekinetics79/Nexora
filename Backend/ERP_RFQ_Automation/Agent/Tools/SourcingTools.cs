using System.Globalization;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Agent.Sourcing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Encodes/decodes the RFQ linkage we stash in <see cref="SupplierQuotedItem.QuoteReference"/>.
/// The existing SupplierQuotedItem model has no RfqId/RfqItemId/LeadTime columns and
/// must not be modified, so captured quotes carry that context in the reference string
/// (format: <c>rfq={id};item={id};lead={days}</c>). See SOURCING-WIRING.md.
/// </summary>
internal static class QuoteRef
{
    public static string Build(long rfqId, long rfqItemId, int? leadDays) =>
        $"rfq={rfqId};item={rfqItemId};lead={(leadDays?.ToString(CultureInfo.InvariantCulture) ?? "")}";

    public static (long? rfqId, long? rfqItemId, int? lead) Parse(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return (null, null, null);
        long? rfq = null, item = null; int? lead = null;
        foreach (var part in reference.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var val = kv[1].Trim();
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "rfq": if (long.TryParse(val, out var r)) rfq = r; break;
                case "item": if (long.TryParse(val, out var i)) item = i; break;
                case "lead": if (int.TryParse(val, out var l)) lead = l; break;
            }
        }
        return (rfq, item, lead);
    }
}

/// <summary>
/// Dispatch an RFQ to MANY suppliers at once, recording a tracked
/// <see cref="SupplierSolicitation"/> per supplier and emailing each via the
/// notifications module. Supersedes the single-supplier <c>dispatch_rfq_to_supplier</c>
/// for real, auditable multi-supplier sourcing. Mutation — guarded by
/// RequireApprovalForSupplierEmails.
/// </summary>
public sealed class SendRfqToSuppliersTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    private readonly INotificationService _notifications;

    public SendRfqToSuppliersTool(ErpRfqAutomationContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public string Name => AgentToolNames.SendRfqToSuppliers;
    public string Description =>
        "Send an RFQ invitation to multiple suppliers at once. Records a tracked solicitation per supplier " +
        "and emails each one. Use this to actually put an RFQ out to the market.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"supplierIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"minItems\":1}," +
        "\"message\":{\"type\":\"string\"}}," +
        "\"required\":[\"rfqId\",\"supplierIds\"]}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        var supplierIds = ReadLongArray(input, "supplierIds");
        if (supplierIds.Count == 0) return AgentToolResult.Fail("supplierIds must contain at least one supplier id.");

        // Rfq set IS tenant-filtered — this both loads context and enforces ownership.
        var rfq = await _db.Set<Rfq>().AsNoTracking()
            .Where(r => r.Id == rfqId.Value)
            .Select(r => new { r.Id, r.Rfqno, r.BuyersName, r.BidClosingDate, r.NoOfLineItems })
            .FirstOrDefaultAsync(ct);
        if (rfq is null) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        var suppliers = await _db.Set<Supplier>().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.ContactEmail })
            .ToListAsync(ct);
        var byId = suppliers.ToDictionary(s => s.Id);

        var message = input.GetStringOrNull("message") ?? "Please submit your best pricing and lead times.";
        var dueDate = rfq.BidClosingDate?.ToString("yyyy-MM-dd") ?? "As soon as possible";
        var now = DateTime.UtcNow;

        var results = new List<object>();
        var solicitedCount = 0;
        foreach (var sid in supplierIds.Distinct())
        {
            if (!byId.TryGetValue(sid, out var supplier))
            {
                results.Add(new { supplierId = sid, solicited = false, notified = false, reason = "supplier not found in this tenant" });
                continue;
            }

            var notified = false;
            string? note = null;
            if (string.IsNullOrWhiteSpace(supplier.ContactEmail))
            {
                note = "no contact email on file; solicitation recorded but not emailed";
            }
            else
            {
                notified = await _notifications.SendRfqToSupplierAsync(new RfqToSupplierNotification
                {
                    ToEmail = supplier.ContactEmail!,
                    ToName = supplier.Name,
                    BusinessUnitId = ctx.BusinessUnitId.ToString(),
                    SupplierName = supplier.Name,
                    RfqNumber = rfq.Rfqno,
                    RfqTitle = rfq.BuyersName ?? rfq.Rfqno,
                    ItemSummary = rfq.NoOfLineItems.HasValue ? $"{rfq.NoOfLineItems} line item(s)" : "See RFQ details",
                    DueDate = dueDate,
                    Message = message
                }, ct);
                if (!notified) note = "email dispatch failed; solicitation still recorded";
            }

            var solicitation = new SupplierSolicitation
            {
                BusinessUnitId = ctx.BusinessUnitId,
                RfqId = rfq.Id,
                SupplierId = supplier.Id,
                Status = SolicitationStatus.Sent,
                SentOn = now,
                Channel = "Email",
                Notes = note
            };
            _db.Set<SupplierSolicitation>().Add(solicitation);
            solicitedCount++;
            results.Add(new { supplierId = supplier.Id, supplierName = supplier.Name, email = supplier.ContactEmail, solicited = true, notified, reason = note });
        }

        await _db.SaveChangesAsync(ct);

        return AgentToolResult.Ok(new
        {
            rfqId = rfq.Id,
            rfqNo = rfq.Rfqno,
            requested = supplierIds.Distinct().Count(),
            solicited = solicitedCount,
            recipients = results
        });
    }

    private static List<long> ReadLongArray(JsonElement input, string prop)
    {
        var list = new List<long>();
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) list.Add(n);
                else if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s)) list.Add(s);
            }
        }
        return list;
    }
}

/// <summary>Read-only: who an RFQ was solicited to and their response status.</summary>
public sealed class ListSolicitationsTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public ListSolicitationsTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.ListSolicitations;
    public string Description => "List the suppliers an RFQ was sent to and each solicitation's status (Sent/Responded/Declined/Expired).";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"rfqId\":{\"type\":\"integer\"}},\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        // SupplierSolicitation is tenant-filtered globally; join supplier names client-side.
        var rows = await _db.Set<SupplierSolicitation>().AsNoTracking()
            .Where(s => s.RfqId == rfqId.Value)
            .OrderBy(s => s.SentOn)
            .Select(s => new { s.Id, s.SupplierId, s.Status, s.SentOn, s.RespondedOn, s.Channel, s.Notes })
            .ToListAsync(ct);

        var supplierIds = rows.Select(r => r.SupplierId).Distinct().ToList();
        var names = await _db.Set<Supplier>().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);
        var nameById = names.ToDictionary(n => n.Id, n => n.Name);

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            total = rows.Count,
            solicitations = rows.Select(r => new
            {
                r.Id,
                r.SupplierId,
                supplierName = nameById.TryGetValue(r.SupplierId, out var nm) ? nm : $"Supplier {r.SupplierId}",
                status = r.Status.ToString(),
                sentOn = r.SentOn,
                respondedOn = r.RespondedOn,
                channel = r.Channel,
                notes = r.Notes
            })
        });
    }
}

/// <summary>
/// Capture a supplier's returned quote as <see cref="SupplierQuotedItem"/> rows and
/// mark the matching solicitation Responded. Lets a demo close the loop without inbound
/// email parsing. Mutation — data entry; allowed at Act, approval at Suggest.
/// </summary>
public sealed class CaptureSupplierQuoteTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public CaptureSupplierQuoteTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.CaptureSupplierQuote;
    public string Description =>
        "Record a supplier's quote against an RFQ (one or more priced lines). Persists supplier-quoted items " +
        "and marks that supplier's solicitation as Responded.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"supplierId\":{\"type\":\"integer\"}," +
        "\"lines\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"rfqItemId\":{\"type\":\"integer\"}," +
        "\"unitPrice\":{\"type\":\"number\"}," +
        "\"quantity\":{\"type\":\"number\"}," +
        "\"leadTimeDays\":{\"type\":\"integer\"}," +
        "\"currency\":{\"type\":\"string\"}}," +
        "\"required\":[\"rfqItemId\",\"unitPrice\"]}}," +
        "\"validUntil\":{\"type\":\"string\"}}," +
        "\"required\":[\"rfqId\",\"supplierId\",\"lines\"]}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        var supplierId = input.GetInt64OrNull("supplierId");
        if (rfqId is null || supplierId is null)
            return AgentToolResult.Fail("rfqId and supplierId are required.");

        // Ownership check via tenant-filtered Rfq + Supplier sets.
        var rfqExists = await _db.Set<Rfq>().AsNoTracking().AnyAsync(r => r.Id == rfqId.Value, ct);
        if (!rfqExists) return AgentToolResult.Fail($"RFQ {rfqId} not found.");
        var supplierExists = await _db.Set<Supplier>().AsNoTracking().AnyAsync(s => s.Id == supplierId.Value, ct);
        if (!supplierExists) return AgentToolResult.Fail($"Supplier {supplierId} not found.");

        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0)
            return AgentToolResult.Fail("lines must contain at least one quoted line.");

        // RFQ line items provide fallback name/quantity. Rfqitem is not tenant-filtered,
        // so constrain to this RFQ (already ownership-checked above).
        var rfqItems = await _db.Set<Rfqitem>().AsNoTracking()
            .Where(i => i.Rfqid == rfqId.Value)
            .Select(i => new { i.Id, i.ProductShortName, i.Quantity, i.UomId, i.CurrencyId })
            .ToListAsync(ct);
        var rfqItemById = rfqItems.ToDictionary(i => i.Id);

        var validUntil = ParseDate(input.GetStringOrNull("validUntil"));
        var now = DateTime.UtcNow;
        var createdBy = ctx.UserName ?? "agent";

        var created = new List<SupplierQuotedItem>();
        foreach (var line in lines.EnumerateArray())
        {
            var rfqItemId = line.GetInt64OrNull("rfqItemId");
            var unitPrice = line.GetDecimalOrNull("unitPrice");
            if (rfqItemId is null || unitPrice is null)
                return AgentToolResult.Fail("each line requires rfqItemId and unitPrice.");

            rfqItemById.TryGetValue(rfqItemId.Value, out var refItem);
            var qty = line.GetDecimalOrNull("quantity") ?? (refItem is not null ? refItem.Quantity : 1m);
            var leadDays = (int?)line.GetInt64OrNull("leadTimeDays");
            // currency accepted as a numeric CurrencyId; a currency *code* string is not
            // persisted (SupplierQuotedItem has only CurrencyId). See SOURCING-WIRING.md.
            var currencyId = line.GetInt64OrNull("currency") ?? refItem?.CurrencyId;

            var sqi = new SupplierQuotedItem
            {
                SupplierId = supplierId.Value,
                ItemName = refItem?.ProductShortName,
                Quantity = qty,
                UnitPrice = unitPrice,
                CurrencyId = currencyId,
                UomId = refItem?.UomId,
                QuoteReference = QuoteRef.Build(rfqId.Value, rfqItemId.Value, leadDays),
                QuoteDate = now,
                ValidUntil = validUntil,
                CreatedBy = createdBy,
                CreatedDate = now,
                IsActive = true,
                BusinessUnitId = ctx.BusinessUnitId
            };
            _db.Set<SupplierQuotedItem>().Add(sqi);
            created.Add(sqi);
        }

        // Mark the matching Sent solicitation as Responded.
        var solicitation = await _db.Set<SupplierSolicitation>()
            .Where(s => s.RfqId == rfqId.Value && s.SupplierId == supplierId.Value && s.Status == SolicitationStatus.Sent)
            .OrderByDescending(s => s.SentOn)
            .FirstOrDefaultAsync(ct);
        var solicitationUpdated = false;
        if (solicitation is not null)
        {
            solicitation.Status = SolicitationStatus.Responded;
            solicitation.RespondedOn = now;
            solicitation.UpdatedOn = now;
            solicitationUpdated = true;
        }

        await _db.SaveChangesAsync(ct);

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            supplierId = supplierId.Value,
            linesCaptured = created.Count,
            supplierQuotedItemIds = created.Select(c => c.Id),
            solicitationUpdated
        });
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;
}

/// <summary>
/// Read-only per-line comparison matrix of captured supplier quotes for an RFQ, with a
/// shared multi-criteria score per supplier per line and an overall recommendation.
/// Reads the quotes captured via <c>capture_supplier_quote</c> (SupplierQuotedItem).
/// </summary>
public sealed class CompareSupplierQuotesTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public CompareSupplierQuotesTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.CompareSupplierQuotes;
    public string Description =>
        "Compare captured supplier quotes for an RFQ line-by-line, scoring each supplier on price, lead time " +
        "and success rate, and highlighting the best option per line plus an overall recommendation.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"rfqId\":{\"type\":\"integer\"}},\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        var rfqExists = await _db.Set<Rfq>().AsNoTracking().AnyAsync(r => r.Id == rfqId.Value, ct);
        if (!rfqExists) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        // SupplierQuotedItem is NOT globally tenant-filtered — scope explicitly.
        var reference = $"rfq={rfqId.Value};";
        var quoted = await _db.Set<SupplierQuotedItem>().AsNoTracking()
            .Where(q => q.BusinessUnitId == ctx.BusinessUnitId
                        && q.IsActive
                        && q.QuoteReference != null
                        && q.QuoteReference.StartsWith(reference))
            .Select(q => new { q.Id, q.SupplierId, q.UnitPrice, q.Quantity, q.QuoteReference, q.ItemName })
            .ToListAsync(ct);

        if (quoted.Count == 0)
            return AgentToolResult.Fail($"No captured supplier quotes found for RFQ {rfqId}. Capture supplier quotes first.");

        var supplierIds = quoted.Select(q => q.SupplierId).Distinct().ToList();
        var suppliers = await _db.Set<Supplier>().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.SuccessRate })
            .ToListAsync(ct);
        var supById = suppliers.ToDictionary(s => s.Id);

        // Build per-line bids.
        var byLine = new Dictionary<long, List<LineBid>>();
        var lineNames = new Dictionary<long, string?>();
        foreach (var q in quoted)
        {
            var (_, itemId, lead) = QuoteRef.Parse(q.QuoteReference);
            if (itemId is null) continue;
            supById.TryGetValue(q.SupplierId, out var sup);
            var bid = new LineBid
            {
                SupplierId = q.SupplierId,
                SupplierName = sup?.Name ?? $"Supplier {q.SupplierId}",
                Price = (q.UnitPrice ?? 0m) * q.Quantity,
                UnitPrice = q.UnitPrice ?? 0m,
                Quantity = q.Quantity,
                LeadTime = lead ?? 0,
                SuccessRate = (double)(sup?.SuccessRate ?? 0m)
            };
            if (!byLine.TryGetValue(itemId.Value, out var list)) { list = new(); byLine[itemId.Value] = list; }
            list.Add(bid);
            lineNames[itemId.Value] = q.ItemName;
        }

        var lineWins = new Dictionary<long, int>();
        var lineResults = new List<object>();
        foreach (var (itemId, bids) in byLine.OrderBy(kv => kv.Key))
        {
            SupplierScoring.ScoreInPlace(bids);
            var ranked = bids.OrderByDescending(b => b.Score).ToList();
            var best = ranked[0];
            lineWins[best.SupplierId] = lineWins.GetValueOrDefault(best.SupplierId) + 1;

            lineResults.Add(new
            {
                rfqItemId = itemId,
                itemName = lineNames.GetValueOrDefault(itemId),
                bestSupplierId = best.SupplierId,
                bestSupplierName = best.SupplierName,
                bids = ranked.Select(b => new
                {
                    b.SupplierId,
                    b.SupplierName,
                    b.UnitPrice,
                    b.Quantity,
                    lineTotal = b.Price,
                    leadTimeDays = b.LeadTime,
                    b.SuccessRate,
                    b.Score,
                    isBest = b.SupplierId == best.SupplierId
                })
            });
        }

        // Overall: supplier winning most lines, tiebreak by lowest aggregate priced total.
        var totalsBySupplier = quoted
            .GroupBy(q => q.SupplierId)
            .ToDictionary(g => g.Key, g => g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity));
        var overall = lineWins
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => totalsBySupplier.GetValueOrDefault(kv.Key))
            .Select(kv => kv.Key)
            .FirstOrDefault();

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            weights = SupplierScoring.Weights,
            lineCount = byLine.Count,
            recommendedSupplierId = overall,
            recommendedSupplierName = supById.TryGetValue(overall, out var ov) ? ov.Name : null,
            rationale = $"Supplier {overall} wins {lineWins.GetValueOrDefault(overall)} of {byLine.Count} line(s) on the weighted score (price 50%, lead time 25%, success rate 25%).",
            lines = lineResults
        });
    }

    private sealed class LineBid : IScoreCandidate
    {
        public long SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Quantity { get; set; }
        public double LeadTime { get; set; }
        public double SuccessRate { get; set; }
        public double Score { get; set; }
    }
}

/// <summary>
/// Record the sourcing award decision as <see cref="SourcingAward"/> rows. Mutation —
/// guarded by RequireApprovalForAwards and the MaxAutoAwardValue cap (checked against
/// the award total).
/// </summary>
public sealed class AwardRfqTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public AwardRfqTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.AwardRfq;
    public string Description =>
        "Record the award decision for an RFQ: one or more line/supplier awards at agreed unit prices. " +
        "This is the sourcing decision — subject to the award value cap and approval policy.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"awards\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"rfqItemId\":{\"type\":\"integer\"}," +
        "\"supplierId\":{\"type\":\"integer\"}," +
        "\"unitPrice\":{\"type\":\"number\"}," +
        "\"quantity\":{\"type\":\"number\"}}," +
        "\"required\":[\"supplierId\",\"unitPrice\"]}}," +
        "\"rationale\":{\"type\":\"string\"}," +
        "\"totalValue\":{\"type\":\"number\",\"description\":\"Optional award total hint for guardrail checks; defaults to sum(unitPrice*quantity)\"}}," +
        "\"required\":[\"rfqId\",\"awards\"]}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        var rfqExists = await _db.Set<Rfq>().AsNoTracking().AnyAsync(r => r.Id == rfqId.Value, ct);
        if (!rfqExists) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("awards", out var awards) || awards.ValueKind != JsonValueKind.Array || awards.GetArrayLength() == 0)
            return AgentToolResult.Fail("awards must contain at least one award.");

        var rationale = input.GetStringOrNull("rationale");
        var now = DateTime.UtcNow;
        var actedByAgent = ctx.UserId is null; // no acting user => autonomous agent action

        var created = new List<SourcingAward>();
        decimal grandTotal = 0m;
        foreach (var a in awards.EnumerateArray())
        {
            var supplierId = a.GetInt64OrNull("supplierId");
            var unitPrice = a.GetDecimalOrNull("unitPrice");
            if (supplierId is null || unitPrice is null)
                return AgentToolResult.Fail("each award requires supplierId and unitPrice.");

            var qty = a.GetDecimalOrNull("quantity");
            var total = unitPrice.Value * (qty ?? 1m);
            grandTotal += total;

            var award = new SourcingAward
            {
                BusinessUnitId = ctx.BusinessUnitId,
                RfqId = rfqId.Value,
                RfqItemId = a.GetInt64OrNull("rfqItemId"),
                SupplierId = supplierId.Value,
                UnitPrice = unitPrice.Value,
                Quantity = qty,
                TotalValue = total,
                Rationale = rationale,
                AwardedByUserId = ctx.UserId,
                AwardedByAgent = actedByAgent
            };
            _db.Set<SourcingAward>().Add(award);
            created.Add(award);
        }

        await _db.SaveChangesAsync(ct);

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            awardsRecorded = created.Count,
            totalValue = grandTotal,
            awards = created.Select(c => new
            {
                c.Id,
                c.RfqItemId,
                c.SupplierId,
                c.UnitPrice,
                c.Quantity,
                c.TotalValue,
                c.AwardedByAgent
            })
        });
    }
}
