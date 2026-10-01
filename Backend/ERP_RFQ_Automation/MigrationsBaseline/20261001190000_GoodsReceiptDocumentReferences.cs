using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20261001190000_GoodsReceiptDocumentReferences")]
public sealed class GoodsReceiptDocumentReferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SupplierInvoiceNumber", table: "goods_receipts",
            type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "BillOfLadingNumber", table: "goods_receipts",
            type: "character varying(100)", maxLength: 100, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SupplierInvoiceNumber", table: "goods_receipts");
        migrationBuilder.DropColumn(name: "BillOfLadingNumber", table: "goods_receipts");
    }
}
