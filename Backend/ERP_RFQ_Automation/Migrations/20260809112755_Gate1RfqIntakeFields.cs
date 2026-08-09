using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gate1RfqIntakeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgreementReference",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BidClosingDateHijri",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryLocation",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequiredDeliveryDate",
                table: "Leads",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSha256",
                table: "Attachments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreementReference",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "BidClosingDateHijri",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "DeliveryLocation",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "RequiredDeliveryDate",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ContentSha256",
                table: "Attachments");
        }
    }
}
