using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.UserGroup
{
    public class UserGroupUpdateRequestDTO
    {
        [Required]
        public string UserGroupsName { get; set; } = null!;

        [Required]
        public long BusinessUnitId { get; set; }

        public string? ModifiedBy { get; set; }
    }
}