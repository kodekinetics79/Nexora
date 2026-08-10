using ERP_RFQ_Automation.Authorization;

namespace ERP_RFQ_Automation.Agent;

/// <summary>
/// One module+action grant a tool requires before the orchestrator may dispatch it.
/// <see cref="Policy"/> produces the SAME dynamic policy name
/// <c>[RequireModulePermission]</c> produces, so the check runs through
/// <see cref="ModulePermissionPolicyProvider"/> → <see cref="PermissionHandler"/> →
/// RolePermissions — the real gate, not a second implementation of it.
/// </summary>
public readonly record struct AgentToolPermission(string Module, PermissionAction Action)
{
    public string Policy => $"{RequireModulePermissionAttribute.PolicyPrefix}{Module}:{Action}";

    public override string ToString() => $"{Module}:{Action}";
}

/// <summary>
/// The module permission every agent tool must hold before it runs.
///
/// <para><b>The defect this closes.</b> <c>POST /api/agent/chat</c> carried only
/// <c>[Authorize]</c> and a <c>RequiresEntitlement(Ai)</c> billing gate, the tool context
/// carried no role, and no tool checked anything. The copilot was therefore a complete
/// parallel data path around module RBAC: a Member who received 403 from
/// <c>GET /api/Quote</c> could ask the agent to "list every quote over 50,000 with the
/// customer and margin" and receive it over SSE. Every read tool returns exactly the rows
/// a gated controller returns, so the agent must demand exactly the same grants.</para>
///
/// <para><b>Each entry is anchored to the HTTP endpoint that does the same thing</b>, not
/// invented — see the citations beside each row. Where the controller requires two grants
/// (for example <c>POST /api/procurement/supplier-rfqs</c> requires RFQ Management:Edit
/// AND Supplier History:Create) the tool requires both, because stacked
/// <c>[RequireModulePermission]</c> attributes are conjunctive and the agent must not be
/// the cheaper of two routes to the same write.</para>
///
/// <para><b>An unmapped tool denies.</b> <see cref="TryGetRequirements"/> returns false and
/// the orchestrator refuses to dispatch. A tool added without an entry here is unusable
/// rather than ungoverned, and <c>AgentAuthorityBoundaryTests</c> fails the build until it
/// is mapped, exactly as <c>ModuleCatalogTests</c> does for a gated endpoint.</para>
/// </summary>
public static class AgentToolPermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AgentToolPermission>> Map =
        new Dictionary<string, IReadOnlyList<AgentToolPermission>>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- Reads. Same grant as the list/detail endpoint that returns these rows. ----
            // RfqController.cs:39 / :83
            ["search_rfqs"] = [new("RFQ Management", PermissionAction.View)],
            ["get_rfq"] = [new("RFQ Management", PermissionAction.View)],
            // ContactController.cs:300
            ["search_suppliers"] = [new("Suppliers", PermissionAction.View)],
            // LeadUploaderController.cs:32, ConversionIntelligenceController.cs:31
            ["search_leads"] = [new("Leads", PermissionAction.View)],
            // QuotationUploaderController.cs:29
            ["search_quotes"] = [new("Quotations", PermissionAction.View)],
            // OrderController.cs:75
            ["search_orders"] = [new("Orders", PermissionAction.View)],
            // The dashboard KPI surface.
            ["get_dashboard_summary"] = [new("Dashboard", PermissionAction.View)],
            // ProcurementController.cs:37 (GetSourcingCase) — solicitation state is supplier history.
            ["list_solicitations"] = [new("Supplier History", PermissionAction.View)],
            // ProcurementController.cs:115 (CompareQuotes)
            ["compare_supplier_quotes"] = [new("Supplier History", PermissionAction.View)],
            // Advisory read over the same supplier-quote rows CompareQuotes gates.
            ["recommend_award"] = [new("Supplier History", PermissionAction.View)],
            // LeadDecisionController.cs:30
            ["lead_decision_brief"] = [new("Leads", PermissionAction.View)],
            // PricingIntelligenceController.cs:22-23
            ["price_rfq"] =
            [
                new("RFQ Management", PermissionAction.View),
                new("Quotations", PermissionAction.View)
            ],
            // ConversionIntelligenceController.cs:31
            ["preview_lead_conversion"] = [new("Leads", PermissionAction.View)],
            // BoqController.cs:50
            ["get_boq"] = [new("Quotations", PermissionAction.View)],

            // ---- Mutations. Same grants as the endpoint that performs the write. ----
            // ProcurementController.cs:52-53 / :64-65 (PrepareSupplierRfq / QueuePreparedSupplierRfq)
            ["dispatch_rfq_to_supplier"] =
            [
                new("RFQ Management", PermissionAction.Edit),
                new("Supplier History", PermissionAction.Create)
            ],
            ["send_rfq_to_suppliers"] =
            [
                new("RFQ Management", PermissionAction.Edit),
                new("Supplier History", PermissionAction.Create)
            ],
            // ProcurementController.cs:103 (CaptureSupplierQuote)
            ["capture_supplier_quote"] = [new("Supplier History", PermissionAction.Create)],
            // ProcurementController.cs:120 (ApproveAward)
            ["award_rfq"] = [new("Supplier History", PermissionAction.Edit)],
            // OrderController.cs:168-169 (POST orders/from-quote/{quoteId})
            ["create_order_from_quote"] = [new("Orders", PermissionAction.Create)],
            // ConversionIntelligenceController.cs:56-57
            ["convert_lead_to_rfq"] =
            [
                new("Leads", PermissionAction.Create),
                new("RFQ Management", PermissionAction.Create)
            ],
            // PricingIntelligenceController.cs:52-53
            ["apply_rfq_pricing"] =
            [
                new("RFQ Management", PermissionAction.View),
                new("Quotations", PermissionAction.Edit)
            ],
            // Completes a held apply-pricing / send-quote action: the same grants the held
            // action itself needed. Approval decides WHETHER; this decides WHO may hold it.
            ["approve_below_floor_quote"] =
            [
                new("RFQ Management", PermissionAction.View),
                new("Quotations", PermissionAction.Edit)
            ],
            // BoqController.cs:31 (POST boq/draft)
            ["draft_boq"] = [new("Quotations", PermissionAction.Create)]
        };

    /// <summary>Every tool name that carries a declared permission. Used by the completeness test.</summary>
    public static IReadOnlyCollection<string> MappedToolNames => (IReadOnlyCollection<string>)Map.Keys;

    /// <summary>
    /// The grants <paramref name="toolName"/> requires. Returns <c>false</c> for an unmapped
    /// or unnamed tool — the caller MUST treat that as a denial. There is deliberately no
    /// permissive fallback: a tool nobody mapped is a tool nobody decided who may run.
    /// </summary>
    public static bool TryGetRequirements(string? toolName, out IReadOnlyList<AgentToolPermission> requirements)
    {
        if (!string.IsNullOrWhiteSpace(toolName) && Map.TryGetValue(toolName, out var found) && found.Count > 0)
        {
            requirements = found;
            return true;
        }

        requirements = Array.Empty<AgentToolPermission>();
        return false;
    }
}
