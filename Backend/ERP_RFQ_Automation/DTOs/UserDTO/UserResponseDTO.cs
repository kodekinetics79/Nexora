using System;

namespace ERP_RFQ_Automation.DTOs.UserDTO
{
    public class UserResponseDTO
    {
        public long Id { get; set; }
        public string FirstName { get; set; } = null!;
        public string? MiddleName { get; set; }
        public string LastName { get; set; } = null!;
        public string Email { get; set; } = null!;
        public string? ImageUrl { get; set; }
        public long? RoleId { get; set; }
        public string? RoleName { get; set; }
        public long? TeamId { get; set; }
        public string? TeamName { get; set; }
        public string? Timezone { get; set; }
        public DateTime? LastLogin { get; set; }
        public string? Region { get; set; }
        public long? ManagerId { get; set; }
        public long? Buid { get; set; }
        public string? BusinessUnitName { get; set; }
        public long? UserGroupId { get; set; }
        public string? UserGroupName { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}