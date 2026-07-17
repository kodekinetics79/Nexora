namespace ERP_RFQ_Automation.Models;

// WP-A3 duplicate flag & quote-block. NEW partial file so the scaffolded
// Lead.cs stays untouched; EF configuration lives in
// ErpRfqAutomationContext.Tenancy.cs (OnModelCreatingPartial). The migration
// adding the three columns is generated separately by the lead (see
// Deduplication/DEDUP-WIRING.md for the exact column list).
public partial class Lead
{
    /// <summary>
    /// Duplicate-review state:
    /// null = never flagged; "suspected" = auto-flagged, conversion blocked;
    /// "confirmed" = human-confirmed duplicate, conversion stays blocked;
    /// "not_duplicate" = human cleared the flag, conversion allowed again.
    /// </summary>
    public string? DuplicateStatus { get; set; }

    /// <summary>The OLDER lead this one is suspected/confirmed to duplicate.</summary>
    public long? DuplicateOfLeadId { get; set; }

    /// <summary>Email of the user who resolved (confirmed / cleared) the flag.</summary>
    public string? DuplicateResolvedBy { get; set; }
}
