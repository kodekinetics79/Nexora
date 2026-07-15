using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.RolePermission
{
    public class RolePermissionCreateRequestDTO
    {
        [Required]
        public long? RoleId { get; set; }

        [Required]
        public long ModuleId { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public bool? CanCreate { get; set; }

        public bool? CanEdit { get; set; }

        public bool? CanDelete { get; set; }

        public string? CreatedBy { get; set; }
    }
}