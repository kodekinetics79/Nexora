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

    public virtual EmailConfiguration EmailConfiguration { get; set; } = null!;

    public virtual ICollection<Lead> Leads { get; set; } = new List<Lead>();
}
