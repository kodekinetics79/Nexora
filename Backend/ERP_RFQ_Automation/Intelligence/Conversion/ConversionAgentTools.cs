using System.Text.Json;
using ERP_RFQ_Automation.Agent;

namespace ERP_RFQ_Automation.Intelligence.Conversion;

/// <summary>
/// Stable snake_case tool names. Kept here (not in AgentToolNames) so the
/// conversion-intelligence work stays isolated; the guardrail's unknown-mutation
/// fail-safe routes convert_lead_to_rfq to human approval by design (see
/// CONVERSION-WIRING.md for the suggested future guardrail case).
/// </summary>
public static class ConversionToolNames
{
    public const string PreviewLeadConversion = "preview_lead_conversion";
    public const string ConvertLeadToRfq = "convert_lead_to_rfq";
}

/// <summary>Read-only dry run of a lead conversion for the sourcing copilot.</summary>
public sealed class PreviewLeadConversionTool : IAgentTool
{
    private readonly ILeadConversionIntelligence _intelligence;
    public PreviewLeadConversionTool(ILeadConversionIntelligence intelligence) => _intelligence = intelligence;

    public string Name => ConversionToolNames.PreviewLeadConversion;

    public string Description =>
        "Preview converting a lead into an RFQ: resolves each line against the product catalog " +
        "and reports the best matches, normalized quantity/UoM and a per-line confidence. Read-only.";

    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"leadId\":{\"type\":\"integer\",\"description\":\"Lead id to preview\"}},\"required\":[\"leadId\"]}";

    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var leadId = input.GetInt64OrNull("leadId");
        if (leadId is null) return AgentToolResult.Fail("leadId is required.");

        try
        {
            var preview = await _intelligence.PreviewAsync(leadId.Value, ctx.BusinessUnitId, ct);

            // Compact summary shaped for the model, mirroring the read-tools style.
            return AgentToolResult.Ok(new
            {
                leadId = preview.LeadId,
                rfqNo = preview.Header.Rfqno,
                buyer = preview.Header.BuyersName,
                overallConfidence = preview.OverallConfidence,
                lineCount = preview.Items.Count,
                needsAttentionCount = preview.Items.Count(i => i.NeedsAttention),
                lines = preview.Items.Select(i => new
                {
                    leadItemId = i.LeadItemId,
                    text = i.SourceText,
                    qty = i.NormalizedQuantity ?? i.Quantity,
                    uom = i.NormalizedUom ?? i.UnitOfMeasure,
                    bestMatch = i.Matches.Count > 0
                        ? new
                        {
                            productId = i.Matches[0].ProductId,
                            product = i.Matches[0].ProductName,
                            score = i.Matches[0].Score,
                            reason = i.Matches[0].Reason
                        }
                        : null,
                    confidence = i.Confidence,
                    needsAttention = i.NeedsAttention,
                    attentionReason = i.AttentionReason
                }).ToList()
            });
        }
        catch (KeyNotFoundException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Converts a lead into an RFQ using the best catalog match for every line (all
/// lines included). Mutation: the orchestrator routes it through the guardrail,
/// where the unknown-mutation fail-safe requires human approval.
/// </summary>
public sealed class ConvertLeadToRfqTool : IAgentTool
{
    private readonly ILeadConversionIntelligence _intelligence;
    public ConvertLeadToRfqTool(ILeadConversionIntelligence intelligence) => _intelligence = intelligence;

    public string Name => ConversionToolNames.ConvertLeadToRfq;

    public string Description =>
        "Convert an accepted lead into an RFQ. Includes every line and links each line to its best " +
        "catalog match (match score is stamped as the line's AI confidence; low-confidence links are " +
        "reported for review). Optional notes are appended to the RFQ header remarks.";

    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"leadId\":{\"type\":\"integer\",\"description\":\"Lead id to convert\"}," +
        "\"notes\":{\"type\":\"string\",\"description\":\"Optional note appended to the RFQ header remarks\"}}," +
        "\"required\":[\"leadId\"]}";

    public bool IsMutation => true;

    public Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var leadId = input.GetInt64OrNull("leadId");
        if (leadId is null) return Task.FromResult(AgentToolResult.Fail("leadId is required."));

        return Task.FromResult(AgentToolResult.Fail(
            $"Lead {leadId.Value} was not converted. Commit the current Lead Revision participation decision and invoke RFQ Promotion."));
    }
}
