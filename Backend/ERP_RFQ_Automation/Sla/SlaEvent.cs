using System;
using System.Globalization;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Append-only record of one SLA notification/action, used both as an audit trail
/// and as the send-once dedup ledger.
///
/// SCALE-OUT NOTE: the worker used to do lookup-before-insert against a NON-unique
/// index, so two instances sweeping concurrently both passed the lookup and both
/// sent the same customer-facing deadline email / manager escalation. The ledger is
/// now claim-before-send: <see cref="DedupKey"/> carries a UNIQUE constraint per
/// business unit, the worker INSERTs the claim first and only sends when the insert
/// won. A losing instance gets a PostgreSQL 23505 and skips the send entirely.
///
/// EntityType values: "lead", "lead-unassigned", "quote", "quote-stale-digest", "approval",
///                    "supplier-order-ship", "supplier-order-ack".
/// Level values:      "warn", "critical", "overdue", "stale", "expired", "escalated".
///
/// The supplier ship-date reminder passes the COMMITTED SHIP DATE as the dayUtc component of
/// <see cref="BuildDedupKey"/>, so it is "once per order per committed date" rather than
/// "once ever" — a supplier who counters with a new date earns the buyer a fresh reminder.
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

    /// <summary>
    /// Send-once claim key, UNIQUE per business unit. Normally
    /// <c>{EntityType}:{EntityId}:{Level}</c>; the stale-quote digest appends the UTC
    /// day so it stays "once per owner per day" rather than "once ever".
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    public DateTime CreatedOn { get; set; }

    /// <summary>Builds the canonical <see cref="DedupKey"/>. Pass <paramref name="dayUtc"/>
    /// only for keys that are meant to repeat daily (the stale-quote digest).</summary>
    public static string BuildDedupKey(string entityType, long entityId, string level, DateTime? dayUtc = null)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{entityType}:{entityId}:{level}");
        return dayUtc is { } day
            ? string.Create(CultureInfo.InvariantCulture, $"{key}:{day:yyyy-MM-dd}")
            : key;
    }
}
