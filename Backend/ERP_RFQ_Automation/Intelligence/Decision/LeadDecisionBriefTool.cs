using System.Text.Json;
using ERP_RFQ_Automation.Agent;

namespace ERP_RFQ_Automation.Intelligence.Decision;

/// <summary>
/// Copilot tool: compact Bid / Review / Skip decision brief for a lead (read-only).
/// NOT registered by AddLeadDecisionIntelligence — the registration line lives in
/// DECISION-WIRING.md so the lead can splice it into the Agent tool set.
/// </summary>
public sealed class LeadDecisionBriefTool : IAgentTool
{
    public const string ToolName = "lead_decision_brief";

    private readonly ILeadDecisionService _service;
    public LeadDecisionBriefTool(ILeadDecisionService service) => _service = service;

    public string Name => ToolName;
    public string Description =>
        "Build a Bid/Review/Skip decision brief for a lead: catalog coverage, estimated value, " +
        "margin potential, customer history, deadline feasibility, and a transparent recommendation " +
        "with plain-language reasons. Read-only.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"leadId\":{\"type\":\"integer\",\"description\":\"Id of the lead to brief\"}}," +
        "\"required\":[\"leadId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var leadId = input.GetInt64OrNull("leadId");
        if (leadId is null) return AgentToolResult.Fail("leadId is required.");

        try
        {
            var brief = await _service.GetBriefAsync(leadId.Value, ctx.BusinessUnitId, ct);

            // Compact shape for the model: headline numbers + reasons, no per-item rows.
            return AgentToolResult.Ok(new
            {
                leadId = brief.LeadId,
                rfqNo = brief.Rfqno,
                buyersName = brief.BuyersName,
                recommendation = brief.Recommendation,
                reasons = brief.Reasons,
                coverage = new
                {
                    coveredItems = brief.Coverage.CoveredItems,
                    totalItems = brief.Coverage.TotalItems,
                    coveragePct = brief.Coverage.CoveragePct,
                    inStockItems = brief.Coverage.InStockItems
                },
                estimatedValue = brief.EstimatedValue,
                valueConfidence = brief.ValueConfidence,
                currency = brief.Currency,
                marginPotentialPct = brief.MarginPotentialPct,
                customer = new
                {
                    isExistingCustomer = brief.Customer.IsExistingCustomer,
                    customerName = brief.Customer.CustomerName,
                    pastLeads = brief.Customer.PastLeads,
                    quotes = brief.Customer.Quotes,
                    orders = brief.Customer.Orders,
                    totalOrderValue = brief.Customer.TotalOrderValue,
                    totalOrderCurrency = brief.Customer.TotalOrderCurrency
                },
                deadline = new
                {
                    daysLeft = brief.Deadline.DaysLeft,
                    urgency = brief.Deadline.Urgency,
                    workloadHint = brief.Deadline.WorkloadHint
                },
                extractionConfidence = brief.ExtractionConfidence
            });
        }
        catch (KeyNotFoundException ex)
        {
            return AgentToolResult.Fail(ex.Message);
        }
    }
}
