using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.UserDTO
{
    /// <summary>
    /// B2 / RC-5: the caller's OWN effective grants.
    ///
    /// The login bootstrap used to read <c>GET /api/RolePermission</c>, which is gated by
    /// <c>[RequireModulePermission("Roles &amp; Permissions", View)]</c>. Any role without RBAC
    /// administration rights — i.e. every role a customer creates — got a 403, the frontend
    /// swallowed it in a <c>console.error</c>, and the session proceeded with an EMPTY permission
    /// set: no sidebar, Access Denied on every screen. Super admins only escaped because
    /// PermissionHandler short-circuits for them.
    ///
    /// Reading your own grants is not a privileged operation, so this endpoint requires
    /// authentication and nothing else. It never widens what <c>GET /api/RolePermission</c>
    /// exposes: it returns exactly one role's rows — the caller's, from the claim.
    /// </summary>
    public class MyPermissionsResponseDTO
    {
        public long? UserId { get; set; }

        public long? RoleId { get; set; }

        public string? RoleName { get; set; }

        public long BusinessUnitId { get; set; }

        /// <summary>Computed by <c>IRoleGate</c>, the same authority
        /// <c>PermissionHandler</c> consults — never re-derived locally (closes RC-8).</summary>
        public bool IsSuperAdmin { get; set; }

        public bool IsManager { get; set; }

        /// <summary>
        /// The caller's role rank satisfies every tenant module permission requirement directly.
        /// This mirrors <c>PermissionHandler</c>'s explicit <c>rank &gt;= Admin</c> rule and is
        /// deliberately separate from <see cref="IsSuperAdmin"/>, which remains Owner-only.
        /// </summary>
        public bool HasModuleAuthorityByRank { get; set; }

        public List<MyModulePermissionDTO> Permissions { get; set; } = new();

        /// <summary>
        /// The runtime-available entitlement keys the caller's TENANT is granted (e.g.
        /// <c>capability.full-navigation</c>), in catalogue order. Tenant scope, not user
        /// privilege: it rides the same bootstrap as permissions but answers a different question
        /// — what surface this customer bought, not what this user may do on it.
        ///
        /// <para>Empty means no optional surface, and clients must treat it that way: an
        /// ungoverned business unit, an unreadable platform plane and an older server that never
        /// wrote this field all look identical to the client, and all of them must land on the
        /// minimal surface rather than an error.</para>
        /// </summary>
        public List<string> Entitlements { get; set; } = new();
    }

    public class MyModulePermissionDTO
    {
        public long ModuleId { get; set; }

        public string? ModuleName { get; set; }

        public bool CanView { get; set; }

        public bool CanCreate { get; set; }

        public bool CanEdit { get; set; }

        public bool CanDelete { get; set; }
    }
}
