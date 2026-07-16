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

    /// <summary>Awards at or below this value may auto-execute at Act level.</summary>
    public decimal MaxAutoAwardValue { get; set; }

    /// <summary>Orders at or below this value may auto-execute at Act level.</summary>
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
        MaxAutoAwardValue = 0m,
        MaxAutoOrderValue = 0m,
        RequireApprovalForAwards = true,
        RequireApprovalForOrders = true,
        RequireApprovalForSupplierEmails = true,
        PerToolOverrides = null
    };
}
