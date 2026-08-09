namespace ERP_RFQ_Automation.Procurement;

/// <summary>
/// FR-SPO-01. Whether the buyer who approves a supplier purchase order may be the same person who
/// approved the sourcing award behind its lines.
///
/// <para>Award approval and PO issuance were already separate permissions, but a permission pair is
/// not a segregation of duties: nothing stopped one user holding both and walking a supplier of
/// their choosing from quote to committed spend unobserved. The award approver is recorded on
/// <c>SourcingAward.AwardedByUserId</c> and the PO approver on
/// <c>SupplierPurchaseOrder.ApprovedByUserId</c>, so the rule is a comparison of two identities the
/// system already holds.</para>
///
/// <para>It defaults to enforced because that is the control the requirement asks for. It is
/// configurable because a two-person trading company physically cannot satisfy it, and a control
/// that makes the product unusable gets disabled in the worst possible way — by turning off the
/// approval step altogether. Whichever way it is set, the setting in force at the moment of
/// approval is written into the audit event, so a later change to configuration cannot make a past
/// approval look like it passed a check it never faced.</para>
/// </summary>
public sealed class ProcurementApprovalOptions
{
    public const string SectionName = "Procurement:Approval";

    /// <summary>
    /// When true (the default), the user approving the purchase order must not be a user who
    /// approved any sourcing award on it.
    /// </summary>
    public bool SegregationOfDutiesEnforced { get; set; } = true;
}
