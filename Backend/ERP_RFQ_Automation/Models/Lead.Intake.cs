namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Intake fields required by FR-RFQ-03 and FR-RFQ-04 that had no column at all, so the values
/// were read from the document and then discarded.
/// </summary>
public partial class Lead
{
    /// <summary>
    /// FR-RFQ-04. The buyer's delivery location — Saudi region or city — as stated on the
    /// inquiry. Free text on purpose: it is the buyer's own wording, and normalising it against
    /// the city master is a separate, reviewable step. Nothing is inferred from it.
    /// </summary>
    public string? DeliveryLocation { get; set; }

    /// <summary>
    /// FR-RFQ-04. The delivery date the BUYER is asking for, which is not the same thing as a
    /// supplier's lead time and must never be written into one.
    /// </summary>
    public DateTime? RequiredDeliveryDate { get; set; }

    /// <summary>
    /// FR-RFQ-04. The closing date in the Umm al-Qura calendar, stored as the rendered
    /// <c>yyyy-MM-dd</c> Hijri string alongside the Gregorian <see cref="BidClosingDate"/> rather
    /// than instead of it.
    ///
    /// <para>Saudi government tenders publish closing dates in Hijri. This is a data-correctness
    /// concern rather than a language one — a Hijri deadline read as a Gregorian one loses the
    /// bid — which is why it is in scope even though the Arabic interface is deferred.</para>
    /// </summary>
    public string? BidClosingDateHijri { get; set; }

    /// <summary>
    /// FR-RFQ-03. The reference of a standing agreement or frame contract this inquiry is called
    /// off against, kept separate from the inquiry's own <see cref="Rfqno"/>. Distinct from the
    /// free-text <see cref="DurationAgreement"/> note, which describes the arrangement rather
    /// than identifying it.
    /// </summary>
    public string? AgreementReference { get; set; }
}
