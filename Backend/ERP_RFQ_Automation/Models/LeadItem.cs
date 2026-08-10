using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class LeadItem
{
    public long Id { get; set; }

    public long LeadId { get; set; }

    public string? CompanyRef { get; set; }

    public string? CustomerAccountPortalId { get; set; }

    public string? CustomerRfqno { get; set; }

    public string? ItemMaterialCode { get; set; }

    public string? CommodityProduct { get; set; }

    public string? BuyerName { get; set; }

    public string? LineItemNo { get; set; }

    public string? ProductShortName { get; set; }

    public string? Alternative { get; set; }

    public string? ProductShortDescription { get; set; }

    public string? Currency { get; set; }

    public string? UnitOfMeasure { get; set; }

    public decimal? UnitPrice { get; set; }

    /// <summary>
    /// The demand quantity the document stated, or NULL when it stated none we could read.
    ///
    /// <para><b>Why this is nullable.</b> It was <c>int</c>, and every door wrote
    /// <c>source.Quantity ?? 0</c> — so "the document says 2,500 and we could not read it" and
    /// "the document says 0" were stored as the same number. Zero is a value, not an absence: it
    /// reads as a real demand of nothing, passes review looking plausible, and is quoted. Null is
    /// the only representation of "unknown" that a reviewer, a guard or a screen can tell apart
    /// from a stated zero, and every consumer below now has to decide which it is looking at.</para>
    ///
    /// <para>Nothing downstream may coalesce this to 0. A line whose quantity is unknown is a line
    /// that cannot be quoted until a human supplies it.</para>
    /// </summary>
    public int? Quantity { get; set; }

    public string? StorageLocation { get; set; }

    public string? ManufacturerName { get; set; }

    public string? ManufacturerPartNumber { get; set; }

    public string? AlternateProductName { get; set; }

    public string? AlternatePartNumber { get; set; }

    public string? ItemText { get; set; }

    public string? MaterialPotext { get; set; }

    public int? LeadTime { get; set; }

    public DateTime? ReceivedDate { get; set; }

    public DateTime? BidClosingDateLine { get; set; }

    public decimal? Aiconfidence { get; set; }

    public virtual Lead Lead { get; set; } = null!;
}
