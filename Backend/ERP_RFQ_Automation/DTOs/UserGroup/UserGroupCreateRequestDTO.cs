using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.UserGroup
{
    public class UserGroupCreateRequestDTO
    {
        [Required]
        public string UserGroupsName { get; set; } = null!;

        [Required]
        public long BusinessUnitId { get; set; }

        public string? CreatedBy { get; set; }
    }
}