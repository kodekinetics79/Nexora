using System;

namespace ERP_RFQ_Automation.DTOs
{
    public class SetupMasterResponseDTO
    {
        public long SetupId { get; set; }
        public string SetupType { get; set; } = null!;
        public string? SetupCode { get; set; }
        public string SetupName { get; set; } = null!;
        public string? Description { get; set; }
        public long? ParentSetupId { get; set; }
        /// <summary>Authority tier for role rows; always 0 for lookup rows.</summary>
        public short RoleRank { get; set; }

        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}