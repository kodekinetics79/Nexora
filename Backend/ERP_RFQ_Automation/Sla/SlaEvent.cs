using System;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Append-only record of one SLA notification/action, used both as an audit trail
/// and as the send-once dedup ledger. The sweep worker performs a
/// lookup-before-insert on the (BusinessUnitId, EntityType, EntityId, Level) key,
/// so an alert for a given entity+level is only ever produced once — except the
/// daily stale-quote digest, which dedups per owner per UTC day via CreatedOn
/// (EntityType "quote-stale-digest", EntityId = owner user id).
///
/// EntityType values: "lead", "lead-unassigned", "quote", "quote-stale-digest", "approval".
/// Level values:      "warn", "critical", "overdue", "stale", "expired", "escalated".
/// </summary>
public sealed class SlaEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>What kind of entity the event refers to (see class docs).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Id of the lead/quote/approval/user the event refers to.</summary>
    public long EntityId { get; set; }

    /// <summary>Escalation level (see class docs).</summary>
    public string Level { get; set; } = string.Empty;

    public DateTime CreatedOn { get; set; }
}
