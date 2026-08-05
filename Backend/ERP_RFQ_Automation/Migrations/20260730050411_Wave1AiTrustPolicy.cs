using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Wave1AiTrustPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedDataClassifications",
                table: "AiProcessingPolicies",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "Public,Internal");

            migrationBuilder.AddColumn<string>(
                name: "DataResidency",
                table: "AiProcessingPolicies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "TenantApprovedRegion");

            migrationBuilder.AddColumn<string>(
                name: "EgressPolicy",
                table: "AiProcessingPolicies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "RedactedFieldsOnly");

            migrationBuilder.AddColumn<decimal>(
                name: "ExternalDependencyCeilingPercent",
                table: "AiProcessingPolicies",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 10m);

            migrationBuilder.AddColumn<bool>(
                name: "InputOutputAuditAllowed",
                table: "AiProcessingPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "LocalComputeCostPerHour",
                table: "AiProcessingPolicies",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalCostCurrency",
                table: "AiProcessingPolicies",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrCostPerPage",
                table: "AiProcessingPolicies",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrivacyReviewRequired",
                table: "AiProcessingPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RedactionRequired",
                table: "AiProcessingPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "AiProcessingPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.Sql("""
                ALTER TABLE public."AiProcessingPolicies"
                    ADD CONSTRAINT "CK_AiProcessingPolicies_TrustControls"
                    CHECK ("ExternalDependencyCeilingPercent" BETWEEN 0 AND 10
                       AND "RetentionDays" BETWEEN 1 AND 3650
                       AND length(trim("AllowedDataClassifications")) > 0
                       AND length(trim("EgressPolicy")) > 0
                       AND length(trim("DataResidency")) > 0
                       AND (NOT "ExternalProcessingAllowed"
                            OR ("RedactionRequired" AND "PrivacyReviewRequired"
                                AND "AllowedProvider" IS NOT NULL AND "AllowedModel" IS NOT NULL))),
                    ADD CONSTRAINT "CK_AiProcessingPolicies_LocalRates"
                    CHECK (("LocalComputeCostPerHour" IS NULL AND "OcrCostPerPage" IS NULL
                            AND "LocalCostCurrency" IS NULL)
                       OR ("LocalComputeCostPerHour" >= 0 AND "OcrCostPerPage" >= 0
                           AND "LocalCostCurrency" ~ '^[A-Z]{3}$'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public."AiProcessingPolicies"
                    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_TrustControls",
                    DROP CONSTRAINT IF EXISTS "CK_AiProcessingPolicies_LocalRates";
                """);

            migrationBuilder.DropColumn(
                name: "AllowedDataClassifications",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "DataResidency",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "EgressPolicy",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "ExternalDependencyCeilingPercent",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "InputOutputAuditAllowed",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "LocalComputeCostPerHour",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "LocalCostCurrency",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "OcrCostPerPage",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "PrivacyReviewRequired",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "RedactionRequired",
                table: "AiProcessingPolicies");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                table: "AiProcessingPolicies");
        }
    }
}
