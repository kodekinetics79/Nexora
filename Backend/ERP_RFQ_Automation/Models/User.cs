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
