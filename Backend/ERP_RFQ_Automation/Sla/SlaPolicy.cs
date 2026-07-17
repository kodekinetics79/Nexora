using System;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Per-tenant SLA / deadline configuration. One row per BusinessUnitId; when a
/// tenant has no stored row a conservative default (<see cref="Default"/>) is
/// applied — mirrors the <c>AgentPolicy</c> pattern in Agent/Guardrails.
/// All durations are expressed in plain hours/days so the Setup UI can use
/// plain-language labels ("Alert me X days before a bid closes").
/// </summary>
public sealed class SlaPolicy
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Hours an accepted lead may sit unassigned before managers are alerted.</summary>
    public int UnassignedHours { get; set; } = 2;

    /// <summary>Days before a bid-closing date at which the assignee gets a warning.</summary>
    public int WarnDaysBeforeClose { get; set; } = 3;

    /// <summary>Days before a bid-closing date at which the alert becomes critical.</summary>
    public int CriticalDaysBeforeClose { get; set; } = 1;

    /// <summary>Days after sending with no customer response before a quote counts as stale.</summary>
    public int StaleQuoteDays { get; set; } = 7;

    /// <summary>Days after coalesce(ValidUntil, SentOn) at which a SENT quote auto-expires.</summary>
    public int QuoteAutoExpireDays { get; set; } = 14;

    /// <summary>Hours a copilot approval may stay pending before managers are alerted.</summary>
    public int ApprovalEscalationHours { get; set; } = 4;

    /// <summary>
    /// Internal safety buffer (hours) before an external deadline. Reserved for the
    /// upcoming "internal deadline" surface; stored/configurable now so tenants can
    /// tune it once that ships. Not yet consumed by the sweep.
    /// </summary>
    public int DeadlineBufferHours { get; set; } = 12;

    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }

    /// <summary>Conservative default used when a tenant has no stored policy.</summary>
    public static SlaPolicy Default(long businessUnitId) => new()
    {
        BusinessUnitId = businessUnitId,
        UnassignedHours = 2,
        WarnDaysBeforeClose = 3,
        CriticalDaysBeforeClose = 1,
        StaleQuoteDays = 7,
        QuoteAutoExpireDays = 14,
        ApprovalEscalationHours = 4,
        DeadlineBufferHours = 12
    };
}
