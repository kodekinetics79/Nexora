using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// The lifecycle of one claim. The row is APPEND-ONLY in the sense the audit needs: a
/// claim is never deleted, only transitioned, so "we tried and it did not go out" leaves
/// a record instead of a hole.
///
/// <para><see cref="Claimed"/> — inserted before the send; nobody else may send this alert.</para>
/// <para><see cref="Sent"/> — the transport returned a receipt naming a provider AND an
/// acceptance reference. This is the only outcome that counts as delivered.</para>
/// <para><see cref="Uncertain"/> — the provider MAY already have accepted: the send threw
/// after the transport was entered, or it returned without acceptance evidence. The claim
/// is KEPT, so the alert is never re-sent. A missed escalation is recoverable; a supervisor
/// mailed twice with conflicting information is not.</para>
/// <para><see cref="Released"/> — definitely not sent (the failure happened before the
/// transport could have accepted anything, or there was nothing to send). The row stays as
/// audit, and the partial unique index lets a later sweep claim the same key again.</para>
/// </summary>
public static class SlaEventStatuses
{
    public const string Claimed = "CLAIMED";
    public const string Sent = "SENT";
    public const string Uncertain = "UNCERTAIN";
    public const string Released = "RELEASED";

    /// <summary>The one status that frees the dedup key for a fresh claim. Mirrors
    /// <c>UX_SlaEvents_BU_DedupKey</c>'s filter, which is the enforcement.</summary>
    public static bool IsReclaimable(string? status) =>
        string.Equals(status, Released, StringComparison.Ordinal);
}

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
/// RELEASE POLICY: the claim used to be DELETED whenever the send did not report
/// success, and "success" only ever meant "nothing threw". SMTP that accepted the body
/// and then dropped the connection before its 250 therefore deleted the claim and the
/// next sweep mailed the supervisor the same escalation again, with no record of the
/// first attempt. Releasing is now a STATUS TRANSITION (see <see cref="SlaEventStatuses"/>)
/// and only "definitely not sent" releases; an ambiguous failure settles
/// <see cref="SlaEventStatuses.Uncertain"/> and is never re-sent.
///
/// ONE CLAIM PER RECIPIENT: <see cref="Recipient"/> is part of the dedup key. A single
/// claim used to cover a whole escalation audience under `anyDelivered |= ...`, so one
/// recipient failing kept the claim and the other people were never told, while one
/// recipient succeeding and the rest failing released the claim and mailed the first
/// person twice.
///
/// EntityType values: "lead", "lead-unassigned", "quote", "quote-stale-digest", "approval",
///                    "supplier-order-ship", "supplier-order-ack",
///                    "inbound-shipment-late", "inbound-shipment-risk".
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
    /// Send-once claim key, UNIQUE per business unit among rows that are not
    /// <see cref="SlaEventStatuses.Released"/>. Normally
    /// <c>{EntityType}:{EntityId}:{Level}</c>, plus the UTC day for keys that repeat daily
    /// (the stale-quote digest) and plus a recipient discriminator when the alert names one.
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>
    /// Where this claim's copy was addressed, lower-cased and trimmed. Null only for claims
    /// that gate a non-email action (the quote auto-expiry) or for rows written before the
    /// recipient became part of the key — never an empty string standing in for "unknown".
    /// </summary>
    public string? Recipient { get; set; }

    /// <summary>Lifecycle status — see <see cref="SlaEventStatuses"/>.</summary>
    public string Status { get; set; } = SlaEventStatuses.Claimed;

    /// <summary>Transport that accepted the message. Set only alongside
    /// <see cref="SlaEventStatuses.Sent"/>; it is half of the acceptance evidence.</summary>
    public string? Provider { get; set; }

    /// <summary>The provider's own reference for the accepted message (message id, queue id).
    /// Set only alongside <see cref="SlaEventStatuses.Sent"/>; without it there is no proof the
    /// provider took the message, so the outcome is uncertain rather than delivered.</summary>
    public string? AcceptanceReference { get; set; }

    /// <summary>When the claim left <see cref="SlaEventStatuses.Claimed"/>. Null while in flight.</summary>
    public DateTime? SettledOn { get; set; }

    /// <summary>Why the claim settled the way it did — the failure category for an
    /// uncertain or released claim. Null for a clean send.</summary>
    public string? OutcomeReason { get; set; }

    public DateTime CreatedOn { get; set; }

    /// <summary>Builds the canonical <see cref="DedupKey"/>. Pass <paramref name="dayUtc"/>
    /// only for keys that are meant to repeat daily (the stale-quote digest), and
    /// <paramref name="recipient"/> whenever the claim covers a message to one person —
    /// which is every email claim, so that a failure for one recipient can never suppress
    /// another's copy.
    ///
    /// <para>The recipient is folded to a short hash rather than embedded verbatim: the
    /// column is <c>varchar(120)</c> and an address can be 254 characters on its own. The
    /// readable address is carried by <see cref="Recipient"/>, which is what the audit is
    /// read from; the key only has to discriminate.</para></summary>
    public static string BuildDedupKey(
        string entityType, long entityId, string level, DateTime? dayUtc = null, string? recipient = null)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{entityType}:{entityId}:{level}");
        if (dayUtc is { } day)
            key = string.Create(CultureInfo.InvariantCulture, $"{key}:{day:yyyy-MM-dd}");
        var normalized = NormalizeRecipient(recipient);
        return normalized is null ? key : $"{key}:to:{RecipientToken(normalized)}";
    }

    /// <summary>Lower-cased, trimmed address, or null when there is no recipient. Empty and
    /// whitespace are NOT recipients — they must not silently become a distinct dedup key.</summary>
    public static string? NormalizeRecipient(string? recipient) =>
        string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim().ToLowerInvariant();

    private static string RecipientToken(string normalizedRecipient)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRecipient));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
