using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Adds the immutable-by-name authority column <c>Setup_Master.RoleRank</c> and performs the
    /// ONE-TIME backfill that carries existing tenants across.
    ///
    /// Why the column exists: privilege used to be decided by SUBSTRING-MATCHING the free-text role
    /// name — <c>contains "super" AND contains "admin"</c> made a tenant super administrator (which
    /// bypassed every module permission check), and <c>contains "admin" OR contains "manager"</c>
    /// made a manager. In industrial and energy procurement those substrings are ordinary job
    /// titles: "Supervisor Admin", "Superintendent Administrator" and "Site Supervisor - Admin" all
    /// contain "super" and "admin", so every one of them silently owned the tenant. After this
    /// migration a role name is a label and carries no authority at all.
    /// </summary>
    public partial class AddSetupMasterRoleRank : Migration
    {
        /// <summary>
        /// The one-time backfill. It DELIBERATELY REPRODUCES THE LEGACY NAME HEURISTIC, EXACTLY,
        /// and it runs EXACTLY ONCE:
        ///
        ///   name contains "super" AND "admin"          → 30 (Owner / super admin)
        ///   else name contains "admin" OR "manager"    → 10 (Manager)
        ///   else                                       →  0 (Member)
        ///
        /// …matching on SetupCode OR SetupValue, case-insensitively, exactly as the deleted
        /// <c>RoleGate.IsSuperAdminName</c>/<c>IsManagerName</c> did. That is not an endorsement of
        /// the heuristic — it is the only rule under which no live tenant loses access at deploy
        /// time. Every role that is privileged today stays privileged tomorrow, and every role that
        /// is not, is not.
        ///
        /// It also means the vulnerable names are grandfathered ONCE, on purpose and visibly: a
        /// tenant that today has a role called "Supervisor Admin" really does have a super
        /// administrator, and silently demoting it during a deploy would lock a live customer out of
        /// their own tenant. It is now an explicit 30 in a column an administrator can see and
        /// lower, instead of a hidden consequence of a job title.
        ///
        /// FROM THIS POINT ON, RANK COMES ONLY FROM THIS COLUMN. No code reads a role name to
        /// decide authority; the two name-matching helpers were deleted rather than deprecated so
        /// that no future call site can reintroduce the class. Renaming a role changes nothing about
        /// what it can do. Changing the rank is a separate, audited act (ROLE_RANK_CHANGED) bounded
        /// by the caller's own rank.
        ///
        /// Written in portable SQL (lower/replace/coalesce/LIKE, unqualified table name) so the
        /// statement executed against production PostgreSQL is the SAME TEXT a test can execute
        /// against the SQLite test database — see
        /// <c>RoleRankBackfillTests.Backfill_ReproducesLegacyNameHeuristic_Once</c>. A hand-copied
        /// mirror of this rule in C# would be free to drift; this cannot.
        /// </summary>
        public const string LegacyRankBackfillSql = """
            UPDATE "Setup_Master"
               SET "RoleRank" = CASE
                    WHEN (lower(coalesce("SetupCode", '')) LIKE '%super%'
                          AND lower(coalesce("SetupCode", '')) LIKE '%admin%')
                      OR (lower(coalesce("SetupValue", '')) LIKE '%super%'
                          AND lower(coalesce("SetupValue", '')) LIKE '%admin%')
                    THEN 30
                    WHEN lower(coalesce("SetupCode", '')) LIKE '%admin%'
                      OR lower(coalesce("SetupCode", '')) LIKE '%manager%'
                      OR lower(coalesce("SetupValue", '')) LIKE '%admin%'
                      OR lower(coalesce("SetupValue", '')) LIKE '%manager%'
                    THEN 10
                    ELSE 0
               END
             WHERE lower(replace("SetupType", ' ', '')) = 'role';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT NULL with a store default of 0: any row written by a raw INSERT, an older
            // migration or code that predates the column lands as Member. Fail-closed — an omitted
            // value can never mean "owner".
            migrationBuilder.AddColumn<short>(
                name: "RoleRank",
                table: "Setup_Master",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.Sql(LegacyRankBackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the column discards every rank an administrator has set since the backfill.
            // That is unavoidable: the information does not exist anywhere else, which is the whole
            // point of the change.
            migrationBuilder.DropColumn(
                name: "RoleRank",
                table: "Setup_Master");
        }
    }
}
