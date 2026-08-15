using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.MigrationsBaseline
{
    /// <inheritdoc />
    public partial class EmailInquiryAssembledLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AssembledLeadId",
                table: "EmailInquiryAssemblies",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryAssemblies_BusinessUnitId_AssembledLeadId",
                table: "EmailInquiryAssemblies",
                columns: new[] { "BusinessUnitId", "AssembledLeadId" });

            // THE RECOVERY SWEEP'S INDEX, and it has to be partial to be usable at all.
            //
            // The sweep's platform-wide enumeration constrains Status, AssembledLeadId and
            // UpdatedAtUtc but NOT BusinessUnitId, so neither of the tenant-leading indexes can
            // serve it — PostgreSQL 16 has no index skip-scan, leaving a full scan of a table
            // that grows with every email the platform has ever received, every two minutes,
            // forever. Leading with UpdatedAtUtc and excluding everything that is not stranded
            // makes the index approximately empty in steady state, which is exactly the shape
            // this query wants.
            migrationBuilder.Sql("""
                CREATE INDEX "IX_EmailInquiryAssemblies_Stranded"
                    ON public."EmailInquiryAssemblies" ("UpdatedAtUtc", "BusinessUnitId")
                    WHERE "Status" = 'ReadyForAssembly' AND "AssembledLeadId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS public."IX_EmailInquiryAssemblies_Stranded";
                """);

            migrationBuilder.DropIndex(
                name: "IX_EmailInquiryAssemblies_BusinessUnitId_AssembledLeadId",
                table: "EmailInquiryAssemblies");

            migrationBuilder.DropColumn(
                name: "AssembledLeadId",
                table: "EmailInquiryAssemblies");
        }
    }
}
