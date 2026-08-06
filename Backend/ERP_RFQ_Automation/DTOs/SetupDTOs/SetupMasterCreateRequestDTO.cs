using System;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs
{
    public class SetupMasterCreateRequestDTO
    {
        [Required]
        public string SetupType { get; set; } = null!;

        public string? SetupCode { get; set; }

        [Required(AllowEmptyStrings = true)]
        public string SetupName { get; set; } = null!;

        public string? Description { get; set; }

        public long? ParentSetupId { get; set; }

        /// <summary>
        /// Authority tier for a role row (see <c>RoleRanks</c>: 0 Member, 10 Manager, 20 Admin,
        /// 30 Owner). Omitted → Member. The server refuses any value at or above the CALLER'S own
        /// rank, and refuses any value at all on a non-role row.
        /// </summary>
        public short? RoleRank { get; set; }

        public bool? IsActive { get; set; } = true;

        [Required]
        public string CreatedBy { get; set; } = null!;
    }
}