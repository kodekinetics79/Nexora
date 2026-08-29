namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Exact customer terms inherited from the immutable Lead revision at governed promotion.
/// These are deliberately separate from Nexora's generated <see cref="Rfq.Rfqno"/> and from
/// supplier-side fulfilment terms.
/// </summary>
public partial class Rfq
{
    public string? CustomerRfqReference { get; set; }
    public DateTime? RequiredDeliveryDate { get; set; }
    public string? DeliveryLocation { get; set; }
    public string? AgreementReference { get; set; }
    public string? BidClosingDateHijri { get; set; }
    public string? InquiryType { get; set; }
}

public partial class Rfqitem
{
    /// <summary>
    /// Verbatim jsonb object captured from unrecognised customer columns (including plant,
    /// Incoterms, project and cost-centre headings). Promotion copies this value byte-for-byte
    /// from the immutable LeadItemRevision snapshot.
    /// </summary>
    public string? ExtraFields { get; set; }
}
