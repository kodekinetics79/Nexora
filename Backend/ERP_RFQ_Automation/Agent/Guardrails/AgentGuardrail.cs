using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Guardrails;

/// <summary>
/// Default guardrail engine. Precedence (highest first):
///   1. Per-tool override in <see cref="AgentPolicy.PerToolOverrides"/> — see the paragraph
///      below for exactly how far "allow" reaches.
///   2. Autonomy level (Observe denies all mutations; Suggest requires approval).
///   3. Category flags (RequireApprovalForAwards/Orders/SupplierEmails).
///   4. Value caps (MaxAutoAwardValue / MaxAutoOrderValue) for amount-bearing tools.
/// A tool that passes every gate at Act level is allowed to auto-execute.
///
/// <para><b>What a per-tool "allow" may and may not do.</b> It used to return Allow outright,
/// before the autonomy gate and before any cap. A tenant Manager — not a platform operator —
/// could therefore store <c>{"award_rfq":"allow","create_order_from_quote":"allow",
/// "send_rfq_to_suppliers":"allow"}</c> through PUT /api/agent/policy and hand model output
/// the power to award RFQs, raise orders and email suppliers with no ceiling and no human.
/// "allow" now NARROWS the evaluation instead of terminating it: it relaxes the Suggest
/// autonomy level and the tool's own category flag for that one tool, then evaluation
/// continues. It does NOT lift Observe — that level is the tenant's read-only switch and a
/// stale per-tool exception must not defeat it — and it never, under any configuration,
/// skips <see cref="EvaluateCapAsync"/>. An amount-bearing tool with an "allow" override
/// still has to be within a cap denominated in a known currency; with the conservative
/// defaults (null currency, zero caps) that is still a human. "deny" and "require_approval"
/// keep their outright precedence, because both only ever tighten.</para>
///
/// <para><b>Currency contract for gate 4.</b> The caps are denominated in
/// <see cref="AgentPolicy.CurrencyId"/> and the amounts they are compared against are
/// denominated in whatever currency the underlying commercial record carries. Every comparison
/// therefore goes through <see cref="AgentSpendCap"/>, which converts with approved,
/// effective-dated FX evidence first. There is no raw numeric comparison left in this file.
/// An amount whose currency is unknown, or for which no approved rate exists, yields
/// RequireApproval — never Allow.</para>
///
/// <para><b>Amount currencies are read from persisted records, never from the tool input.</b>
/// The caller of a mutating tool is a language model; letting it declare the currency of its own
/// spend would reopen the defect from the other side. The order cap reads
/// <c>Quote.CurrencyId</c>; the award cap reads <c>SupplierQuotedItem.CurrencyId</c>. When no
/// persisted anchor is reachable the currency is unknown and the action goes to a human.</para>
/// </summary>
public sealed class AgentGuardrail : IAgentGuardrail
{
    private readonly ErpRfqAutomationContext _db;
    private readonly AgentSpendCap _spendCap;

    public AgentGuardrail(ErpRfqAutomationContext db)
    {
        _db = db;
        _spendCap = new AgentSpendCap(db);
    }

    /// <summary>Seam for substituting the FX authority in tests.</summary>
    public AgentGuardrail(ErpRfqAutomationContext db, AgentSpendCap spendCap)
    {
        _db = db;
        _spendCap = spendCap;
    }

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

        // 1. Per-tool override. "deny" and "require_approval" terminate — they only tighten.
        //    "allow" does NOT terminate; it sets `relaxed` and evaluation continues, so the
        //    value caps below are still reached.
        var verb = ReadOverrideVerb(policy.PerToolOverrides, tool.Name);
        if (verb == OverrideVerb.Deny)
            return GuardrailDecision.Deny($"Per-tool override: {tool.Name} denied.");
        if (verb == OverrideVerb.RequireApproval)
            return GuardrailDecision.RequireApproval($"Per-tool override: {tool.Name} requires approval.");

        var relaxed = verb == OverrideVerb.Allow;
        string Note(string reason) => relaxed ? $"Per-tool override: {tool.Name} allowed. {reason}" : reason;

        // 2. Autonomy level. An "allow" override relaxes Suggest for this one tool. It does not
        //    lift Observe: that level is the tenant's read-only switch for the whole copilot,
        //    and a per-tool exception left behind in the policy must not defeat it.
        if (policy.AutonomyLevel == AgentAutonomyLevel.Observe)
            return GuardrailDecision.Deny("Autonomy level is Observe (read-only); mutations are not permitted."
                + (relaxed ? $" A per-tool 'allow' for {tool.Name} does not lift Observe." : string.Empty));

        if (policy.AutonomyLevel == AgentAutonomyLevel.Suggest && !relaxed)
            return GuardrailDecision.RequireApproval("Autonomy level is Suggest; this action requires human approval.");

        // 3 + 4. Act level (or a tool relaxed to it) — category flags and value caps.
        switch (tool.Name)
        {
            case AgentToolNames.DispatchRfqToSupplier:
                if (policy.RequireApprovalForSupplierEmails && !relaxed)
                    return GuardrailDecision.RequireApproval("Policy requires approval before emailing suppliers.");
                return GuardrailDecision.Allow(Note("Act level; supplier emails allowed by policy."));

            case AgentToolNames.RecommendAward:
                if (policy.RequireApprovalForAwards && !relaxed)
                    return GuardrailDecision.RequireApproval("Policy requires approval for award recommendations.");
                // COMPARISON SITE 1. recommend_award is advisory (IsMutation = false), so it
                // returns at the top of this method and never reaches here today. If it is ever
                // promoted to a mutation, its "amount" arrives as a bare caller-supplied number
                // with no persisted record behind it and therefore no trustworthy currency —
                // which converts to "unknown currency", which fails closed. That is the correct
                // direction for a control to break.
                return Annotate(await EvaluateCapAsync(ctx.BusinessUnitId,
                    input.GetDecimalOrNull("amount") ?? 0m, amountCurrencyId: null,
                    policy, policy.MaxAutoAwardValue, "award", ct), Note);

            case AgentToolNames.CreateOrderFromQuote:
                if (policy.RequireApprovalForOrders && !relaxed)
                    return GuardrailDecision.RequireApproval("Policy requires approval before creating orders.");
                // COMPARISON SITE 2. Reached even when `relaxed`: a per-tool "allow" is not a
                // licence to spend without a ceiling.
                var (orderAmount, orderCurrencyId) = await ResolveOrderAmountAsync(input, ctx.BusinessUnitId, ct);
                return Annotate(await EvaluateCapAsync(ctx.BusinessUnitId, orderAmount, orderCurrencyId,
                    policy, policy.MaxAutoOrderValue, "order", ct), Note);

            case AgentToolNames.SendRfqToSuppliers:
                if (policy.RequireApprovalForSupplierEmails && !relaxed)
                    return GuardrailDecision.RequireApproval("Policy requires approval before emailing suppliers.");
                return GuardrailDecision.Allow(Note("Act level; supplier solicitations allowed by policy."));

            case AgentToolNames.AwardRfq:
                if (policy.RequireApprovalForAwards && !relaxed)
                    return GuardrailDecision.RequireApproval("Policy requires approval before recording awards.");
                // COMPARISON SITE 3. Also reached when `relaxed`.
                var awardCurrencyId = await ResolveAwardCurrencyAsync(input, ctx.BusinessUnitId, ct);
                return Annotate(await EvaluateCapAsync(ctx.BusinessUnitId, ResolveAwardTotal(input), awardCurrencyId,
                    policy, policy.MaxAutoAwardValue, "award", ct), Note);

            case AgentToolNames.CaptureSupplierQuote:
                // Data entry: no category flag/value cap. Autonomy gate above already
                // denies at Observe and requires approval at Suggest; allow at Act.
                return GuardrailDecision.Allow(Note("Act level; supplier quote capture (data entry) allowed."));

            default:
                // Unknown mutation — fail safe. A per-tool "allow" cannot make an unmapped
                // mutation auto-execute; there is no cap for it, so there is no ceiling to obey.
                return GuardrailDecision.RequireApproval(
                    Note("Unrecognized mutation; requires human approval."));
        }
    }

    /// <summary>Re-labels a cap verdict so the reason names the override that got it this far.</summary>
    private static GuardrailDecision Annotate(GuardrailDecision decision, Func<string, string> note) =>
        decision.Outcome switch
        {
            GuardrailOutcome.Allow => GuardrailDecision.Allow(note(decision.Reason)),
            GuardrailOutcome.RequireApproval => GuardrailDecision.RequireApproval(note(decision.Reason)),
            _ => GuardrailDecision.Deny(note(decision.Reason))
        };

    /// <summary>
    /// The ONLY cap comparison in this class. Converts first, and maps the three-way verdict onto
    /// the guardrail's two-way outcome: anything that is not demonstrably within the cap —
    /// including "could not be converted" — becomes RequireApproval, carrying the reason verbatim
    /// so the human is told exactly what is missing.
    /// </summary>
    private async Task<GuardrailDecision> EvaluateCapAsync(long businessUnitId, decimal amount,
        long? amountCurrencyId, AgentPolicy policy, decimal cap, string label, CancellationToken ct)
    {
        var verdict = await _spendCap.EvaluateAsync(businessUnitId, amount, amountCurrencyId, policy, cap, label,
            DateTime.UtcNow, ct);
        return verdict.MayAutoExecute
            ? GuardrailDecision.Allow(verdict.Reason)
            : GuardrailDecision.RequireApproval(verdict.Reason);
    }

    /// <summary>
    /// Order value and its denomination. The quote is the authoritative source of both: a total
    /// and the currency it is expressed in have to travel together, which is exactly what the old
    /// code did not do. An explicit caller-supplied "amount" is still honoured for the figure
    /// (preserving existing behaviour) but carries no currency, so it fails closed.
    /// </summary>
    private async Task<(decimal Amount, long? CurrencyId)> ResolveOrderAmountAsync(JsonElement input,
        long businessUnitId, CancellationToken ct)
    {
        var explicitAmount = input.GetDecimalOrNull("amount");
        if (explicitAmount.HasValue) return (explicitAmount.Value, null);

        var quoteId = input.GetInt64OrNull("quoteId");
        if (quoteId is null) return (0m, null);

        var quote = await _db.Set<Quote>().AsNoTracking()
            .Where(q => q.Id == quoteId.Value && q.BusinessUnitId == businessUnitId)
            .Select(q => new { q.TotalAmount, q.CurrencyId })
            .FirstOrDefaultAsync(ct);

        // A quote that cannot be found is 0, which is within any non-negative cap — but with an
        // unknown currency, so it still routes to a human rather than sliding through on zero.
        return quote is null ? (0m, null) : (quote.TotalAmount ?? 0m, quote.CurrencyId);
    }

    /// <summary>
    /// The denomination of an award, taken from the persisted supplier quote lines the award
    /// names — never from the tool input.
    ///
    /// Returns null (which fails closed) whenever the answer is not unambiguous: no
    /// supplierQuotedItemId supplied, the row is not visible to this tenant, the row carries no
    /// currency, or the named lines disagree with each other. Mixed currencies in one award are
    /// exactly the case where summing them into one number is meaningless, so no single currency
    /// is claimed for the total.
    /// </summary>
    private async Task<long?> ResolveAwardCurrencyAsync(JsonElement input, long businessUnitId, CancellationToken ct)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("awards", out var awards) ||
            awards.ValueKind != JsonValueKind.Array)
            return null;

        var quotedItemIds = new List<long>();
        foreach (var a in awards.EnumerateArray())
        {
            var id = a.GetInt64OrNull("supplierQuotedItemId");
            if (id.HasValue) quotedItemIds.Add(id.Value);
        }
        if (quotedItemIds.Count == 0) return null;

        var distinctIds = quotedItemIds.Distinct().ToList();
        var rows = await _db.Set<SupplierQuotedItem>().AsNoTracking()
            .Where(q => distinctIds.Contains(q.Id) && q.BusinessUnitId == businessUnitId)
            .Select(q => new { q.Id, q.CurrencyId })
            .ToListAsync(ct);

        // Every named line must be visible to this tenant. A line the guardrail cannot see is a
        // line whose currency it does not know, and the total already includes its value.
        if (rows.Count != distinctIds.Count) return null;

        var currencyIds = rows.Select(r => r.CurrencyId).Distinct().ToList();

        // Exactly one currency, and it must actually be set. A single distinct value of null is
        // still "unknown", so it falls through to null and fails closed.
        return currencyIds.Count == 1 ? currencyIds[0] : null;
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

    /// <summary>The three verbs a stored per-tool override may use; null = no applicable entry.</summary>
    public enum OverrideVerb
    {
        Allow,
        RequireApproval,
        Deny
    }

    /// <summary>
    /// The verb stored for this tool, or null when there is none. Returns the VERB rather than
    /// a finished decision on purpose: "allow" is no longer a decision, it is an input to one.
    /// Malformed JSON, a non-object root, a non-string value and an unknown verb are all
    /// "no override" and fall through to the policy.
    /// </summary>
    public static OverrideVerb? ReadOverrideVerb(string? perToolOverridesJson, string toolName)
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
                "allow" => OverrideVerb.Allow,
                "require_approval" => OverrideVerb.RequireApproval,
                "deny" => OverrideVerb.Deny,
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
