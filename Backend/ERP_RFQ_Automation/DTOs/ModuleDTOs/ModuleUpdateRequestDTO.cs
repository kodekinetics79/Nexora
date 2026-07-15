using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.ModuleDTOs
{
    public class ModuleUpdateRequestDTO
    {
        [Required]
        public string ModuleName { get; set; } = null!;

        public string? Description { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public bool? IsActive { get; set; }

        public string? ModifiedBy { get; set; }
    }
}