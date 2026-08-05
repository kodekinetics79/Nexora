using System.Linq.Expressions;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Canonical <c>Setup_Master.SetupType</c> discrimination.
    ///
    /// RC-1: production stores roles as <c>'Role'</c> (title case), while
    /// <c>UserRepository.GetRolesAsync</c> and the users-grid <c>GroupJoin</c> compared against the
    /// lowercase literal <c>"role"</c>. PostgreSQL's <c>=</c> is case-SENSITIVE, so both queries
    /// matched nothing and <c>GET /api/User/Roles</c> returned HTTP 200 with an empty array — no
    /// error state anywhere, which is exactly why the User Management and Roles &amp; Permissions
    /// screens rendered blank instead of failing loudly.
    ///
    /// The comparison is also whitespace-tolerant: migration
    /// 20260723235500_CompleteLedgerKernelControls.cs:402 defensively strips spaces
    /// (<c>lower(replace(role."SetupType", ' ', '')) = 'role'</c>), which implies <c>' Role '</c>
    /// variants exist in live data. Match that form exactly so EF and SQL never disagree about
    /// which rows are roles.
    /// </summary>
    public static class SetupTypes
    {
        public const string Role = "role";

        /// <summary>Imperative check for an already-materialised value (controllers, guards).</summary>
        public static bool IsRole(string? setupType) =>
            setupType != null &&
            setupType.Replace(" ", string.Empty).Equals(Role, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The SAME rule as <see cref="IsRole"/>, expressed so EF Core can translate it to
        /// <c>lower(replace("SetupType", ' ', '')) = 'role'</c>. Compose it with
        /// <c>.Where(SetupTypes.IsRoleRow)</c>; never call <see cref="IsRole"/> inside an
        /// expression tree — it would either fail to translate or silently evaluate client-side.
        /// </summary>
        public static Expression<Func<SetupMaster, bool>> IsRoleRow =>
            sm => sm.SetupType != null && sm.SetupType.Replace(" ", "").ToLower() == Role;
    }
}
