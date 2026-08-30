using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Makes outbound despatch and proof-of-delivery retries tenant-scoped commands whose meaning
/// cannot change after the first successful write. Columns remain nullable for historical rows,
/// which pre-date a replay contract and therefore cannot honestly claim one.
/// </summary>
[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20260829223000_GovernShipmentAndDeliveryReplay")]
public partial class GovernShipmentAndDeliveryReplay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "IdempotencyKey",
            table: "Shipments",
            type: "character varying(160)",
            maxLength: 160,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RequestHash",
            table: "Shipments",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RequestHash",
            table: "delivery_proofs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "UX_Shipments_BU_IdempotencyKey",
            table: "Shipments",
            columns: new[] { "BusinessUnitID", "IdempotencyKey" },
            unique: true,
            filter: "\"IdempotencyKey\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "UX_Shipments_BU_IdempotencyKey", table: "Shipments");
        migrationBuilder.DropColumn(name: "IdempotencyKey", table: "Shipments");
        migrationBuilder.DropColumn(name: "RequestHash", table: "Shipments");
        migrationBuilder.DropColumn(name: "RequestHash", table: "delivery_proofs");
    }
}
