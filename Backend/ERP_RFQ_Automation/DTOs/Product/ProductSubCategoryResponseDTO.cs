
using System.ComponentModel.DataAnnotations;
namespace ERP_RFQ_Automation.DTOs.ProductSubCategory
{
    public class ProductSubCategoryResponseDTO
    {
        public int Id { get; set; }
        public string SubCategoryName { get; set; } = null!;
        public string? Description { get; set; }
        public long? BusinessUnitId { get; set; }
        public bool IsActive { get; set; }               
        public string? CreatedBy { get; set; }
        public DateTime? CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }



    public class PaginatedProductSubCategoryResponseDTO
    {
        public IEnumerable<ProductSubCategoryResponseDTO> Items { get; set; } = new List<ProductSubCategoryResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }


    public class ProductSubCategoryCreateRequestDTO
    {
        [Required, StringLength(100)]
        public string SubCategoryName { get; set; } = null!;

        [StringLength(500)]
        public string? Description { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }


    public class ProductSubCategoryUpdateRequestDTO
    {
        [Required, StringLength(100)]
        public string SubCategoryName { get; set; } = null!;

        [StringLength(500)]
        public string? Description { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public bool? IsActive { get; set; } = true;
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }
}