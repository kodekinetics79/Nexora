using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Inventory
{
    public long Id { get; set; }

    public string? DocId { get; set; }

    public string? ProductName { get; set; }

    public string PartNo { get; set; } = null!;

    public string? ModelNo { get; set; }

    public string? Description { get; set; }

    public long? CategoryId { get; set; }

    public decimal QtyOnHand { get; set; }

    public decimal ReorderPoint { get; set; }

    public decimal? UnitCost { get; set; }

    public decimal? SellingPrice { get; set; }

    public long? WarehouseId { get; set; }

    public long? PreferredSupplierId { get; set; }

    public long? ItemStatusId { get; set; }

    public bool? BatchTracking { get; set; }

    public bool? SerialTracking { get; set; }

    public DateOnly? ExpirationDate { get; set; }

    public long? TaxId { get; set; }

    public decimal? Weight { get; set; }

    public string? Dimensions { get; set; }

    public string? Barcode { get; set; }

    public string? Qrcode { get; set; }

    public int? LeadTime { get; set; }

    public string? Hscode { get; set; }

    public string? CountryOfOrigin { get; set; }

    public long? PrimaryImageId { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public long? Buid { get; set; }

    public virtual BusinessUnit? Bu { get; set; }

    public virtual InventoryCategory? Category { get; set; }

    public virtual ICollection<InventoryAttachment> InventoryAttachments { get; set; } = new List<InventoryAttachment>();

    public virtual SetupMaster? ItemStatus { get; set; }

    public virtual Supplier? PreferredSupplier { get; set; }

    public virtual Image? PrimaryImage { get; set; }

    public virtual Taxis? Tax { get; set; }

    public virtual Warehouse? Warehouse { get; set; }
}
