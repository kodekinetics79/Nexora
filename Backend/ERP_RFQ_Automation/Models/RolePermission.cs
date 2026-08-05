using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class RolePermission
{
    public long Id { get; set; }

    public long? RoleId { get; set; }

    public long ModuleId { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>
    /// RC-3/RC-4: read access, made explicit.
    ///
    /// Until this column existed, <c>RolePermissionRepository.CheckPermissionAsync</c> answered
    /// "CanView" with a bare <c>AnyAsync()</c> — the EXISTENCE of a row was the view grant. That
    /// made revocation impossible through the UI: unchecking every box wrote an all-false row,
    /// and an all-false row still granted read on the module. "Revoke All Access" therefore
    /// GRANTED read across every module it touched.
    ///
    /// Nullable to match the sibling flags, and backfilled to <c>true</c> by
    /// 20260805120000_IamCanViewAndAccessAuditTrail so the deploy reproduces the old row-exists
    /// semantics exactly for every pre-existing row. All checks compare <c>== true</c>.
    /// </summary>
    public bool? CanView { get; set; }

    public bool? CanCreate { get; set; }

    public bool? CanEdit { get; set; }

    public bool? CanDelete { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual Module Module { get; set; } = null!;

    public virtual SetupMaster? Role { get; set; }
}
