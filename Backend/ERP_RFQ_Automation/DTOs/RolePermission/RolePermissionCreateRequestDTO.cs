using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.DTOs.RolePermission
{
    public class RolePermissionCreateRequestDTO
    {
        [Required]
        public long? RoleId { get; set; }

        [Required]
        public long ModuleId { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        /// <summary>Explicit read grant (B3). Null means "unspecified" and the store default
        /// (true) applies, which reproduces the old row-exists semantics.</summary>
        public bool? CanView { get; set; }

        public bool? CanCreate { get; set; }

        public bool? CanEdit { get; set; }

        public bool? CanDelete { get; set; }

        /// <summary>
        /// RC-7: retained for wire compatibility ONLY. The server derives the actor from the
        /// validated token (see Authorization/ActorContext.cs) and NEVER reads this value —
        /// accepting it let a caller name anyone as the author of their own privilege change.
        /// </summary>
        [JsonIgnore]
        public string? CreatedBy { get; set; }
    }
}