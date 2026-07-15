using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Product
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

    public int? UomId { get; set; }

    public decimal? UnitCost { get; set; }

    public decimal? SellingPrice { get; set; }

    public long? WarehouseId { get; set; }

    public long? PreferredSupplierId { get; set; }

    public bool? BatchTracking { get; set; }

    public bool? SerialTracking { get; set; }

    public DateOnly? ExpirationDate { get; set; }

    public decimal? Height { get; set; }

    public decimal? Width { get; set; }

    public decimal? Depth { get; set; }

    public decimal? Weight { get; set; }

    public string? Dimensions { get; set; }

    public string? Barcode { get; set; }

    public string? Qrcode { get; set; }

    public int? LeadTime { get; set; }

    public string? Hscode { get; set; }

    public string? CountryOfOrigin { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public long? Buid { get; set; }

    public bool? IsActive { get; set; }

    public bool? IsCatalogItem { get; set; }

    public int? SubCategoryId { get; set; }

    public decimal? FinalLandedCost { get; set; }

    public decimal? FinalSalesPrice { get; set; }

    public virtual BusinessUnit? Bu { get; set; }

    public virtual ProductCategory? Category { get; set; }

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual Supplier? PreferredSupplier { get; set; }

    public virtual ICollection<ProductAttachment> ProductAttachments { get; set; } = new List<ProductAttachment>();

    public virtual ICollection<QuoteItem> QuoteItems { get; set; } = new List<QuoteItem>();

    public virtual ICollection<Rfqitem> Rfqitems { get; set; } = new List<Rfqitem>();

    public virtual ProductSubCategory? SubCategory { get; set; }

    public virtual ICollection<SupplierPurchaseHistory> SupplierPurchaseHistories { get; set; } = new List<SupplierPurchaseHistory>();

    public virtual SetUom? Uom { get; set; }

    public virtual Warehouse? Warehouse { get; set; }
}
