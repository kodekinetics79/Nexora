using System.Text.Json;
using ERP_RFQ_Automation.Agent;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// Copilot tool: multi-signal price recommendations for an RFQ (read-only).
/// NOT registered by AddPricingIntelligence — registration lines live in
/// PRICING-WIRING.md so the lead can splice them into the Agent tool set.
/// </summary>
public sealed class PriceRfqTool : IAgentTool
{
    public const string ToolName = "price_rfq";

    private readonly IPricingEngine _engine;
    public PriceRfqTool(IPricingEngine engine) => _engine = engine;

    public string Name => ToolName;
    public string Description =>
        "Compute recommended unit prices for every line of an RFQ using price lists, accepted quotes, " +
        "supplier quotes captured for the RFQ, purchase history and product master data. " +
        "Returns per-line recommendation, floor, confidence and a one-sentence rationale. Read-only.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\",\"description\":\"Id of the RFQ to price\"}}," +
        "\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        try
        {
            var preview = await _engine.PriceRfqAsync(rfqId.Value, ctx.BusinessUnitId, ct);

            // Compact shape for the model: rationale + numbers, no full signal payloads.
            return AgentToolResult.Ok(new
            {
                rfqId = preview.RfqId,
                currency = preview.Currency,
                overallConfidence = preview.OverallConfidence,
                recommendedTotal = preview.Totals.RecommendedTotal,
                linesNeedingAttention = preview.Lines.Count(l => l.NeedsAttention),
                lines = preview.Lines.Select(l => new
                {
                    rfqItemId = l.RfqItemId,
                    description = Truncate(l.Description, 80),
                    quantity = l.Quantity,
                    recommendedUnitPrice = l.RecommendedUnitPrice,
                    floorUnitPrice = l.FloorUnitPrice,
                    marginPct = l.MarginPct,
                    confidence = l.Confidence,
                    needsAttention = l.NeedsAttention,
                    rationale = l.Rationale
                })
            });
        }
        catch (KeyNotFoundException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// Copilot tool: persist unit prices onto the RFQ lines (Rfqitem.UnitPrice) so the
/// existing approve → quote flow picks them up. Mutation — routed through the guardrail
/// engine; until a dedicated case is added, the unknown-mutation fail-safe requires
/// human approval (see PRICING-WIRING.md for the suggested case + value-cap mapping).
/// </summary>
public sealed class ApplyRfqPricingTool : IAgentTool
{
    public const string ToolName = "apply_rfq_pricing";

    private readonly IPricingEngine _engine;
    public ApplyRfqPricingTool(IPricingEngine engine) => _engine = engine;

    public string Name => ToolName;
    public string Description =>
        "Apply unit prices to an RFQ's lines so the generated quote uses them. " +
        "Provide explicit lines [{rfqItemId, unitPrice}], or omit lines to apply the engine's " +
        "recommended prices (lines with no recommendation are skipped and reported).";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\",\"description\":\"Id of the RFQ to price\"}," +
        "\"lines\":{\"type\":\"array\",\"description\":\"Optional explicit prices; omit to use recommendations\"," +
        "\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"rfqItemId\":{\"type\":\"integer\"},\"unitPrice\":{\"type\":\"number\"}}," +
        "\"required\":[\"rfqItemId\",\"unitPrice\"]}}}," +
        "\"required\":[\"rfqId\"]}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        try
        {
            var request = new ApplyPricingRequest { AppliedBy = ctx.UserName ?? "copilot" };
            var skipped = 0;

            if (input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("lines", out var linesEl)
                && linesEl.ValueKind == JsonValueKind.Array
                && linesEl.GetArrayLength() > 0)
            {
                foreach (var el in linesEl.EnumerateArray())
                {
                    var itemId = el.GetInt64OrNull("rfqItemId");
                    var unitPrice = el.GetDecimalOrNull("unitPrice");
                    if (itemId is null || unitPrice is null)
                        return AgentToolResult.Fail("Each line must have rfqItemId and unitPrice.");
                    request.Lines.Add(new ApplyPricingLine { RfqItemId = itemId.Value, UnitPrice = unitPrice.Value });
                }
            }
            else
            {
                // Omitted lines = use the engine's recommendations. Lines the engine could
                // not price (no signal) are skipped and surfaced in the result.
                var preview = await _engine.PriceRfqAsync(rfqId.Value, ctx.BusinessUnitId, ct);
                foreach (var line in preview.Lines)
                {
                    if (line.RecommendedUnitPrice > 0)
                        request.Lines.Add(new ApplyPricingLine { RfqItemId = line.RfqItemId, UnitPrice = line.RecommendedUnitPrice });
                    else
                        skipped++;
                }
                if (request.Lines.Count == 0)
                    return AgentToolResult.Fail("No line could be priced from the available signals; nothing was applied.");
            }

            var result = await _engine.ApplyPricingAsync(rfqId.Value, ctx.BusinessUnitId, request, ct);

            return AgentToolResult.Ok(new
            {
                applied = result.Applied,
                total = result.Total,
                skipped
            });
        }
        catch (KeyNotFoundException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
    }
}
