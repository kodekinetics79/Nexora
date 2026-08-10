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
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }
}