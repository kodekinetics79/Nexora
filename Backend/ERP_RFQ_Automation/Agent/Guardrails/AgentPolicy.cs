using System;

namespace ERP_RFQ_Automation.Agent.Guardrails;

/// <summary>
/// How much autonomy the copilot has for a tenant.
///   Observe  – read only; every mutation is denied.
///   Suggest  – may propose mutations, but each requires human approval.
///   Act      – may execute mutations autonomously, subject to value caps and the
///              per-category RequireApproval flags.
/// </summary>
public enum AgentAutonomyLevel
{
    Observe,
    Suggest,
    Act
}

/// <summary>
/// Per-tenant guardrail configuration. One row per BusinessUnitId. When absent, a
/// conservative default (see <see cref="Default"/>) is applied.
/// </summary>
public sealed class AgentPolicy
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    public AgentAutonomyLevel AutonomyLevel { get; set; } = AgentAutonomyLevel.Suggest;

    /// <summary>
    /// The currency the two caps below are denominated in. Tenant-scoped FK to
    /// <c>Currency</c> on the composite (BusinessUnitId, CurrencyId) -&gt; (BusinessUnitId, Id),
    /// exactly as <c>SupplierQuotedItem</c> and <c>SourcingAward</c> declare it — a currency
    /// ID, not a free-text code, so a cap cannot name a currency this tenant does not hold.
    ///
    /// <para><b>Why this exists.</b> The caps were bare decimals with no denomination, and the
    /// comparison sites tested them against supplier-quoted amounts denominated in the quote's
    /// own currency with no conversion. A cap of 10,000 therefore stopped a 10,000 SAR award
    /// and a 10,000 USD award alike — the same configured ceiling authorising several times more
    /// unattended spend purely on which currency a supplier happened to quote in, the multiple
    /// being whatever that pair's rate happens to be. A cap that does not know its own currency
    /// is not a control.</para>
    ///
    /// <para><b>Null means "not configured", and that fails closed.</b> Every existing row has
    /// null here, because the column is being added to a populated table and no honest backfill
    /// value exists — guessing SAR would silently re-authorise the very spend this defect
    /// exposed. While null, the cap comparison cannot be performed at all, so the guardrail
    /// routes every amount-bearing action to a human. A tenant restores auto-approval by
    /// setting this currency (PUT /api/agent/policy) and confirming the cap amounts are
    /// intended in it.</para>
    /// </summary>
    public long? CurrencyId { get; set; }

    /// <summary>
    /// Awards at or below this value <i>expressed in <see cref="CurrencyId"/></i> may
    /// auto-execute at Act level. Meaningless without <see cref="CurrencyId"/>.
    /// </summary>
    public decimal MaxAutoAwardValue { get; set; }

    /// <summary>
    /// Orders at or below this value <i>expressed in <see cref="CurrencyId"/></i> may
    /// auto-execute at Act level. Meaningless without <see cref="CurrencyId"/>.
    /// </summary>
    public decimal MaxAutoOrderValue { get; set; }

    public bool RequireApprovalForAwards { get; set; } = true;
    public bool RequireApprovalForOrders { get; set; } = true;
    public bool RequireApprovalForSupplierEmails { get; set; } = true;

    /// <summary>
    /// JSON object mapping tool name -&gt; "allow" | "require_approval" | "deny".
    /// Highest precedence when present. jsonb.
    /// </summary>
    public string? PerToolOverrides { get; set; }

    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }

    /// <summary>Conservative default used when a tenant has no stored policy.</summary>
    public static AgentPolicy Default(long businessUnitId) => new()
    {
        BusinessUnitId = businessUnitId,
        AutonomyLevel = AgentAutonomyLevel.Suggest,
        // No currency, and zero caps. Both say the same thing from different directions:
        // an unconfigured tenant auto-executes nothing.
        CurrencyId = null,
        MaxAutoAwardValue = 0m,
        MaxAutoOrderValue = 0m,
        RequireApprovalForAwards = true,
        RequireApprovalForOrders = true,
        RequireApprovalForSupplierEmails = true,
        PerToolOverrides = null
    };
}
