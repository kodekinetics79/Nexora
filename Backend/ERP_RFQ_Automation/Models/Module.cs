using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Module
{
    public long Id { get; set; }

    public string ModuleName { get; set; } = null!;

    public string? Description { get; set; }

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
