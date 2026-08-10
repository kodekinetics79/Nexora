using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class QuoteItem
{
    public long Id { get; set; }

    public long QuoteId { get; set; }

    public long? RfqitemId { get; set; }

    public long? ProductId { get; set; }

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal? Discount { get; set; }

    /// <summary>
    /// Output tax on this line, DERIVED server-side by <c>OrderToCash.OutputTaxFormula</c> from the
    /// line's taxable base, the tenant's <c>CommercialMatchingPolicy.OutputTaxRatePercent</c> and
    /// the <see cref="TaxCategory"/> the user chose.
    ///
    /// <para>Never taken from the client. It was, and the result was that nothing computed output
    /// tax at all: the value defaulted to null and validation rejected only negative amounts, so a
    /// quote left the building with no VAT on it — which under KSA law makes the price deemed
    /// VAT-inclusive and costs the seller 15/115 of it (decision R17).</para>
    /// </summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>
    /// The tax treatment the USER stated for this line: STANDARD, ZERO_RATED_EXPORT, EXEMPT or
    /// OUT_OF_SCOPE_RCM (see <c>OrderToCash.QuoteLineTaxCategories</c>). Null on lines written
    /// before decision R19 and read as STANDARD.
    ///
    /// <para>Without it a correctly zero-rated export and a Riyadh sale where the rep forgot the
    /// 15% are byte-identical records. The system does not infer this from an address or a delivery
    /// term — a Riyadh-registered customer can still buy for export — it records what was decided.</para>
    /// </summary>
    public string? TaxCategory { get; set; }

    /// <summary>
    /// Why this line departs from the standard rate. Required whenever <see cref="TaxCategory"/> is
    /// not STANDARD, because that is the assertion an auditor will ask for evidence of; null on a
    /// standard-rated line, which asserts nothing unusual.
    /// </summary>
    public string? TaxCategoryReason { get; set; }

    /// <summary>
    /// The rate actually applied when <see cref="TaxAmount"/> was derived — the tenant's rate on a
    /// standard-rated line, 0 on any other category.
    ///
    /// <para>Null means the tax was NEVER DERIVED, which is a different state from "derived to
    /// zero" and the one the quote send gate refuses on. <see cref="TaxAmount"/> cannot carry that
    /// distinction: its column has a database default of 0, so a row inserted with a null amount
    /// silently stores zero.</para>
    /// </summary>
    public decimal? TaxRatePercentApplied { get; set; }

    public int? DeliveryLeadTime { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime? CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public long? DiscountTypeId { get; set; }

    public decimal? DiscountValue { get; set; }

    /// <summary>Unit the quantity is quoted in (EA, PACK, M, …), carried from the RFQ line
    /// so the customer-facing document never prints a bare quantity. Null on legacy rows.</summary>
    public string? UnitOfMeasure { get; set; }

    /// <summary>The buyer's own line reference from their RFQ (e.g. SAP "00010", "OPT-29",
    /// traced from LeadItem/Rfqitem.LineItemNo). Printed instead of a synthetic 1,2,3 so the
    /// buyer can match our quote lines against their request. Null on legacy rows.</summary>
    public string? CustomerLineRef { get; set; }

    public virtual SetupMaster? DiscountType { get; set; }

    public virtual Product? Product { get; set; }

    public virtual Quote Quote { get; set; } = null!;

    public virtual Rfqitem? Rfqitem { get; set; }
}
