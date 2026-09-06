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

    /// <summary>List price per month in USD (precision 10,2). Null = not priced yet.</summary>
    public decimal? MonthlyPriceUsd { get; set; }

    /// <summary>
    /// Which AI package this plan sells — one of <see cref="AI.AiPackages"/>. It is the STARTING
    /// posture a tenant provisioned from this plan is given, copied once at provisioning in the
    /// same way <see cref="Features"/> is: editing a plan afterwards never reaches back into
    /// tenants already created from it.
    ///
    /// <para>It cannot carry consent. A plan may say a customer bought cloud extraction; it may
    /// not say they agreed to send whole documents off their own infrastructure. See
    /// <see cref="AI.AiPackages"/>.</para>
    /// </summary>
    public string AiPackage { get; set; } = AI.AiPackages.Off;

    /// <summary>
    /// The monthly AI token ceiling a tenant on this plan starts with. Null means either "not
    /// decided" or "deliberately uncapped" — <see cref="AiAllowanceUnlimited"/> is what tells
    /// those two apart, and only one of them is a decision anybody made.
    /// </summary>
    public long? AiMonthlyTokenAllowance { get; set; }

    /// <summary>Unbounded AI spend, chosen. Never inferred from an absent allowance.</summary>
    public bool AiAllowanceUnlimited { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}
