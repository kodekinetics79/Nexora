using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01OrderLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CommercialCaseID",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContactID",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NexoraSerial",
                table: "Orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("""
                DO $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "Orders" orders
                        WHERE orders."QuoteID" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM "Quotes" quote
                            WHERE quote."ID" = orders."QuoteID"
                              AND quote."BusinessUnitID" = orders."BusinessUnitID")) THEN
                        RAISE EXCEPTION 'legacy Order has a missing or cross-tenant Quote; reconcile before migration'
                            USING ERRCODE = '23503';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "Orders" orders JOIN "Quotes" quote
                          ON quote."ID" = orders."QuoteID" AND quote."BusinessUnitID" = orders."BusinessUnitID"
                        WHERE orders."CustomerID" IS DISTINCT FROM quote."CustomerID") THEN
                        RAISE EXCEPTION 'legacy Order customer conflicts with Quote customer; reconcile before migration'
                            USING ERRCODE = '23514';
                    END IF;
                END; $function$;

                UPDATE "Orders" orders
                   SET "CommercialCaseID" = quote."CommercialCaseID",
                       "NexoraSerial" = quote."NexoraSerial",
                       "ContactID" = quote."ContactID"
                  FROM "Quotes" quote
                 WHERE orders."QuoteID" = quote."ID"
                   AND orders."BusinessUnitID" = quote."BusinessUnitID";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessUnitID_CommercialCaseID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "CommercialCaseID" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessUnitID_NexoraSerial",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "NexoraSerial" });

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_CommercialCases_BusinessUnitID_CommercialCaseID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "CommercialCaseID" },
                principalTable: "CommercialCases",
                principalColumns: new[] { "BusinessUnitID", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_validate_order_commercial_identity()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD."CommercialCaseID" IS NOT NULL
                       AND (NEW."CommercialCaseID", NEW."NexoraSerial") IS DISTINCT FROM
                           (OLD."CommercialCaseID", OLD."NexoraSerial") THEN
                        RAISE EXCEPTION 'Order Nexora Serial lineage is immutable once assigned' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL
                       AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
                        RAISE EXCEPTION 'Order customer identity is immutable once assigned' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL
                       AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
                        RAISE EXCEPTION 'Order contact identity is immutable once assigned' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."QuoteID" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM "Quotes" quote
                        WHERE quote."ID" = NEW."QuoteID"
                          AND quote."BusinessUnitID" = NEW."BusinessUnitID"
                          AND (quote."CommercialCaseID", quote."NexoraSerial", quote."CustomerID", quote."ContactID")
                              IS NOT DISTINCT FROM
                              (NEW."CommercialCaseID", NEW."NexoraSerial", NEW."CustomerID", NEW."ContactID")) THEN
                        RAISE EXCEPTION 'Order commercial identity must match its Quote' USING ERRCODE = '23503';
                    END IF;
                    RETURN NEW;
                END; $function$;
                CREATE TRIGGER "TR_Orders_CommercialIdentity"
                    BEFORE INSERT OR UPDATE OF "CommercialCaseID", "NexoraSerial", "CustomerID", "ContactID", "QuoteID" ON "Orders"
                    FOR EACH ROW EXECUTE FUNCTION nexora_validate_order_commercial_identity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_Orders_CommercialIdentity" ON "Orders";
                DROP FUNCTION IF EXISTS nexora_validate_order_commercial_identity();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_CommercialCases_BusinessUnitID_CommercialCaseID",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BusinessUnitID_CommercialCaseID",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BusinessUnitID_NexoraSerial",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CommercialCaseID",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ContactID",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "NexoraSerial",
                table: "Orders");
        }
    }
}
