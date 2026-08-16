using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Makes a governed email-triage command's Idempotency-Key single-use per tenant. The partial
/// predicate leaves every unrelated IAM audit action unchanged while giving concurrent reprocess
/// requests a database-enforced winner in addition to their transaction-scoped advisory lock.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260816120000_EmailTriageReprocessIdempotency")]
public sealed class EmailTriageReprocessIdempotency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "UX_IamAuditEvents_EmailTriageReprocess_Idempotency",
            table: "IamAuditEvents",
            columns: new[] { "BusinessUnitId", "Action", "CorrelationId" },
            unique: true,
            filter: "\"Action\" = 'EmailTriageReprocessed' AND \"CorrelationId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_IamAuditEvents_EmailTriageReprocess_Idempotency",
            table: "IamAuditEvents");
    }
}
