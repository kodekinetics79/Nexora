using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Rfqitem
{
    public long Id { get; set; }

    public long Rfqid { get; set; }

    public string? CompanyRef { get; set; }

    public string? CustomerAccountPortalId { get; set; }

    public string? CustomerRfqno { get; set; }

    public string? ItemMaterialCode { get; set; }

    public string? LineItemNo { get; set; }

    public long? ProductId { get; set; }

    public string? CommodityProduct { get; set; }

    public string? ProductShortName { get; set; }

    public string? ProductShortDescription { get; set; }

    public string? Alternative { get; set; }

    public string? BuyerName { get; set; }

    public string? Currency { get; set; }

    public long? CurrencyId { get; set; }

    public string? UnitOfMeasure { get; set; }

    public int? UomId { get; set; }

    public decimal? UnitPrice { get; set; }

    /// <summary>Null means the customer did not state a quantity; never substitute a demand.</summary>
    public int? Quantity { get; set; }

    public string? StorageLocation { get; set; }

    public long? WarehouseId { get; set; }

    public string? ManufacturerName { get; set; }

    public string? ManufacturerPartNumber { get; set; }

    public long? SupplierId { get; set; }

    public string? AlternateProductName { get; set; }

    public string? AlternatePartNumber { get; set; }

    public string? ItemText { get; set; }

    public string? MaterialPotext { get; set; }

    public int? LeadTime { get; set; }

    public DateTime? RequiredDesiredDate { get; set; }

    public DateTime? ReceivedDate { get; set; }

    public DateTime? BidClosingDateLine { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public decimal? Aiconfidence { get; set; }

    public long? SupplierQuotedItemId { get; set; }

    public virtual Currency? CurrencyNavigation { get; set; }

    public virtual Product? Product { get; set; }

    public virtual ICollection<QuoteItem> QuoteItems { get; set; } = new List<QuoteItem>();

    public virtual Rfq Rfq { get; set; } = null!;

    public virtual Supplier? Supplier { get; set; }

    public virtual SupplierQuotedItem? SupplierQuotedItem { get; set; }

    public virtual SetUom? Uom { get; set; }

    public virtual Warehouse? Warehouse { get; set; }
}
