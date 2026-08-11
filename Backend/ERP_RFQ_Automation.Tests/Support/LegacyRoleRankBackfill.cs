namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// The ONE-TIME backfill that carried existing tenants off substring-matched privilege and onto the
/// explicit <c>Setup_Master.RoleRank</c> column.
///
/// PROVENANCE. This text was <c>AddSetupMasterRoleRank.LegacyRankBackfillSql</c>, a const on the
/// migration 20260806193620_AddSetupMasterRoleRank, and the migration executed it. It moved here
/// when 20260811033109_SquashedSchemaBaseline squashed the 134 pre-baseline migrations: that
/// migration is gone, so there is no longer a production copy of this statement for a test copy to
/// drift from. Keeping the const on a dead migration class purely so a test could reference it was
/// the only reason one file out of 134 stayed compiled, and it made the migration folder
/// undeletable.
///
/// WHY IT IS STILL WORTH KEEPING. The statement no longer runs anywhere and never will again — a
/// new database is created with the column already present and defaulted to Member, and every
/// existing database ran this once, before the baseline was stamped over it. What it still is, is
/// the ONLY written record of how every rank now sitting in a production Setup_Master row was
/// decided. Deleting it would leave "why is 'Supervisor Admin' an Owner in this tenant?" with no
/// answer anywhere in the repository. RoleRankAuthorityTests executes it against a relational
/// database and asserts the tier each representative legacy name lands on, which keeps that record
/// honest rather than merely present.
///
/// WHAT IT DID, exactly, and once:
///   name contains "super" AND "admin"          → 30 (Owner / super admin)
///   else name contains "admin" OR "manager"    → 10 (Manager)
///   else                                       →  0 (Member)
///
/// …matching on SetupCode OR SetupValue, case-insensitively, exactly as the deleted
/// <c>RoleGate.IsSuperAdminName</c>/<c>IsManagerName</c> did. That was not an endorsement of the
/// heuristic — it was the only rule under which no live tenant lost access at deploy time. The
/// vulnerable names were grandfathered ONCE, visibly: a tenant that had a role called "Supervisor
/// Admin" really did have a super administrator, and silently demoting it during a deploy would
/// have locked a live customer out of their own tenant. It became an explicit 30 in a column an
/// administrator can see and lower, instead of a hidden consequence of a job title.
///
/// FROM THAT POINT ON, RANK COMES ONLY FROM THE COLUMN. No code reads a role name to decide
/// authority; the two name-matching helpers were deleted rather than deprecated so that no future
/// call site can reintroduce the class of defect. Renaming a role changes nothing about what it can
/// do. Changing the rank is a separate, audited act (ROLE_RANK_CHANGED) bounded by the caller's own
/// rank.
///
/// Written in portable SQL (lower/replace/coalesce/LIKE, unqualified table name) so the statement
/// that ran against production PostgreSQL is the SAME TEXT the test executes against SQLite.
/// </summary>
public static class LegacyRoleRankBackfill
{
    public const string Sql = """
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
}
