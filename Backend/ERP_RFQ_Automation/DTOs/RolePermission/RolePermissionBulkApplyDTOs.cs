using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.RolePermission
{
    /// <summary>
    /// B5: apply a whole permission matrix row/column/page in ONE transactional call.
    ///
    /// The Roles &amp; Permissions screen previously issued ~51 independent PUT/POSTs for a single
    /// "select all" click. Each one was separately authorised, so a denial partway through left the
    /// role holding a half-applied grant set that matched nothing the administrator intended — and
    /// the UI showed a success toast regardless.
    ///
    /// Note the absence of a BusinessUnitId: the tenant is read from the caller's claim and can
    /// never be supplied by the body.
    /// </summary>
    public class RolePermissionBulkApplyRequestDTO
    {
        [Required]
        public long? RoleId { get; set; }

        /// <summary>Free-text justification, persisted on every emitted audit event.</summary>
        [MaxLength(512)]
        public string? Reason { get; set; }

        [Required]
        [MinLength(1, ErrorMessage = "At least one entry is required.")]
        public List<RolePermissionBulkEntryDTO> Entries { get; set; } = new();
    }

    public class RolePermissionBulkEntryDTO
    {
        [Required]
        public long ModuleId { get; set; }

        public bool? CanView { get; set; }

        public bool? CanCreate { get; set; }

        public bool? CanEdit { get; set; }

        public bool? CanDelete { get; set; }
    }

    public class RolePermissionBulkApplyResponseDTO
    {
        /// <summary>Entries accepted (created + updated + unchanged).</summary>
        public int Applied { get; set; }

        public int Created { get; set; }

        public int Updated { get; set; }
    }
}
