using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// The file behind a quote the rep made outside Nexora and uploaded (owner request 2026-09-28).
///
/// <para>Five nullable columns on <c>Quotes</c>, so every existing quote is untouched and a quote
/// Nexora produced keeps them null. They live on the quote rather than in <c>Attachments</c>,
/// whose row-level security admits only lead attachments: a tenant could not have written or
/// read a quote's file there. <c>Quotes</c> is already isolated per tenant.</para>
///
/// <para>Hand-written (no Designer), like the other repair migrations in this folder. The model
/// snapshot carries the same five properties.</para>
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260929050000_QuoteUploadedFile")]
public sealed class QuoteUploadedFile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UploadedFileName", table: "Quotes",
            type: "character varying(255)", maxLength: 255, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "UploadedFileStorageUri", table: "Quotes",
            type: "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "UploadedFileSha256", table: "Quotes",
            type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "UploadedFileContentType", table: "Quotes",
            type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "UploadedFileSize", table: "Quotes",
            type: "bigint", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "UploadedFileName", table: "Quotes");
        migrationBuilder.DropColumn(name: "UploadedFileStorageUri", table: "Quotes");
        migrationBuilder.DropColumn(name: "UploadedFileSha256", table: "Quotes");
        migrationBuilder.DropColumn(name: "UploadedFileContentType", table: "Quotes");
        migrationBuilder.DropColumn(name: "UploadedFileSize", table: "Quotes");
    }
}
