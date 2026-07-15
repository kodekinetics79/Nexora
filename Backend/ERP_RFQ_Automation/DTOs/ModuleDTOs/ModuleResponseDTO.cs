using System;

namespace ERP_RFQ_Automation.DTOs.ModuleDTOs
{
    public class ModuleResponseDTO
    {
        public long Id { get; set; }
        public string ModuleName { get; set; } = null!;
        public string? Description { get; set; }
        public long BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}