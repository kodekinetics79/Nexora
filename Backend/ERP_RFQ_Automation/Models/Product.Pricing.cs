namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The part's price-list entry, kept on the Pricing sheet by a manager or other authorised person:
/// <see cref="Product.UnitCost"/> is the landed cost (what one unit costs us, delivered) and
/// <see cref="Product.SellingPrice"/> the sale price, both in <see cref="PriceCurrency"/>.
///
/// Before this, neither figure carried a currency: the product form labelled them "$" while every
/// quote went out in SAR, and the RFQ pricing window assumed whatever currency the quote had. A
/// price is now in the currency its keeper chose, and nothing converts it silently.
/// </summary>
public partial class Product
{
    /// <summary>Null on a part nobody has priced on the sheet yet.</summary>
    public long? PriceCurrencyId { get; set; }

    public virtual Currency? PriceCurrency { get; set; }
}
