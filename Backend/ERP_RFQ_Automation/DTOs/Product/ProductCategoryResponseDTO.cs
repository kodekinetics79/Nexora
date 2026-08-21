using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.ProductCategory
{
    public class ProductCategoryResponseDTO
    {
        public long Id { get; set; }
        public string CategoryName { get; set; } = null!;
        public string? Description { get; set; }
        public long? ParentCategoryId { get; set; }
        public string? ParentCategoryName { get; set; }     
        public long BusinessUnitId { get; set; }
        public bool IsActive { get; set; }                 
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }

 public class PaginatedProductCategoryResponseDTO
    {
        public IEnumerable<ProductCategoryResponseDTO> Items { get; set; } = new List<ProductCategoryResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }



 public class ProductCategoryCreateRequestDTO
    {
        [Required, StringLength(100)]
        public string CategoryName { get; set; } = null!;

        /// <summary>
        /// 255, because <c>ProductCategories."Description"</c> is <c>character varying(255)</c>. DO
        /// NOT WIDEN THIS. The cap mirrors the column so an over-long description is refused as a
        /// 400 that names the field; while this said 500 a 300-character description passed
        /// ModelState and died at the INSERT as Postgres <c>22001</c>, which reached the caller as
        /// "An unexpected error occurred." — the identical defect products carried into production
        /// on 2026-08-20.
        ///
        /// <para>Widening the COLUMN to match instead is not available. It would need a migration,
        /// and migrations are not a free option here: <c>Program.cs</c> runs <c>MigrateAsync()</c>
        /// unguarded at startup, so a migration that fails fails the DEPLOY rather than degrading.
        /// That is not hypothetical — the sibling widening of <c>Products."ProductName"</c> was
        /// ruled out for exactly this reason (<c>View_SupplierPriceList</c> selects the column and
        /// PostgreSQL refuses "cannot alter type of a column used by a view or rule"). The
        /// attribute comes down to meet the column, in both directions, on every DTO.</para>
        /// </summary>
        [StringLength(255)]
        public string? Description { get; set; }

        public long? ParentCategoryId { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }



    public class ProductCategoryUpdateRequestDTO
    {
        [Required, StringLength(100)]
        public string CategoryName { get; set; } = null!;

        /// <summary>
        /// 255, because <c>ProductCategories."Description"</c> is <c>character varying(255)</c>. DO
        /// NOT WIDEN THIS. The cap mirrors the column so an over-long description is refused as a
        /// 400 that names the field; while this said 500 a 300-character description passed
        /// ModelState and died at the INSERT as Postgres <c>22001</c>, which reached the caller as
        /// "An unexpected error occurred." — the identical defect products carried into production
        /// on 2026-08-20.
        ///
        /// <para>Widening the COLUMN to match instead is not available. It would need a migration,
        /// and migrations are not a free option here: <c>Program.cs</c> runs <c>MigrateAsync()</c>
        /// unguarded at startup, so a migration that fails fails the DEPLOY rather than degrading.
        /// That is not hypothetical — the sibling widening of <c>Products."ProductName"</c> was
        /// ruled out for exactly this reason (<c>View_SupplierPriceList</c> selects the column and
        /// PostgreSQL refuses "cannot alter type of a column used by a view or rule"). The
        /// attribute comes down to meet the column, in both directions, on every DTO.</para>
        /// </summary>
        [StringLength(255)]
        public string? Description { get; set; }

        public long? ParentCategoryId { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public bool? IsActive { get; set; } = true;
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }
}