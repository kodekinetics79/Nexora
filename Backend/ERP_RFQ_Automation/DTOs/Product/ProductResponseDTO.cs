using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.DTOs.ProductDTOs
{
    public class ProductResponseDTO
    {
        public long Id { get; set; }
        public string? DocId { get; set; }
        public string? ProductName { get; set; }
        public string PartNo { get; set; } = null!;
        public string? ModelNo { get; set; }
        public string? Description { get; set; }
        public long? CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public decimal QtyOnHand { get; set; }
        public decimal ReorderPoint { get; set; }
        public int? UomId { get; set; }
        public string? UomName { get; set; }
        public decimal? UnitCost { get; set; }
        public decimal? SellingPrice { get; set; }
        public decimal? FinalLandedCost { get; set; }
        public decimal? FinalSalesPrice { get; set; }

        public long? WarehouseId { get; set; }
        public string? WarehouseName { get; set; }
        public long? PreferredSupplierId { get; set; }
        public string? PreferredSupplierName { get; set; }
        public string? PreferredSupplierEmail { get; set; }
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
        public long? Buid { get; set; }
        public string? BusinessUnitName { get; set; }
        public bool IsActive { get; set; }
        public bool? IsCatalogItem { get; set; }
        public int? SubCategoryId { get; set; }
        public string? SubCategoryName { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
        public List<ProductAttachmentDTO> Images { get; set; } = new List<ProductAttachmentDTO>();
        public List<ProductAttachmentDTO> Attachments { get; set; } = new List<ProductAttachmentDTO>();
    }

    public class ProductAttachmentDTO
    {
        public long AttachmentId { get; set; }
        public string FileName { get; set; } = null!;
        public string Location { get; set; } = null!;
        public string? Description { get; set; }
    }

    public class ProductCreateRequestDTO
    {
        /// <summary>
        /// 100, because <c>Products."ProductName"</c> is <c>character varying(100)</c>. DO NOT
        /// WIDEN THIS. The cap mirrors the column so an over-long name is refused as a 400 that
        /// names the field; while this said 200 the value passed ModelState and died at the INSERT
        /// as <c>22001</c>, which reached the caller as "An unexpected error occurred."
        ///
        /// <para>Widening the COLUMN to match instead is not available: <c>View_SupplierPriceList</c>
        /// selects <c>ProductName</c>, PostgreSQL refuses "cannot alter type of a column used by a
        /// view or rule", and <c>Program.cs</c> runs <c>MigrateAsync()</c> unguarded at startup — so
        /// that migration fails the deploy itself.</para>
        /// </summary>
        [StringLength(100)]
        public string? ProductName { get; set; }

        [Required, StringLength(100)]
        public string PartNo { get; set; } = null!;

        [StringLength(100)]
        public string? ModelNo { get; set; }

        /// <summary>
        /// 500, mirroring <c>Products."Description"</c> — <c>character varying(500)</c>. Same
        /// reason as <see cref="ProductName"/>: the cap exists so the refusal happens at validation
        /// rather than at the INSERT. Do not widen it without widening the column first.
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        public long? CategoryId { get; set; }

        [Required, Range(0, double.MaxValue)]
        public decimal QtyOnHand { get; set; }

        [Required, Range(0, double.MaxValue)]
        public decimal ReorderPoint { get; set; }

        public int? UomId { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? UnitCost { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? SellingPrice { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? FinalLandedCost { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? FinalSalesPrice { get; set; }


        public long? WarehouseId { get; set; }

        public long? PreferredSupplierId { get; set; }

        public bool? BatchTracking { get; set; }

        public bool? SerialTracking { get; set; }

        public DateOnly? ExpirationDate { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Height { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Width { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Depth { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Weight { get; set; }

        [StringLength(100)]
        public string? Dimensions { get; set; }

        [StringLength(50)]
        public string? Barcode { get; set; }

        [StringLength(50)]
        public string? Qrcode { get; set; }

        [Range(0, int.MaxValue)]
        public int? LeadTime { get; set; }

        [StringLength(20)]
        public string? Hscode { get; set; }

        [StringLength(100)]
        public string? CountryOfOrigin { get; set; }

        [Required]
        public long Buid { get; set; }

        public bool? IsActive { get; set; } = true;

        public bool? IsCatalogItem { get; set; }

        public int? SubCategoryId { get; set; }

        [Required]
        public string CreatedBy { get; set; } = null!;

        public List<IFormFile>? Attachments { get; set; }
    }

    public class ProductUpdateRequestDTO
    {
        /// <summary>
        /// 100, because <c>Products."ProductName"</c> is <c>character varying(100)</c>. DO NOT
        /// WIDEN THIS. The cap mirrors the column so an over-long name is refused as a 400 that
        /// names the field; while this said 200 the value passed ModelState and died at the INSERT
        /// as <c>22001</c>, which reached the caller as "An unexpected error occurred."
        ///
        /// <para>Widening the COLUMN to match instead is not available: <c>View_SupplierPriceList</c>
        /// selects <c>ProductName</c>, PostgreSQL refuses "cannot alter type of a column used by a
        /// view or rule", and <c>Program.cs</c> runs <c>MigrateAsync()</c> unguarded at startup — so
        /// that migration fails the deploy itself.</para>
        /// </summary>
        [StringLength(100)]
        public string? ProductName { get; set; }

        [Required, StringLength(100)]
        public string PartNo { get; set; } = null!;

        [StringLength(100)]
        public string? ModelNo { get; set; }

        /// <summary>
        /// 500, mirroring <c>Products."Description"</c> — <c>character varying(500)</c>. Same
        /// reason as <see cref="ProductName"/>: the cap exists so the refusal happens at validation
        /// rather than at the INSERT. Do not widen it without widening the column first.
        /// </summary>
        [StringLength(500)]
        public string? Description { get; set; }

        public long? CategoryId { get; set; }

        [Range(0, double.MaxValue)]
        public decimal QtyOnHand { get; set; }

        [Range(0, double.MaxValue)]
        public decimal ReorderPoint { get; set; }

        public int? UomId { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? UnitCost { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? SellingPrice { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? FinalLandedCost { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? FinalSalesPrice { get; set; }


        public long? WarehouseId { get; set; }

        public long? PreferredSupplierId { get; set; }

        public bool? BatchTracking { get; set; }

        public bool? SerialTracking { get; set; }

        public DateOnly? ExpirationDate { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Height { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Width { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Depth { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Weight { get; set; }

        [StringLength(100)]
        public string? Dimensions { get; set; }

        [StringLength(50)]
        public string? Barcode { get; set; }

        [StringLength(50)]
        public string? Qrcode { get; set; }

        [Range(0, int.MaxValue)]
        public int? LeadTime { get; set; }

        [StringLength(20)]
        public string? Hscode { get; set; }

        [StringLength(100)]
        public string? CountryOfOrigin { get; set; }

        [Required]
        public long Buid { get; set; }

        public bool? IsActive { get; set; }

        public bool? IsCatalogItem { get; set; }

        public int? SubCategoryId { get; set; }

        [Required]
        public string ModifiedBy { get; set; } = null!;

        public List<IFormFile>? Attachments { get; set; }
    }

    public class PaginatedProductResponseDTO
    {
        public IEnumerable<ProductResponseDTO> Items { get; set; } = new List<ProductResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}