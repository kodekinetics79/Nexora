using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Audit linkage for the rescoped external-dependency ceiling: the ceiling now
    /// governs UNAUTHORIZED external usage only, and a reservation exempted from it
    /// because the tenant holds a live allow-list authorization records WHICH
    /// authorization exempted it ("ExternalAuthorizationId") and the deployment's
    /// declared inference posture at that moment ("InferencePosture"), directly on the
    /// AiRequests ledger row. Both columns are nullable and unset for local calls,
    /// externals under the ceiling, and denials. No FK — the ledger row must outlive the
    /// authorization row's lifecycle, like the other denormalised audit fields there.
    /// AiRequests is an existing tenant table: its RLS policy, EF query filter and role
    /// grants are table-level and unaffected by these added columns.
    /// </summary>
    public partial class RecordAiCeilingExemptionAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ExternalAuthorizationId",
                table: "AiRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InferencePosture",
                table: "AiRequests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalAuthorizationId",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "InferencePosture",
                table: "AiRequests");
        }
    }
}
