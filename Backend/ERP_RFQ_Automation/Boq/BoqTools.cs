using System.Text.Json;
using ERP_RFQ_Automation.Agent;

namespace ERP_RFQ_Automation.Boq;

/// <summary>
/// Copilot tool: draft a bill of quantities from a lead or raw scope text.
/// Mutation — persists a Draft BoqDocument, so it is routed through the guardrail
/// engine and (until a dedicated case is added) rides the unknown-mutation
/// fail-safe requiring human approval. NOT registered by AddBoqEngine —
/// registration lines live in BOQ-WIRING.md so the lead can splice them into the
/// Agent tool set (same convention as the pricing tools).
/// </summary>
public sealed class DraftBoqTool : IAgentTool
{
    public const string ToolName = "draft_boq";

    private readonly IBoqBuilderService _boq;
    public DraftBoqTool(IBoqBuilderService boq) => _boq = boq;

    public string Name => ToolName;
    public string Description =>
        "Draft a bill of quantities (BOQ) for a SERVICE request (maintenance, installation, " +
        "testing, supply-and-install, manpower hire) from an existing lead (leadId) or from raw " +
        "scope text. Quantities the source does not state are marked TBD for a human — never guessed. " +
        "Creates a Draft BOQ the user can refine at /services/boq.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"leadId\":{\"type\":\"integer\",\"description\":\"Draft from this lead's extracted content\"}," +
        "\"title\":{\"type\":\"string\",\"description\":\"Optional BOQ title\"}," +
        "\"text\":{\"type\":\"string\",\"description\":\"Raw scope-of-work text to draft from (alternative to leadId)\"}," +
        "\"serviceCategory\":{\"type\":\"string\",\"description\":\"Optional hint: electrical|mechanical|civil|maintenance|manpower|mixed|other\"}}}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var leadId = input.GetInt64OrNull("leadId");
        var text = input.GetStringOrNull("text");
        if (leadId is null && string.IsNullOrWhiteSpace(text))
            return AgentToolResult.Fail("Provide leadId or text to draft from.");

        try
        {
            var dto = await _boq.DraftFromTextAsync(new BoqDraftRequest
            {
                LeadId = leadId,
                Title = input.GetStringOrNull("title"),
                Text = text,
                ServiceCategory = input.GetStringOrNull("serviceCategory"),
                CreatedBy = ctx.UserName ?? "copilot"
            }, ctx.BusinessUnitId, ct);

            return AgentToolResult.Ok(new
            {
                boqId = dto.Id,
                title = dto.Title,
                serviceCategory = dto.ServiceCategory,
                status = dto.Status,
                sections = dto.Sections.Select(s => new { s.Title, items = s.Items.Count }),
                itemCount = dto.ItemCount,
                itemsNeedingDetails = dto.TbdCount,
                pricedTotal = dto.TotalAmount,
                overallConfidence = dto.OverallConfidence,
                assumptions = dto.Assumptions,
                note = dto.Notes
            });
        }
        catch (KeyNotFoundException ex) { return AgentToolResult.Fail(ex.Message); }
        catch (ArgumentException ex) { return AgentToolResult.Fail(ex.Message); }
    }
}

/// <summary>
/// Copilot tool: read a BOQ (full tree, compact shape). Read-only.
/// Registration line lives in BOQ-WIRING.md.
/// </summary>
public sealed class GetBoqTool : IAgentTool
{
    public const string ToolName = "get_boq";

    private readonly IBoqBuilderService _boq;
    public GetBoqTool(IBoqBuilderService boq) => _boq = boq;

    public string Name => ToolName;
    public string Description =>
        "Open a bill of quantities (BOQ) by id: sections, line items, quantities, rates, totals, " +
        "and which lines still need human details (TBD). Read-only.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"boqId\":{\"type\":\"integer\",\"description\":\"Id of the BOQ document\"}}," +
        "\"required\":[\"boqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var boqId = input.GetInt64OrNull("boqId");
        if (boqId is null) return AgentToolResult.Fail("boqId is required.");

        var dto = await _boq.GetAsync(boqId.Value, ctx.BusinessUnitId, ct);
        if (dto is null) return AgentToolResult.Fail($"BOQ {boqId} was not found.");

        return AgentToolResult.Ok(new
        {
            boqId = dto.Id,
            title = dto.Title,
            serviceCategory = dto.ServiceCategory,
            status = dto.Status,
            pricedTotal = dto.TotalAmount,
            itemsNeedingDetails = dto.TbdCount,
            overallConfidence = dto.OverallConfidence,
            assumptions = dto.Assumptions,
            sections = dto.Sections.Select(s => new
            {
                s.Title,
                subtotal = s.TotalAmount,
                items = s.Items.Select(i => new
                {
                    i.Seq,
                    description = Truncate(i.Description, 100),
                    i.Unit,
                    quantity = i.IsTbd ? (decimal?)null : i.Quantity,
                    i.ItemType,
                    i.UnitRate,
                    total = i.TotalAmount,
                    needsDetails = i.IsTbd,
                    i.AssemblyCode
                })
            })
        });
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
