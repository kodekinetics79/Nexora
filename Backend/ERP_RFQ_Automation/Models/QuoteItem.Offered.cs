namespace ERP_RFQ_Automation.Models;

public partial class QuoteItem
{
    /// <summary>
    /// Taken from the RFQ line when the quote is priced: the sentence the customer reads under the
    /// item when what is offered is not the part they asked for ("Offered: GE THQL32010, replaces
    /// ABB AF96-30-00-13"), followed by the specs when it is an equivalent.
    /// </summary>
    public string? OfferedNote { get; set; }

    /// <summary>Specs of an offered equivalent, printed so the customer can judge it.</summary>
    public string? OfferedSpecs { get; set; }
}
