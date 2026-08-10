using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate3CaseContinuityAndLeadOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CommercialCaseId",
                table: "supplier_purchase_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NexoraSerial",
                table: "supplier_purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CommercialCaseId",
                table: "Shipments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NexoraSerial",
                table: "Shipments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeNote",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OutcomeOn",
                table: "Leads",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutcomeReasonId",
                table: "Leads",
                type: "bigint",
                nullable: true);

            // Existing despatch documents get their master reference from the sales order they
            // were raised against — the same source the application now copies from at creation.
            // The tenant is matched as well as the key: a shipment must never be handed another
            // business unit's case, even where an id happens to collide.
            //
            // Orders that never came from a lead carry no case, and their shipments are left null
            // rather than given a manufactured one.
            migrationBuilder.Sql("""
                UPDATE public."Shipments" AS shipment
                   SET "CommercialCaseId" = "order"."CommercialCaseID",
                       "NexoraSerial" = "order"."NexoraSerial"
                  FROM public."Orders" AS "order"
                 WHERE "order"."ID" = shipment."OrderID"
                   AND "order"."BusinessUnitID" = shipment."BusinessUnitID"
                   AND ("order"."CommercialCaseID" IS NOT NULL OR "order"."NexoraSerial" IS NOT NULL);
                """);

            // Supplier purchase orders resolve through their RFQ, which is the only link every
            // historic row is guaranteed to have — the customer keys added by the preceding
            // migration are null on every pre-existing row, so they cannot resolve anything here.
            //
            // STOCK orders are excluded on principle rather than by accident: replenishment has no
            // case, so leaving those null is the correct outcome, not a gap.
            migrationBuilder.Sql("""
                UPDATE public.supplier_purchase_orders AS po
                   SET "CommercialCaseId" = rfq."CommercialCaseID",
                       "NexoraSerial" = rfq."NexoraSerial"
                  FROM public."RFQ" AS rfq
                 WHERE rfq."ID" = po."RfqId"
                   AND rfq."BusinessUnitID" = po."BusinessUnitId"
                   AND po."DemandSource" IS DISTINCT FROM 'STOCK'
                   AND (rfq."CommercialCaseID" IS NOT NULL OR rfq."NexoraSerial" IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommercialCaseId",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "NexoraSerial",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "CommercialCaseId",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "NexoraSerial",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "OutcomeNote",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OutcomeOn",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OutcomeReasonId",
                table: "Leads");
        }
    }
}
