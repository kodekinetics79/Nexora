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

    /// <summary>
    /// FR-QTM-07 trigger 1 — GRACE days added to the quote's validity date before it
    /// auto-expires. The default is 0: "mark a quotation Expired AFTER its validity
    /// date" means the day the date passes, not a fortnight later. Kept configurable so
    /// a tenant that wants a courtesy window can buy one deliberately.
    ///
    /// <para>Stored in the legacy <c>QuoteAutoExpireDays</c> column (see
    /// <c>ConfigureSlaModel</c>) — the property was renamed because its meaning changed
    /// from "days after coalesce(ValidUntil, SentOn)" to "days after ValidUntil"; the
    /// column name is left alone so no rename migration is needed.</para>
    /// </summary>
    public int QuoteExpiryGraceDays { get; set; }

    /// <summary>
    /// FR-QTM-07 trigger 2 — days after SUBMISSION (<c>Quote.SentOn</c>) at which a quote
    /// with NO recorded customer response (<c>Quote.RespondedOn</c> is null) auto-expires,
    /// whatever its validity date says. This is the rule that catches quotes sent without
    /// a ValidUntil at all, which trigger 1 can never see.
    /// </summary>
    public int QuoteNoResponseExpiryDays { get; set; } = 90;

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
        QuoteExpiryGraceDays = 0,
        QuoteNoResponseExpiryDays = 90,
        ApprovalEscalationHours = 4,
        DeadlineBufferHours = 12
    };
}
