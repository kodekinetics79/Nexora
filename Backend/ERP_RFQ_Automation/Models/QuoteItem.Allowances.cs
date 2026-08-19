using System.ComponentModel.DataAnnotations.Schema;

namespace ERP_RFQ_Automation.Models;

public partial class QuoteItem
{
    /// <summary>
    /// This line's share of the QUOTE-LEVEL discount (<see cref="Quote.DiscountValue"/>), allocated
    /// pro rata by line net so the shares sum exactly to the header discount.
    ///
    /// <para>Persisted rather than re-derived because re-derivation is what broke. Three separate
    /// places used to reconstruct the header discount by subtracting the stored quote total from a
    /// sum of line values — <c>QuoteService</c>'s PDF builder, <c>OrderService</c> and
    /// <c>CustomerAwardApplicationService</c> — and each reconstruction disagreed with the others
    /// about whether tax was inside the figure. The printed "Additional Discount" came out 15%
    /// larger than the discount the rep actually entered.</para>
    ///
    /// <para><b>It is also the line-level allowance a ZATCA invoice has to state.</b> Output tax is
    /// charged on the consideration after ALL discounts, so a header discount that exists only at
    /// document level cannot be pushed down to a compliant line without an allocation. Storing it
    /// here means the taxable base, the allowance and the tax on every line are read, never
    /// recomputed, by whatever eventually serialises the invoice.</para>
    ///
    /// <para>Zero means "there was no header discount on this quote", which is the ordinary case.
    /// Null means the line predates this column and was written by the arithmetic that did not
    /// allocate at all — a distinction worth keeping, because those rows carry a tax amount derived
    /// on a base that ignored the header discount.</para>
    /// </summary>
    public decimal? HeaderDiscountAllocated { get; set; }

    /// <summary>
    /// The amount output tax was charged on: this line's net after its own discount AND its share
    /// of the header discount. Derived, not stored — <see cref="TotalAmount"/> carries the same
    /// figure plus tax, and the two must never be able to disagree.
    /// </summary>
    [NotMapped]
    public decimal TaxableBase => TotalAmount - (TaxAmount ?? 0m);
}
