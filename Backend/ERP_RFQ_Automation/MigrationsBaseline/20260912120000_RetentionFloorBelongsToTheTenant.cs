using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Lowers the retention floor from 30 days to 1 day.
    ///
    /// <para><b>Why.</b> The 30-day floor was Nexora's opinion about how long a customer should
    /// keep its own uploaded files, and the owner's rule is that Nexora does not impose a data
    /// policy on a tenant: the tenant's administrator decides what to keep. What remains fixed
    /// is not a number but a set — statutory documents, legal holds, anything still in use — and
    /// that set is enforced by <c>EvidenceRetentionEligibility</c> in SQL regardless of the
    /// policy. One day survives as a settle guard for asynchronous extraction, mirroring the
    /// 24-hour settle window the clear-out path already uses.</para>
    ///
    /// <para><b>Down.</b> A policy set below 30 days would violate the old constraint, so Down
    /// clamps such rows back to 30 before restoring it. That is a loss of the tenant's setting,
    /// stated here rather than left to fail at deploy time.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260912120000_RetentionFloorBelongsToTheTenant")]
    public partial class RetentionFloorBelongsToTheTenant : Migration
    {
        private const string Table = "evidence_retention_policies";
        private const string Constraint = "CK_evidence_retention_policies_retention_days";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: Constraint, table: Table);
            migrationBuilder.AddCheckConstraint(
                name: Constraint,
                table: Table,
                sql: "\"RetentionDays\" >= 1 AND \"RetentionDays\" <= 3650");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE evidence_retention_policies SET \"RetentionDays\" = 30 WHERE \"RetentionDays\" < 30;");
            migrationBuilder.DropCheckConstraint(name: Constraint, table: Table);
            migrationBuilder.AddCheckConstraint(
                name: Constraint,
                table: Table,
                sql: "\"RetentionDays\" >= 30 AND \"RetentionDays\" <= 3650");
        }
    }
}
