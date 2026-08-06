namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// The authority tier of a <c>Setup_Master</c> row whose <c>SetupType</c> is a role, stored in
    /// the <c>RoleRank</c> column.
    ///
    /// This column REPLACES the substring-on-free-text rule that used to decide privilege
    /// (<c>name contains "super" AND "admin"</c> → super administrator;
    /// <c>name contains "admin" OR "manager"</c> → manager). That rule read a human label as if it
    /// were an access-control decision, so ordinary industrial/energy job titles resolved as
    /// tenant owners: "Supervisor Admin", "Superintendent Administrator" and
    /// "Site Supervisor - Admin" all contain "super" and "admin" and therefore held the entire
    /// tenant. Rank is now an explicit, numeric, server-controlled column; the name is a label
    /// again and carries no authority whatsoever.
    ///
    /// Gaps of 10 are deliberate: a tier can be inserted between two existing ones without
    /// rewriting stored data.
    /// </summary>
    public static class RoleRanks
    {
        /// <summary>No inherent authority. Everything comes from explicit RolePermissions rows.</summary>
        public const short Member = 0;

        /// <summary>Team/queue-level authority: tenant-wide reads, lead assignment, the
        /// <c>[RequireManagerRole]</c> surface.</summary>
        public const short Manager = 10;

        /// <summary>Administers the tenant. Satisfies module permission checks by rank, so a
        /// missing RolePermissions row cannot lock an administrator out of their own tenant.</summary>
        public const short Admin = 20;

        /// <summary>The tenant owner / super administrator: the top of the tenant plane.</summary>
        public const short Owner = 30;

        /// <summary>Alias for <see cref="Owner"/> — "super admin" is the name used in the UI and in
        /// <c>IRoleGate.IsSuperAdminAsync</c>.</summary>
        public const short SuperAdmin = Owner;

        /// <summary>The tiers that may be stored, lowest first. Anything else is rejected at the
        /// API boundary rather than silently rounded to a neighbouring tier.</summary>
        public static readonly short[] All = { Member, Manager, Admin, Owner };

        public static bool IsDefined(short rank) =>
            rank == Member || rank == Manager || rank == Admin || rank == Owner;

        /// <summary>Human label for audit entries and error messages.</summary>
        public static string Describe(short rank) => rank switch
        {
            Owner => "Owner",
            Admin => "Admin",
            Manager => "Manager",
            Member => "Member",
            _ => rank.ToString()
        };
    }
}
