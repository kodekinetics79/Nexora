using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class QuoteConfiguration
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public string? Logo { get; set; }

    public string? PrimaryColor { get; set; }

    public string? TermsAndConditions { get; set; }

    public string? CompanyAddress { get; set; }

    public string? CompanyPhone { get; set; }

    public string? CompanyEmail { get; set; }

    public string? FooterText { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;
}
