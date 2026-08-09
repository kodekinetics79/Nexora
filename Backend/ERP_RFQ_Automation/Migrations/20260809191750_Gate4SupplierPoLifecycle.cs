using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate4SupplierPoLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_orders_Status",
                table: "supplier_purchase_orders");

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedBy",
                table: "supplier_purchase_orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedOn",
                table: "supplier_purchase_orders",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgementNote",
                table: "supplier_purchase_orders",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgementStatus",
                table: "supplier_purchase_orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "supplier_purchase_orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ApprovedByUserId",
                table: "supplier_purchase_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedOn",
                table: "supplier_purchase_orders",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CommittedShipDate",
                table: "supplier_purchase_orders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Incoterm",
                table: "supplier_purchase_orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortOfDischarge",
                table: "supplier_purchase_orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortOfLoading",
                table: "supplier_purchase_orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevisedLeadTimeDays",
                table: "supplier_purchase_orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentToSupplierOn",
                table: "supplier_purchase_orders",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryOfOrigin",
                table: "supplier_purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HsCode",
                table: "supplier_purchase_order_lines",
                type: "text",
                nullable: true);

            // Backfilled to the same values SlaPolicy declares in code (48 working hours, 3 working
            // days), NOT to 0. Existing tenants all have an SlaPolicy row, and a zero-hour
            // acknowledgement window means every dispatched order is overdue the moment it is
            // sent — the first sweep after deployment would escalate the entire book.
            migrationBuilder.AddColumn<int>(
                name: "SupplierAckEscalationHours",
                table: "SlaPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 48);

            migrationBuilder.AddColumn<int>(
                name: "SupplierShipDateReminderDays",
                table: "SlaPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_orders_Acknowledgement",
                table: "supplier_purchase_orders",
                sql: "\"AcknowledgementStatus\" IS NULL OR \"AcknowledgementStatus\" IN ('ACCEPTED','REJECTED','COUNTERED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_orders_Approval",
                table: "supplier_purchase_orders",
                sql: "(\"ApprovedByUserId\" IS NULL AND \"ApprovedBy\" IS NULL AND \"ApprovedOn\" IS NULL) OR (\"ApprovedByUserId\" IS NOT NULL AND \"ApprovedBy\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_orders_RevisedLeadTime",
                table: "supplier_purchase_orders",
                sql: "\"RevisedLeadTimeDays\" IS NULL OR \"RevisedLeadTimeDays\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_orders_Status",
                table: "supplier_purchase_orders",
                sql: "\"Status\" IN ('DRAFT','APPROVED','SENT','ACKNOWLEDGED','IN_PRODUCTION','SHIPPED','ISSUED','PARTIALLY_RECEIVED','RECEIVED','CLOSED','CANCELLED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_orders_Acknowledgement",
                table: "supplier_purchase_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_orders_Approval",
                table: "supplier_purchase_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_orders_RevisedLeadTime",
                table: "supplier_purchase_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_orders_Status",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "AcknowledgedBy",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "AcknowledgedOn",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "AcknowledgementNote",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "AcknowledgementStatus",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "ApprovedOn",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "CommittedShipDate",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "Incoterm",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "PortOfDischarge",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "PortOfLoading",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "RevisedLeadTimeDays",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "SentToSupplierOn",
                table: "supplier_purchase_orders");

            migrationBuilder.DropColumn(
                name: "CountryOfOrigin",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "HsCode",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "SupplierAckEscalationHours",
                table: "SlaPolicies");

            migrationBuilder.DropColumn(
                name: "SupplierShipDateReminderDays",
                table: "SlaPolicies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_orders_Status",
                table: "supplier_purchase_orders",
                sql: "\"Status\" IN ('DRAFT','ISSUED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')");
        }
    }
}
