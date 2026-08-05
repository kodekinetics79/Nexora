using System;

namespace ERP_RFQ_Automation.DTOs.RolePermission
{
    public class RolePermissionResponseDTO
    {
        public long Id { get; set; }
        public long? RoleId { get; set; }
        public string? RoleName { get; set; }
        public long ModuleId { get; set; }
        public string? ModuleName { get; set; }
        public long BusinessUnitId { get; set; }
        public bool? CanView { get; set; }
        public bool? CanCreate { get; set; }
        public bool? CanEdit { get; set; }
        public bool? CanDelete { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}