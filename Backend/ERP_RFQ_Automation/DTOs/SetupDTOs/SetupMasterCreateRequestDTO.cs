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
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }
}