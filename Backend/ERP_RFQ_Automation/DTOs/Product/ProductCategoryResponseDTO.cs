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

        [StringLength(500)]
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

        [StringLength(500)]
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