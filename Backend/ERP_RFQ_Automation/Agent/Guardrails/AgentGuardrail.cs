using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Guardrails;

/// <summary>
/// Default guardrail engine. Precedence (highest first):
///   1. Per-tool override in <see cref="AgentPolicy.PerToolOverrides"/>.
///   2. Autonomy level (Observe denies all mutations; Suggest requires approval).
///   3. Category flags (RequireApprovalForAwards/Orders/SupplierEmails).
///   4. Value caps (MaxAutoAwardValue / MaxAutoOrderValue) for amount-bearing tools.
/// A tool that passes every gate at Act level is allowed to auto-execute.
/// </summary>
public sealed class AgentGuardrail : IAgentGuardrail
{
    private readonly ErpRfqAutomationContext _db;

    public AgentGuardrail(ErpRfqAutomationContext db) => _db = db;

    public async Task<AgentPolicy> GetPolicyAsync(long businessUnitId, CancellationToken ct)
    {
        var policy = await _db.Set<AgentPolicy>()
            .FirstOrDefaultAsync(p => p.BusinessUnitId == businessUnitId, ct);
        return policy ?? AgentPolicy.Default(businessUnitId);
    }

    public async Task<GuardrailDecision> EvaluateAsync(IAgentTool tool, JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        if (!tool.IsMutation)
            return GuardrailDecision.Allow("Read-only tool.");

        var policy = await GetPolicyAsync(ctx.BusinessUnitId, ct);

        // 1. Per-tool override wins outright.
        var overrideDecision = ReadOverride(policy.PerToolOverrides, tool.Name);
        if (overrideDecision is not null)
            return overrideDecision;

        // 2. Autonomy level.
        if (policy.AutonomyLevel == AgentAutonomyLevel.Observe)
            return GuardrailDecision.Deny("Autonomy level is Observe (read-only); mutations are not permitted.");

        if (policy.AutonomyLevel == AgentAutonomyLevel.Suggest)
            return GuardrailDecision.RequireApproval("Autonomy level is Suggest; this action requires human approval.");

        // 3 + 4. Act level — category flags and value caps.
        switch (tool.Name)
        {
            case AgentToolNames.DispatchRfqToSupplier:
                if (policy.RequireApprovalForSupplierEmails)
                    return GuardrailDecision.RequireApproval("Policy requires approval before emailing suppliers.");
                return GuardrailDecision.Allow("Act level; supplier emails allowed by policy.");

            case AgentToolNames.RecommendAward:
                if (policy.RequireApprovalForAwards)
                    return GuardrailDecision.RequireApproval("Policy requires approval for award recommendations.");
                return await EvaluateAmountCapAsync(input, "amount", policy.MaxAutoAwardValue, "award", ct);

            case AgentToolNames.CreateOrderFromQuote:
                if (policy.RequireApprovalForOrders)
                    return GuardrailDecision.RequireApproval("Policy requires approval before creating orders.");
                var orderAmount = await ResolveOrderAmountAsync(input, ct);
                return EvaluateCap(orderAmount, policy.MaxAutoOrderValue, "order");

            case AgentToolNames.SendRfqToSuppliers:
                if (policy.RequireApprovalForSupplierEmails)
                    return GuardrailDecision.RequireApproval("Policy requires approval before emailing suppliers.");
                return GuardrailDecision.Allow("Act level; supplier solicitations allowed by policy.");

            case AgentToolNames.AwardRfq:
                if (policy.RequireApprovalForAwards)
                    return GuardrailDecision.RequireApproval("Policy requires approval before recording awards.");
                return EvaluateCap(ResolveAwardTotal(input), policy.MaxAutoAwardValue, "award");

            case AgentToolNames.CaptureSupplierQuote:
                // Data entry: no category flag/value cap. Autonomy gate above already
                // denies at Observe and requires approval at Suggest; allow at Act.
                return GuardrailDecision.Allow("Act level; supplier quote capture (data entry) allowed.");

            default:
                // Unknown mutation — fail safe.
                return GuardrailDecision.RequireApproval("Unrecognized mutation; requires human approval.");
        }
    }

    private Task<GuardrailDecision> EvaluateAmountCapAsync(JsonElement input, string prop, decimal cap, string label, CancellationToken ct)
    {
        var amount = input.GetDecimalOrNull(prop) ?? 0m;
        return Task.FromResult(EvaluateCap(amount, cap, label));
    }

    private static GuardrailDecision EvaluateCap(decimal amount, decimal cap, string label)
    {
        if (amount > cap)
            return GuardrailDecision.RequireApproval(
                $"{label} value {amount:0.##} exceeds the auto-execute cap {cap:0.##}; requires approval.");
        return GuardrailDecision.Allow($"{label} value {amount:0.##} within the auto-execute cap {cap:0.##}.");
    }

    /// <summary>Order value = explicit input amount, else the quote total from the DB.</summary>
    private async Task<decimal> ResolveOrderAmountAsync(JsonElement input, CancellationToken ct)
    {
        var explicitAmount = input.GetDecimalOrNull("amount");
        if (explicitAmount.HasValue) return explicitAmount.Value;

        var quoteId = input.GetInt64OrNull("quoteId");
        if (quoteId is null) return 0m;

        var total = await _db.Set<Quote>()
            .Where(q => q.Id == quoteId.Value)
            .Select(q => q.TotalAmount)
            .FirstOrDefaultAsync(ct);
        return total ?? 0m;
    }

    /// <summary>Award value = explicit totalValue/amount hint, else sum(unitPrice*quantity) over the awards[].</summary>
    private static decimal ResolveAwardTotal(JsonElement input)
    {
        var explicitVal = input.GetDecimalOrNull("totalValue") ?? input.GetDecimalOrNull("amount");
        if (explicitVal.HasValue) return explicitVal.Value;

        decimal total = 0m;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("awards", out var awards) && awards.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in awards.EnumerateArray())
            {
                var unitPrice = a.GetDecimalOrNull("unitPrice") ?? 0m;
                var qty = a.GetDecimalOrNull("quantity") ?? 1m;
                total += unitPrice * qty;
            }
        }
        return total;
    }

    private static GuardrailDecision? ReadOverride(string? perToolOverridesJson, string toolName)
    {
        if (string.IsNullOrWhiteSpace(perToolOverridesJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(perToolOverridesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(toolName, out var v) || v.ValueKind != JsonValueKind.String)
                return null;
            return v.GetString()?.ToLowerInvariant() switch
            {
                "allow" => GuardrailDecision.Allow($"Per-tool override: {toolName} allowed."),
                "require_approval" => GuardrailDecision.RequireApproval($"Per-tool override: {toolName} requires approval."),
                "deny" => GuardrailDecision.Deny($"Per-tool override: {toolName} denied."),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null; // malformed overrides are ignored (fall through to policy)
        }
    }
}

/// <summary>Stable tool-name constants shared by the guardrail and tools.</summary>
public static class AgentToolNames
{
    public const string SearchRfqs = "search_rfqs";
    public const string GetRfq = "get_rfq";
    public const string SearchSuppliers = "search_suppliers";
    public const string SearchLeads = "search_leads";
    public const string SearchQuotes = "search_quotes";
    public const string SearchOrders = "search_orders";
    public const string GetDashboardSummary = "get_dashboard_summary";
    public const string DispatchRfqToSupplier = "dispatch_rfq_to_supplier";
    public const string RecommendAward = "recommend_award";
    public const string CreateOrderFromQuote = "create_order_from_quote";

    // Sourcing-loop tools.
    public const string SendRfqToSuppliers = "send_rfq_to_suppliers";
    public const string ListSolicitations = "list_solicitations";
    public const string CaptureSupplierQuote = "capture_supplier_quote";
    public const string CompareSupplierQuotes = "compare_supplier_quotes";
    public const string AwardRfq = "award_rfq";
}
