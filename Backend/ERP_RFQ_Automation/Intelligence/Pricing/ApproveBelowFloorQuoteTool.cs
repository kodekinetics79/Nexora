using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// WP-B3 executor for below-floor holds. The guard (<see cref="BelowFloorGuard"/>)
/// parks a below-floor apply-pricing or quote-send as a pending
/// <c>AgentApproval</c> whose ToolName is this tool; when a manager approves it,
/// the existing approve endpoint re-invokes the tool via the registry with the
/// stored InputJson, and ExecuteAsync completes the originally requested action:
///
///   holdType "apply_pricing" → applies the stored line prices to the RFQ
///                              (IPricingEngine.ApplyPricingAsync).
///   holdType "send_quote"    → performs the held email send
///                              (IQuoteService.SendQuoteEmailAsync with
///                              BypassFloorHold so it cannot re-hold itself).
///
/// IsMutation is true, but the tool is ONLY ever reached through the approvals
/// path — it is deliberately absent from the copilot's chat tool loop intent;
/// were the model ever to call it, the guardrail's unknown-mutation fail-safe
/// would require human approval anyway.
///
/// NOT registered in Program.cs by this work package — the registration line
/// lives in Sla/WAVEB-WIRING.md for the lead to splice.
/// </summary>
public sealed class ApproveBelowFloorQuoteTool : IAgentTool
{
    public const string ToolName = BelowFloorGuard.ToolName; // "approve_below_floor_quote"

    private readonly IPricingEngine _engine;
    private readonly IQuoteService _quotes;
    private readonly ErpRfqAutomationContext _db;

    public ApproveBelowFloorQuoteTool(IPricingEngine engine, IQuoteService quotes, ErpRfqAutomationContext db)
    {
        _engine = engine;
        _quotes = quotes;
        _db = db;
    }

    public string Name => ToolName;

    public string Description =>
        "Completes a below-floor pricing action a human has approved: applies the held line prices to the " +
        "RFQ, or sends the held quote email. Only ever invoked through the approvals inbox — never call it directly.";

    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"holdType\":{\"type\":\"string\",\"enum\":[\"apply_pricing\",\"send_quote\"]}," +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"quoteId\":{\"type\":\"integer\"}," +
        "\"recipientEmail\":{\"type\":\"string\"}," +
        "\"customSubject\":{\"type\":\"string\"}," +
        "\"customBody\":{\"type\":\"string\"}," +
        "\"lines\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"rfqItemId\":{\"type\":\"integer\"},\"unitPrice\":{\"type\":\"number\"}," +
        "\"floorUnitPrice\":{\"type\":\"number\"},\"delta\":{\"type\":\"number\"}}," +
        "\"required\":[\"rfqItemId\",\"unitPrice\"]}}}," +
        "\"required\":[\"holdType\"]}";

    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var holdType = input.GetStringOrNull("holdType");
        try
        {
            return holdType switch
            {
                "apply_pricing" => await ExecuteApplyPricingAsync(input, ctx, ct),
                "send_quote" => await ExecuteSendQuoteAsync(input, ctx, ct),
                _ => AgentToolResult.Fail($"Unknown holdType '{holdType}'. Expected apply_pricing or send_quote.")
            };
        }
        catch (KeyNotFoundException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
    }

    private async Task<AgentToolResult> ExecuteApplyPricingAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required for an apply_pricing hold.");

        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("lines", out var linesEl)
            || linesEl.ValueKind != JsonValueKind.Array
            || linesEl.GetArrayLength() == 0)
            return AgentToolResult.Fail("The hold carries no stored lines to apply.");

        var request = new ApplyPricingRequest { AppliedBy = ctx.UserName ?? "below-floor-approval" };
        foreach (var el in linesEl.EnumerateArray())
        {
            var itemId = el.GetInt64OrNull("rfqItemId");
            var unitPrice = el.GetDecimalOrNull("unitPrice");
            if (itemId is null || unitPrice is null)
                return AgentToolResult.Fail("Each stored line must have rfqItemId and unitPrice.");
            request.Lines.Add(new ApplyPricingLine { RfqItemId = itemId.Value, UnitPrice = unitPrice.Value });
        }

        // Tenant safety: the engine's apply is BU-checked with the approver's tenant.
        var result = await _engine.ApplyPricingAsync(rfqId.Value, ctx.BusinessUnitId, request, ct);

        return AgentToolResult.Ok(new
        {
            holdType = "apply_pricing",
            rfqId = rfqId.Value,
            applied = result.Applied,
            total = result.Total
        });
    }

    private async Task<AgentToolResult> ExecuteSendQuoteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var quoteId = input.GetInt64OrNull("quoteId");
        var recipientEmail = input.GetStringOrNull("recipientEmail");
        if (quoteId is null) return AgentToolResult.Fail("quoteId is required for a send_quote hold.");
        if (string.IsNullOrWhiteSpace(recipientEmail))
            return AgentToolResult.Fail("recipientEmail is required for a send_quote hold.");

        // Tenant safety: never act on a quote outside the approver's business unit.
        var inTenant = await _db.Quotes.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(q => q.Id == quoteId.Value && q.BusinessUnitId == ctx.BusinessUnitId, ct);
        if (!inTenant) return AgentToolResult.Fail($"Quote {quoteId} was not found in this business unit.");

        var result = await _quotes.SendQuoteEmailAsync(
            quoteId.Value,
            ctx.BusinessUnitId,
            recipientEmail!,
            input.GetStringOrNull("customSubject"),
            input.GetStringOrNull("customBody"),
            new QuoteSendOptions
            {
                BypassFloorHold = true, // approval IS the gate — must not re-hold
                RequestedByUserId = ctx.UserId,
                RequestedBy = ctx.UserName
            });

        if (result.Held) // defensive: cannot happen with BypassFloorHold
            return AgentToolResult.Fail("The send was unexpectedly re-held; please retry.");

        // R5: the manager's approval releases the below-floor hold, not the price-provenance
        // gate. If a price was edited between the rep's confirmation and this approval, the
        // attestation no longer covers the quote and nothing is sent.
        if (result.BlockedPendingPriceAttestation)
            return AgentToolResult.Fail(result.PriceAttestationReason
                ?? "The price source has not been confirmed for this quote, so it was not sent.");

        return AgentToolResult.Ok(new
        {
            holdType = "send_quote",
            quoteId = quoteId.Value,
            sentTo = recipientEmail
        });
    }
}
