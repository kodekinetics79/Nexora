using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierEvaluation;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Retained as an explicit fail-closed boundary for older agent plans. Supplier
/// outreach now requires an operator-approved Sourcing Case command.
/// </summary>
public sealed class SendRfqToSuppliersTool : IAgentTool
{
    public SendRfqToSuppliersTool(ErpRfqAutomationContext db, IProcurementApplicationService procurement)
    {
        _ = db;
        _ = procurement;
    }

    public string Name => AgentToolNames.SendRfqToSuppliers;
    public string Description =>
        "Supplier outreach requires a persisted Sourcing Case, governed candidate selection, and explicit operator approval.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"supplierIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"minItems\":1}," +
        "\"message\":{\"type\":\"string\"}}," +
        "\"required\":[\"rfqId\",\"supplierIds\"]}";
    public bool IsMutation => true;

    public Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        _ = ctx;
        _ = ct;
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return Task.FromResult(AgentToolResult.Fail("rfqId is required."));

        var supplierIds = ReadLongArray(input, "supplierIds");
        if (supplierIds.Count == 0)
            return Task.FromResult(AgentToolResult.Fail("supplierIds must contain at least one supplier id."));

        return Task.FromResult(AgentToolResult.Fail(
            "Direct agent dispatch is disabled. Use the Sourcing Case workspace to select governed candidates and explicitly approve numbered Supplier RFQs."));
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
                // NOT r.SentOn: an unsent solicitation stores DateTime.MinValue (-infinity in
                // Postgres) because the column is NOT NULL. Handing that to the model made it
                // report supplier RFQ 1 as "sent on 1 January 0001".
                sentOn = r.SentOn == default ? (DateTime?)null : r.SentOn,
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
///
/// <para><b>One recommender.</b> This tool used to rank on weights hardcoded at price 0.5 /
/// lead time 0.25 / success rate 0.25, while the comparison a human actually awards from
/// (<c>ProcurementApplicationService.CompareQuotesAsync</c>) ranked on coverage then landed cost.
/// Two recommenders could name different winners on the same line. Both now score through
/// <see cref="WeightedSupplierScoring"/> with the SAME tenant weight set, so the answer the agent
/// gives and the answer the workbench shows are the same answer.</para>
///
/// <para>Same weights AND the same inputs. All four criteria are read from the columns the governed
/// comparison reads, warranty included: the typed <c>SupplierQuoteLine.WarrantyMonths</c> on the
/// offer's canonical line, reached through the <c>SourceSupplierQuoteLineId</c> the quoted item
/// already carries. Offering the scorer a hardcoded null there would have re-opened the very split
/// this gate closed — the moment a tenant weighted warranty, this tool would refuse to rank offers
/// the workbench ranks fine.</para>
///
/// <para>Supplier success rate is no longer a scored criterion — it is an operator-typed
/// spreadsheet column, never a measured outcome, and scoring it presented a typed number as
/// performance evidence. It is still reported, as the display-only value it always was.</para>
/// </summary>
public sealed class CompareSupplierQuotesTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public CompareSupplierQuotesTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.CompareSupplierQuotes;
    public string Description =>
        "Compare commercially eligible supplier quotes for an RFQ line-by-line, scoring authoritative landed cost, " +
        "lead time, warranty and payment terms against the tenant's configured comparison weights, and highlighting " +
        "the best option per line plus an overall recommendation.";
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
                q.ItemName,
                // The offer's canonical evidence. Carried through so the warranty an operator typed
                // on that line can be scored here exactly as the governed comparison scores it.
                q.SourceSupplierQuoteLineId
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
            .Select(s => new { s.Id, s.Name, s.SuccessRate, s.CreditDays })
            .ToListAsync(ct);
        var supById = suppliers.ToDictionary(s => s.Id);

        // The warranty period an operator typed on the canonical supplier-quote line — the SAME
        // column the governed comparison scores from. Read by ids the rows above already carry, in
        // one keyed lookup beside the supplier one, so the candidate query gains no join.
        //
        // It used to be offered to the scorer as a literal null, on the belief that warranty was
        // free text and no number existed. It does now, and that null was the original two-recommender
        // defect coming back through a side door: a tenant that weights warranty would have had this
        // tool refuse to rank offers the workbench ranks fine, and tell the operator to capture a
        // value that was already captured.
        var quoteLineIds = quoted.Where(q => q.SourceSupplierQuoteLineId.HasValue)
            .Select(q => q.SourceSupplierQuoteLineId!.Value).Distinct().ToList();
        var warrantyMonthsByQuoteLineId = quoteLineIds.Count == 0
            ? new Dictionary<long, int?>()
            : await _db.Set<SupplierQuotes.SupplierQuoteLine>().AsNoTracking()
                .Where(l => l.BusinessUnitId == ctx.BusinessUnitId && quoteLineIds.Contains(l.Id))
                .Select(l => new { l.Id, l.WarrantyMonths })
                .ToDictionaryAsync(l => l.Id, l => l.WarrantyMonths, ct);

        // The tenant's weights, not this file's opinion. A trader whose customers award on delivery
        // date and one who awards on price are not running the same comparison, and neither of them
        // should be running the one a developer typed into a constant.
        var scoringWeights = await new SupplierComparisonWeightsService(_db)
            .ResolveAsync(ctx.BusinessUnitId, ct);

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
                CurrencyId = q.CurrencyId,
                Quantity = requestedQuantity,
                LeadTime = q.LeadTimeDays!.Value,
                // Null, not zero: a supplier whose credit days nobody has captured has not been
                // told to us as "pays cash", and the scorer must be able to tell those apart.
                CreditDays = sup?.CreditDays,
                // Same distinction again. Null means nobody typed a warranty period on the canonical
                // line — a real state, and the state of every line captured before the column existed.
                // 0 would assert the supplier offered no warranty at all.
                WarrantyMonths = q.SourceSupplierQuoteLineId is { } quoteLineId
                    && warrantyMonthsByQuoteLineId.TryGetValue(quoteLineId, out var months)
                        ? months
                        : null,
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
            var scored = WeightedSupplierScoring.Score(bids, scoringWeights);
            for (var i = 0; i < bids.Count; i++)
            {
                bids[i].Score = scored[i].Score;
                bids[i].ScoreUnavailableReason = scored[i].UnavailableReason;
                bids[i].Contributions = scored[i].Contributions;
            }

            // A bid with no score is not the worst bid; it is a bid whose weighted criteria are not
            // all captured. It ranks below the scored ones and it is still listed, still quoted and
            // still awardable by a human — it is simply not something this tool can rank.
            var ranked = bids
                .OrderByDescending(b => b.Score.HasValue).ThenByDescending(b => b.Score ?? 0d)
                .ThenBy(b => b.Price).ThenBy(b => b.SupplierId).ToList();
            var best = ranked[0].Score.HasValue ? ranked[0] : null;
            if (best is not null)
                lineWins[best.SupplierId] = lineWins.GetValueOrDefault(best.SupplierId) + 1;

            lineResults.Add(new
            {
                rfqItemId = itemId,
                itemName = lineNames.GetValueOrDefault(itemId),
                bestSupplierId = best?.SupplierId,
                bestSupplierName = best?.SupplierName,
                // Says why there is no winner rather than presenting the first row as one.
                bestUnavailableReason = best is null ? ranked[0].ScoreUnavailableReason : null,
                bids = ranked.Select(b => new
                {
                    b.SupplierId,
                    b.SupplierName,
                    b.UnitPrice,
                    b.LandedUnitCost,
                    b.Quantity,
                    lineTotal = b.Price,
                    leadTimeDays = b.LeadTime,
                    creditDays = b.CreditDays,
                    warrantyMonths = b.WarrantyMonths,
                    // Reported, never scored: an operator typed it, nothing measured it.
                    b.SuccessRate,
                    b.Score,
                    scoreOutOf = SupplierScoringWeights.MaximumScore,
                    scoreUnavailableReason = b.ScoreUnavailableReason,
                    scoreBreakdown = b.Contributions,
                    isBest = best is not null && b.SupplierId == best.SupplierId
                })
            });
        }

        // Overall: supplier winning most lines, tiebreak by lowest aggregate priced total. Only
        // lines with a scored winner count — a line nothing could be scored on elects nobody.
        var totalsBySupplier = quoted
            .GroupBy(q => q.SupplierId)
            .ToDictionary(g => g.Key, g => g.Sum(x =>
                x.LandedUnitCost!.Value * requestedByLine[x.RfqItemId!.Value]));
        long? overall = lineWins.Count == 0 ? null : lineWins
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => totalsBySupplier.GetValueOrDefault(kv.Key))
            .Select(kv => kv.Key)
            .First();

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            weights = new
            {
                price = scoringWeights.Price,
                leadTime = scoringWeights.LeadTime,
                warranty = scoringWeights.Warranty,
                paymentTerms = scoringWeights.PaymentTerms,
                outOf = SupplierScoringWeights.MaximumScore
            },
            lineCount = byLine.Count,
            recommendedSupplierId = overall,
            recommendedSupplierName = overall is not null && supById.TryGetValue(overall.Value, out var ov)
                ? ov.Name : null,
            rationale = overall is null
                ? $"No supplier is recommended for RFQ {rfqId}: none of the {byLine.Count} line(s) could be "
                  + "scored against the tenant's comparison weights. The quotes below are still valid and "
                  + "still awardable — capture the missing values, or move the weight off the criterion "
                  + "that is not captured, and the ranking will follow."
                : $"Supplier {overall} wins {lineWins.GetValueOrDefault(overall.Value)} of {byLine.Count} line(s) "
                  + $"on the tenant's weighted score (price {scoringWeights.Price}, lead time {scoringWeights.LeadTime}, "
                  + $"warranty {scoringWeights.Warranty}, payment terms {scoringWeights.PaymentTerms}, "
                  + $"out of {SupplierScoringWeights.MaximumScore}).",
            lines = lineResults
        });
    }

    private sealed class LineBid : IWeightedScoreCandidate
    {
        public long SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LandedUnitCost { get; set; }
        public long? CurrencyId { get; set; }
        public decimal Quantity { get; set; }
        public double LeadTime { get; set; }
        public int? CreditDays { get; set; }

        /// <summary>
        /// The typed warranty period from the canonical supplier-quote line, or null when nobody
        /// captured one. Never derived from the supplier's free-text warranty wording.
        /// </summary>
        public int? WarrantyMonths { get; set; }

        /// <summary>Reported to the operator, deliberately not scored. See the class remarks.</summary>
        public double SuccessRate { get; set; }

        public double? Score { get; set; }
        public string? ScoreUnavailableReason { get; set; }
        public IReadOnlyList<SupplierScoreContribution> Contributions { get; set; } = [];

        // Projection onto the governed scorer, from the same columns the governed comparison reads,
        // so both name the same winner on the same data. Warranty is the number an operator typed on
        // the canonical quote line — never a reading of the prose beside it — and it stays null where
        // nobody typed one, so at a non-zero warranty weight that bid reports it cannot be scored
        // rather than being credited with a warranty nobody stated.
        decimal? IWeightedScoreCandidate.Price => Price;
        double? IWeightedScoreCandidate.LeadTimeDays => LeadTime;
        double? IWeightedScoreCandidate.WarrantyMonths => WarrantyMonths;
        double? IWeightedScoreCandidate.CreditDays => CreditDays;
        long? IWeightedScoreCandidate.PriceCurrencyId => CurrencyId;
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

        // COMPARISON SITE 4. The total is denominated in the supplier quote's OWN currency
        // (quote.CurrencyId); the cap is denominated in policy.CurrencyId. Comparing the two
        // numbers directly — which is what this line used to do — made a cap of 10,000 stop a
        // 10,000 SAR award and a 10,000 USD award alike. AgentSpendCap converts with approved,
        // effective-dated FX evidence first, and refuses rather than guessing when it cannot.
        var authoritativeTotal = quote.LandedUnitCost.Value * quantity.Value;
        var capVerdict = await new AgentSpendCap(_db).EvaluateAsync(
            ctx.BusinessUnitId, authoritativeTotal, quote.CurrencyId, policy, policy.MaxAutoAwardValue,
            "award", DateTime.UtcNow, ct);
        if (!capVerdict.MayAutoExecute)
            return AgentToolResult.Fail(
                $"{capVerdict.Reason} Complete this award in the authenticated procurement workflow.");

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
