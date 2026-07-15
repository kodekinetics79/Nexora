using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.ModuleDTOs
{
    public class ModuleCreateRequestDTO
    {
        [Required]
        public string ModuleName { get; set; } = null!;

        public string? Description { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public string? CreatedBy { get; set; }
    }
}