using System;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs
{
    public class SetupMasterUpdateRequestDTO
    {
        [Required]
        public string SetupType { get; set; } = null!;

        public string? SetupCode { get; set; }

        [Required(AllowEmptyStrings = true)]
        public string SetupName { get; set; } = null!;

        public string? Description { get; set; }

        public long? ParentSetupId { get; set; }

        /// <summary>
        /// Authority tier for a role row (see <c>RoleRanks</c>). Omitted → the stored rank is kept,
        /// so a client that does not know about ranks cannot silently demote a role to Member.
        /// </summary>
        public short? RoleRank { get; set; }

        public bool? IsActive { get; set; } = true;

        public string? ModifiedBy { get; set; }
    }
}