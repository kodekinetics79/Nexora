using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class EmailIngest
{
    public long Id { get; set; }

    public string MessageId { get; set; } = null!;

    public string? EmailSubject { get; set; }

    public string FromEmail { get; set; } = null!;

    public string? ToEmail { get; set; }

    public string? RawEmailPath { get; set; }

    public DateTime? ParsedAt { get; set; }

    public long EmailConfigurationId { get; set; }

    public string? ParseStatus { get; set; }

    public DateTime CreatedOn { get; set; }

    /// <summary>
    /// ING-07: what the intake gate decided about this message
    /// (<c>Inquiry</c> | <c>CommercialNonInquiry</c> | <c>Noise</c> | <c>Uncertain</c>).
    /// Null for messages ingested before the gate existed.
    /// </summary>
    public string? TriageOutcome { get; set; }

    /// <summary>JSON array of stable snake_case reason codes behind
    /// <see cref="TriageOutcome"/>. This is what makes a rejection explainable — and
    /// therefore reversible — instead of a silent drop.</summary>
    public string? TriageReasonJson { get; set; }

    public DateTime? TriageDecidedOn { get; set; }

    /// <summary>
    /// ING-06: JSON array of <c>"filename (reason)"</c> for every attachment on this message
    /// that was NOT handed to extraction — unsupported type, oversize, empty, unnamed, embedded
    /// message. Written on EVERY fan-out path, not only the one where the body produced a job:
    /// a quoted-only reply carrying one supported and one unsupported attachment used to record
    /// the dropped file nowhere at all. Null means nothing was skipped.
    /// </summary>
    public string? SkippedAttachmentsJson { get; set; }

    /// <summary>
    /// FR-RFQ-05/06: the RFC 5322 In-Reply-To header — the Message-Id of the message this one
    /// directly replies to — normalized by <c>EmailService.NormalizeMessageId</c> (angle
    /// brackets stripped) so it joins against <see cref="MessageId"/> on equal terms. Null when
    /// the message is not a reply, was ingested before this column existed, or carries a
    /// pathological id longer than the 255-char key space (such an id can never match a stored
    /// MessageID anyway, so storing a truncation would only manufacture false joins).
    /// </summary>
    public string? InReplyToMessageId { get; set; }

    /// <summary>
    /// FR-RFQ-05/06: the RFC 5322 References header — the ordered ancestor Message-Id chain,
    /// oldest first — stored as a JSON array of normalized ids, the same shape as
    /// <see cref="SkippedAttachmentsJson"/>. This is what lets reconciliation see that a reply
    /// belongs to the thread of an earlier ingested message even when In-Reply-To is absent.
    /// When the chain does not fit the column, the NEWEST ids are kept: the nearest ancestors
    /// are the ones most likely to be in our ingest ledger.
    /// </summary>
    public string? ReferencesJson { get; set; }

    public virtual EmailConfiguration EmailConfiguration { get; set; } = null!;

    public virtual ICollection<Lead> Leads { get; set; } = new List<Lead>();
}
