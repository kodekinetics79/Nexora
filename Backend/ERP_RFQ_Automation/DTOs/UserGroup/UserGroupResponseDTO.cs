using System;

namespace ERP_RFQ_Automation.DTOs.UserGroup
{
    public class UserGroupResponseDTO
    {
        public long Id { get; set; }
        public string UserGroupsName { get; set; } = null!;
        public long BusinessUnitId { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}