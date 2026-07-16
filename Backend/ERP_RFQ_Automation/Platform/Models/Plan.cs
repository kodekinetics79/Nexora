namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// A subscription plan / tier. <see cref="Weight"/> and
/// <see cref="MaxConcurrentExtractionJobs"/> feed the Weighted-Fair-Queuing
/// scheduler and the hard per-tenant concurrency cap (ADR-0005 §5); the quota
/// fields bound seats and monthly document volume. <see cref="Features"/> is a
/// JSON blob of feature toggles/entitlements. (ADR-0005 §4)
/// </summary>
public class Plan
{
    public long Id { get; set; }

    /// <summary>Stable machine code, e.g. "free" / "pro" / "enterprise". Unique.</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>WFQ share weight (higher plan → larger scheduling share).</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Hard per-tenant in-flight extraction cap.</summary>
    public int MaxConcurrentExtractionJobs { get; set; } = 2;

    public int MaxDocsPerMonth { get; set; } = 1000;

    public int MaxSeats { get; set; } = 5;

    /// <summary>JSON map of feature entitlements (stored as jsonb — see WIRING.md).</summary>
    public string Features { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}
