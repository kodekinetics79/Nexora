using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Agent.Sourcing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Queue an RFQ for governed supplier dispatch. The procurement application service
/// atomically records the solicitation, event, and outbox message; only the outbox
/// worker is allowed to perform the external delivery.
/// </summary>
public sealed class SendRfqToSuppliersTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    private readonly IProcurementApplicationService _procurement;

    public SendRfqToSuppliersTool(ErpRfqAutomationContext db, IProcurementApplicationService procurement)
    {
        _db = db;
        _procurement = procurement;
    }

    public string Name => AgentToolNames.SendRfqToSuppliers;
    public string Description =>
        "Queue an RFQ invitation to multiple suppliers. Each invitation is recorded with an auditable " +
        "outbox message before the delivery worker contacts the supplier.";
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
            .Select(r => new { r.Id, r.Rfqno, r.BidClosingDate })
            .FirstOrDefaultAsync(ct);
        if (rfq is null) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        var rfqItemIds = await _db.Set<Rfqitem>().AsNoTracking()
            .Where(item => item.Rfqid == rfq.Id)
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync(ct);
        if (rfqItemIds.Length == 0) return AgentToolResult.Fail("The RFQ has no lines to solicit.");

        var suppliers = await _db.Set<Supplier>().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.ContactEmail })
            .ToListAsync(ct);
        var byId = suppliers.ToDictionary(s => s.Id);

        var results = new List<object>();
        var solicitedCount = 0;
        foreach (var sid in supplierIds.Distinct())
        {
            if (!byId.TryGetValue(sid, out var supplier))
            {
                results.Add(new { supplierId = sid, solicited = false, notified = false, reason = "supplier not found in this tenant" });
                continue;
            }

            var semanticHash = SemanticHash($"rfq={rfq.Id}|supplier={sid}|lines={string.Join(',', rfqItemIds)}|due={rfq.BidClosingDate:O}");
            var idempotencyKey = $"agent-solicit:{semanticHash}";
            try
            {
                var result = await _procurement.CreateSolicitationAsync(new CreateSolicitationCommand(
                    ctx.BusinessUnitId,
                    rfq.Id,
                    supplier.Id,
                    rfqItemIds,
                    rfq.BidClosingDate,
                    idempotencyKey,
                    Actor(ctx),
                    $"agent-solicit:{semanticHash}"), ct);

                results.Add(new
                {
                    supplierId = sid,
                    supplierName = supplier.Name,
                    solicited = true,
                    queued = true,
                    notified = false,
                    replayed = result.Replayed,
                    solicitationId = result.Id,
                    status = result.Status
                });
                solicitedCount++;
            }
            catch (Exception exception) when (exception is ProcurementValidationException or ProcurementConflictException)
            {
                results.Add(new
                {
                    supplierId = sid,
                    supplierName = supplier.Name,
                    solicited = false,
                    queued = false,
                    notified = false,
                    reason = exception.Message
                });
            }
        }

        return AgentToolResult.Ok(new
        {
            rfqId = rfq.Id,
            rfqNo = rfq.Rfqno,
            requested = supplierIds.Distinct().Count(),
            solicited = solicitedCount,
            recipients = results
        });
    }

    private static string Actor(AgentToolContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.UserName) ? $"agent:{ctx.UserId?.ToString() ?? "system"}" : ctx.UserName.Trim();

    private static string SemanticHash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

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
/// Supplier quote capture remains unavailable to agents until the platform can verify
/// a non-forgeable approval token against immutable stored evidence.
/// </summary>
public sealed class CaptureSupplierQuoteTool : IAgentTool
{
    public CaptureSupplierQuoteTool(ErpRfqAutomationContext db, IProcurementApplicationService procurement)
    {
        _ = db;
        _ = procurement;
    }

    public string Name => AgentToolNames.CaptureSupplierQuote;
    public string Description =>
        "Supplier quote capture is disabled for agents. Use the authenticated procurement workflow " +
        "until stored evidence and a non-forgeable approval token can be verified.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"supplierId\":{\"type\":\"integer\"}," +
        "\"supplierQuoteReference\":{\"type\":\"string\"}," +
        "\"evidenceReference\":{\"type\":\"string\",\"pattern\":\"^sha256:[A-Fa-f0-9]{64}$\"}," +
        "\"humanAuthorized\":{\"type\":\"boolean\",\"const\":true}," +
        "\"revision\":{\"type\":\"integer\",\"minimum\":1}," +
        "\"lines\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"rfqItemId\":{\"type\":\"integer\"}," +
        "\"unitPrice\":{\"type\":\"number\"}," +
        "\"quantity\":{\"type\":\"number\"}," +
        "\"leadTimeDays\":{\"type\":\"integer\"}," +
        "\"currency\":{\"type\":\"integer\"}," +
        "\"availableQuantity\":{\"type\":\"number\"}," +
        "\"freightCost\":{\"type\":\"number\"}," +
        "\"dutyCost\":{\"type\":\"number\"}," +
        "\"otherCost\":{\"type\":\"number\"}," +
        "\"taxAmount\":{\"type\":\"number\"}," +
        "\"discountAmount\":{\"type\":\"number\"}," +
        "\"minimumOrderQuantity\":{\"type\":\"number\"}," +
        "\"reliabilitySnapshot\":{\"type\":\"number\"}}," +
        "\"required\":[\"rfqItemId\",\"unitPrice\"]}}," +
        "\"validUntil\":{\"type\":\"string\"}}," +
        "\"required\":[\"rfqId\",\"supplierId\",\"supplierQuoteReference\",\"evidenceReference\",\"humanAuthorized\",\"validUntil\",\"lines\"]}";
    public bool IsMutation => true;

    public Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
        => Task.FromResult(AgentToolResult.Fail(
            "Agent supplier quote capture is disabled until a non-forgeable approval token can be " +
            "verified against immutable stored evidence. Use the authenticated procurement workflow."));
}

/// <summary>
/// Read-only per-line comparison matrix of captured supplier quotes for an RFQ, with a
/// shared multi-criteria score per supplier per line and an overall recommendation.
/// Reads authoritative quotes captured through the authenticated procurement workflow.
/// </summary>
public sealed class CompareSupplierQuotesTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public CompareSupplierQuotesTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.CompareSupplierQuotes;
    public string Description =>
        "Compare commercially eligible supplier quotes for an RFQ line-by-line, scoring authoritative landed cost, " +
        "lead time and success rate, and highlighting the best option per line plus an overall recommendation.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"rfqId\":{\"type\":\"integer\"}},\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        var rfqExists = await _db.Set<Rfq>().AsNoTracking().AnyAsync(r => r.Id == rfqId.Value, ct);
        if (!rfqExists) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        // Keep the security and commercial lineage explicit. QuoteReference is the
        // supplier's own document reference and is never an ownership boundary.
        var now = DateTime.UtcNow;
        var candidates = await _db.Set<SupplierQuotedItem>().AsNoTracking()
            .Where(q => q.BusinessUnitId == ctx.BusinessUnitId
                        && q.IsActive
                        && q.RfqId == rfqId.Value
                        && q.RfqItemId != null)
            .Select(q => new
            {
                q.Id,
                q.SupplierId,
                q.UnitPrice,
                q.LandedUnitCost,
                q.CurrencyId,
                q.Quantity,
                q.AvailableQuantity,
                q.MinimumOrderQuantity,
                q.RfqItemId,
                q.LeadTimeDays,
                q.ValidUntil,
                q.ItemName
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return AgentToolResult.Fail($"No supplier quotes were found for RFQ {rfqId}.");

        var requestedByLine = await _db.Set<Rfqitem>().AsNoTracking()
            .Where(line => line.Rfqid == rfqId.Value)
            .Select(line => new { line.Id, line.Quantity })
            .ToDictionaryAsync(line => line.Id, line => (decimal)line.Quantity, ct);

        var quoted = candidates.Where(q =>
        {
            if (q.ValidUntil is not null && q.ValidUntil <= now) return false;
            if (q.LandedUnitCost is null or <= 0m) return false;
            if (q.LeadTimeDays is null or < 0) return false;
            if (!requestedByLine.TryGetValue(q.RfqItemId!.Value, out var requested) || requested <= 0m) return false;
            if (q.Quantity < requested || q.AvailableQuantity is null || q.AvailableQuantity < requested) return false;
            if (q.MinimumOrderQuantity is > 0m && requested < q.MinimumOrderQuantity) return false;
            return true;
        }).ToList();

        if (quoted.Count == 0)
            return AgentToolResult.Fail(
                $"No eligible supplier quotes were found for RFQ {rfqId}. Quotes require current validity, " +
                "landed cost, lead time, sufficient quoted/available quantity, and a satisfied minimum order quantity.");

        var currencies = quoted.Select(q => q.CurrencyId).Distinct().ToArray();
        if (currencies.Length != 1 || currencies[0] is null)
            return AgentToolResult.Fail("Supplier quote comparison requires one common currency. Normalize mixed or missing currencies before comparison.");

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
            var itemId = q.RfqItemId!.Value;
            var requestedQuantity = requestedByLine[itemId];
            supById.TryGetValue(q.SupplierId, out var sup);
            var bid = new LineBid
            {
                SupplierId = q.SupplierId,
                SupplierName = sup?.Name ?? $"Supplier {q.SupplierId}",
                Price = q.LandedUnitCost!.Value * requestedQuantity,
                UnitPrice = q.UnitPrice ?? 0m,
                LandedUnitCost = q.LandedUnitCost.Value,
                Quantity = requestedQuantity,
                LeadTime = q.LeadTimeDays!.Value,
                SuccessRate = (double)(sup?.SuccessRate ?? 0m)
            };
            if (!byLine.TryGetValue(itemId, out var list)) { list = new(); byLine[itemId] = list; }
            list.Add(bid);
            lineNames[itemId] = q.ItemName;
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
                    b.LandedUnitCost,
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
            .ToDictionary(g => g.Key, g => g.Sum(x =>
                x.LandedUnitCost!.Value * requestedByLine[x.RfqItemId!.Value]));
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
        public decimal LandedUnitCost { get; set; }
        public decimal Quantity { get; set; }
        public double LeadTime { get; set; }
        public double SuccessRate { get; set; }
        public double Score { get; set; }
    }
}

/// <summary>
/// Records one quote-backed sourcing award only when tenant policy explicitly permits
/// autonomous awards. Human-required decisions remain in the authenticated workflow.
/// </summary>
public sealed class AwardRfqTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    private readonly IProcurementApplicationService _procurement;

    public AwardRfqTool(ErpRfqAutomationContext db, IProcurementApplicationService procurement)
    {
        _db = db;
        _procurement = procurement;
    }

    public string Name => AgentToolNames.AwardRfq;
    public string Description =>
        "Approve one authoritative supplier-quote line for an RFQ. Requires the persisted quote id and " +
        "current quote version; the tenant award cap is enforced against persisted landed cost.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"awards\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":1,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"supplierQuotedItemId\":{\"type\":\"integer\"}," +
        "\"expectedQuoteVersion\":{\"type\":\"integer\"}," +
        "\"rfqItemId\":{\"type\":\"integer\"}," +
        "\"quantity\":{\"type\":\"number\"}}," +
        "\"required\":[\"supplierQuotedItemId\",\"expectedQuoteVersion\",\"quantity\"]}}," +
        "\"rationale\":{\"type\":\"string\"}}," +
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

        if (awards.GetArrayLength() != 1)
            return AgentToolResult.Fail("Exactly one quote-backed award is supported per invocation.");

        var requested = awards.EnumerateArray().Single();
        var quoteId = requested.GetInt64OrNull("supplierQuotedItemId");
        var expectedVersion = requested.GetInt64OrNull("expectedQuoteVersion");
        var quantity = requested.GetDecimalOrNull("quantity");
        if (quoteId is null || expectedVersion is null || quantity is null)
            return AgentToolResult.Fail("supplierQuotedItemId, expectedQuoteVersion, and quantity are required.");

        var quote = await _db.Set<SupplierQuotedItem>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == quoteId.Value && row.BusinessUnitId == ctx.BusinessUnitId
                && row.RfqId == rfqId.Value && row.IsActive, ct);
        if (quote is null)
            return AgentToolResult.Fail("The authoritative supplier quote was not found for this RFQ and tenant.");
        if (quantity.Value <= 0m)
            return AgentToolResult.Fail("Award quantity must be positive.");
        if (quote.LandedUnitCost is null or <= 0m)
            return AgentToolResult.Fail("The authoritative supplier quote has no valid landed unit cost.");

        var policy = await _db.Set<AgentPolicy>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessUnitId == ctx.BusinessUnitId, ct)
            ?? AgentPolicy.Default(ctx.BusinessUnitId);
        if (policy.AutonomyLevel != AgentAutonomyLevel.Act || policy.RequireApprovalForAwards)
            return AgentToolResult.Fail(
                "This supplier award requires human approval. Complete it in the authenticated procurement workflow.");

        var authoritativeTotal = quote.LandedUnitCost.Value * quantity.Value;
        if (authoritativeTotal > policy.MaxAutoAwardValue)
            return AgentToolResult.Fail(
                $"Authoritative award value {authoritativeTotal:0.##} exceeds the tenant agent cap " +
                $"{policy.MaxAutoAwardValue:0.##}. Complete this award in the authenticated procurement workflow.");

        var semanticHash = SemanticHash(JsonSerializer.Serialize(new
        {
            QuoteId = quoteId.Value,
            ExpectedVersion = expectedVersion.Value,
            Quantity = quantity.Value,
            Rationale = input.GetStringOrNull("rationale")?.Trim()
        }));
        try
        {
            var result = await _procurement.ApproveAwardAsync(new ApproveAwardCommand(
                ctx.BusinessUnitId,
                quoteId.Value,
                quantity.Value,
                expectedVersion.Value,
                $"agent-award:{semanticHash}",
                Actor(ctx),
                $"agent-award:{semanticHash}",
                ctx.UserId,
                input.GetStringOrNull("rationale")), ct);
            return AgentToolResult.Ok(new
            {
                rfqId = rfqId.Value,
                awardsRecorded = 1,
                totalValue = authoritativeTotal,
                replayed = result.Replayed,
                awards = new[] { new { result.Id, supplierQuotedItemId = quoteId.Value, quantity = quantity.Value, result.Status } }
            });
        }
        catch (Exception exception) when (exception is ProcurementValidationException or ProcurementConflictException)
        {
            return AgentToolResult.Fail(exception.Message);
        }
    }

    private static string Actor(AgentToolContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.UserName) ? $"agent:{ctx.UserId?.ToString() ?? "system"}" : ctx.UserName.Trim();

    private static string SemanticHash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
