namespace ERP_RFQ_Automation.Models;

public partial class Inventory
{
    public long? ProductId { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal QuarantineQuantity { get; set; }
    public decimal DamagedQuantity { get; set; }
    public decimal ExpiredQuantity { get; set; }
    public decimal SafetyStockQuantity { get; set; }

    /// <summary>
    /// FR-INV-04. The stock level below which this item, in this warehouse, is short and somebody
    /// must buy. <b>Null means "not configured", and never zero.</b>
    ///
    /// <para>Zero would be a claim — "this item is fine at nothing on hand" — and the reorder sweep
    /// would have to decide whether to believe it. Null cannot be misread: the sweep skips the row
    /// entirely, and the screen renders "Not set" rather than a number nobody chose. The code
    /// default and the migration backfill are both <c>NULL</c> and they mean the same thing, which
    /// is the check wiring-contract failure #10 exists for.</para>
    ///
    /// <para><b>Why it lives on the stock row and not on the product.</b> The product already
    /// carries <c>ReorderPoint</c>, and copying it down to the stock row once at creation time and
    /// never re-syncing is precisely the defect this gate found and fixed. Minimum and maximum get
    /// exactly one home — the (item, warehouse) pair the alert is evaluated at — so there is no
    /// second copy that can drift out of agreement with the first.</para>
    /// </summary>
    public decimal? MinimumLevel { get; set; }

    /// <summary>
    /// FR-INV-04. The stock level above which this item, in this warehouse, is overstocked: capital
    /// and space tied up in units nobody is asking for. <b>Null means "not configured".</b>
    ///
    /// <para>A maximum defaulting to <c>0</c> would read as "any stock at all is too much" and the
    /// first sweep after deployment would raise an overstock alert against every row in every
    /// warehouse — after which the channel is filtered and every real alert is lost with it. That is
    /// the same trap a zero-backfilled SLA column set two gates ago, in the opposite direction.</para>
    /// </summary>
    public decimal? MaximumLevel { get; set; }
}
