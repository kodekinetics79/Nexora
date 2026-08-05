using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class User
{
    public long Id { get; set; }

    public string FirstName { get; set; } = null!;

    public string? MiddleName { get; set; }

    public string LastName { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string ImageUrl { get; set; } = null!;

    public long? RoleId { get; set; }

    public long? TeamId { get; set; }

    public string? Timezone { get; set; }

    public DateTime? LastLogin { get; set; }

    public string? Region { get; set; }

    public long? ManagerId { get; set; }

    public long? Buid { get; set; }

    public long? UserGroupId { get; set; }

    public bool? IsActive { get; set; }

    /// <summary>
    /// UTC instant the user was last deactivated (IsActive flipped true→false); cleared on
    /// reactivation. Enables a reproducible seats meter: a user occupied a seat in a billing
    /// period when CreatedOn &lt; PeriodEnd AND (IsActive OR DeactivatedAtUtc &gt;= PeriodEnd).
    /// Legacy inactive rows with a null DeactivatedAtUtc count as deactivated.
    /// </summary>
    public DateTime? DeactivatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit? Bu { get; set; }

    public virtual ICollection<User> InverseManager { get; set; } = new List<User>();

    public virtual ICollection<Lead> Leads { get; set; } = new List<Lead>();

    public virtual User? Manager { get; set; }

    public virtual ICollection<ProductAttachment> ProductAttachments { get; set; } = new List<ProductAttachment>();

    public virtual SetupMaster? Role { get; set; }

    public virtual Team? Team { get; set; }

    public virtual ICollection<Team> Teams { get; set; } = new List<Team>();

    public virtual UserGroup? UserGroup { get; set; }
}
