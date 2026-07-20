namespace ERP_RFQ_Automation.Models;

// WP-BOQ foundation: product-vs-service classification of the source inquiry.
// NEW partial file so the scaffolded Lead.cs stays untouched; EF configuration lives
// in ErpRfqAutomationContext.Tenancy.cs (OnModelCreatingPartial). The migration adding
// the column is generated separately by the lead:
//   ALTER TABLE "Leads" ADD COLUMN "InquiryType" varchar(16) NULL;
public partial class Lead
{
    /// <summary>
    /// LLM-classified inquiry type of the source document:
    /// "product" (physical goods/materials), "service" (labor/scope-of-work),
    /// "mixed" (both), or null when unclassified/unknown. Service and mixed leads are
    /// surfaced with a distinct flag in the list DTOs and feed the BOQ engine.
    /// </summary>
    public string? InquiryType { get; set; }
}
