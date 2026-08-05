using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V2Gate02OpportunityCommercialComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComponentsJson",
                table: "commercial_opportunity_recommendations",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"signals\":[],\"expectedCommercialValue\":null,\"currency\":null,\"status\":\"legacy_reconcile_required\",\"responseDeadline\":null,\"currentBlocker\":\"Reconcile to generate commercial components.\"}'::jsonb");

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedCommercialValue",
                table: "commercial_opportunity_recommendations",
                type: "numeric(20,4)",
                precision: 20,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedCommercialValueCurrency",
                table: "commercial_opportunity_recommendations",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE public.commercial_opportunity_recommendations
                    ALTER COLUMN "ComponentsJson" DROP DEFAULT;

                ALTER TABLE public.commercial_opportunity_recommendations
                    ADD CONSTRAINT "CK_opportunity_recommendations_ComponentsObject"
                        CHECK (COALESCE(
                            jsonb_typeof("ComponentsJson") = 'object'
                            AND "ComponentsJson" ? 'signals'
                            AND "ComponentsJson" ? 'status'
                            AND "ComponentsJson" ? 'currentBlocker'
                            AND jsonb_typeof("ComponentsJson" -> 'signals') = 'array'
                            AND jsonb_typeof("ComponentsJson" -> 'status') = 'string'
                            AND jsonb_typeof("ComponentsJson" -> 'currentBlocker') = 'string',
                            FALSE)) NOT VALID,
                    ADD CONSTRAINT "CK_opportunity_recommendations_EcvCurrency"
                        CHECK (COALESCE(
                            "ComponentsJson" ? 'expectedCommercialValue'
                            AND "ComponentsJson" ? 'currency'
                            AND (("ExpectedCommercialValue" IS NULL) = ("ExpectedCommercialValueCurrency" IS NULL))
                            AND ("ExpectedCommercialValueCurrency" IS NULL OR "ExpectedCommercialValueCurrency" ~ '^[A-Z]{3}$')
                            AND CASE
                                WHEN "ExpectedCommercialValue" IS NULL THEN
                                    "ComponentsJson" -> 'expectedCommercialValue' = 'null'::jsonb
                                    AND "ComponentsJson" -> 'currency' = 'null'::jsonb
                                ELSE
                                    jsonb_typeof("ComponentsJson" -> 'expectedCommercialValue') = 'number'
                                    AND ("ComponentsJson" ->> 'expectedCommercialValue')::numeric = "ExpectedCommercialValue"
                                    AND "ComponentsJson" ->> 'currency' = "ExpectedCommercialValueCurrency"
                            END,
                            FALSE)) NOT VALID,
                    ADD CONSTRAINT "CK_opportunity_recommendations_EcvNonNegative"
                        CHECK ("ExpectedCommercialValue" IS NULL OR "ExpectedCommercialValue" >= 0) NOT VALID;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_opportunity_recommendations_ComponentsObject",
                table: "commercial_opportunity_recommendations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_opportunity_recommendations_EcvCurrency",
                table: "commercial_opportunity_recommendations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_opportunity_recommendations_EcvNonNegative",
                table: "commercial_opportunity_recommendations");

            migrationBuilder.DropColumn(
                name: "ComponentsJson",
                table: "commercial_opportunity_recommendations");

            migrationBuilder.DropColumn(
                name: "ExpectedCommercialValue",
                table: "commercial_opportunity_recommendations");

            migrationBuilder.DropColumn(
                name: "ExpectedCommercialValueCurrency",
                table: "commercial_opportunity_recommendations");
        }
    }
}
