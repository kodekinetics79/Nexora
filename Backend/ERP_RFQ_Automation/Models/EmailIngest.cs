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

    public virtual EmailConfiguration EmailConfiguration { get; set; } = null!;

    public virtual ICollection<Lead> Leads { get; set; } = new List<Lead>();
}
