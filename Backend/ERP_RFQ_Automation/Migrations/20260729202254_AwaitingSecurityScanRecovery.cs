using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AwaitingSecurityScanRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE source_document_occurrences
                    DROP CONSTRAINT IF EXISTS ck_source_document_occurrences_intake_status;

                ALTER TABLE source_document_occurrences
                    ADD CONSTRAINT ck_source_document_occurrences_intake_status
                    CHECK (intake_status IN (
                        'Accepted','AwaitingSecurityScan','Queued','Processing','Retryable',
                        'Resolved','ReviewRequired','Rejected','DeadLetter'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE source_document_occurrences
                SET intake_status = 'Rejected',
                    last_error_category = COALESCE(last_error_category, 'SecurityInspection'),
                    last_error_code = COALESCE(last_error_code, 'security_scanner_unavailable'),
                    updated_on = CURRENT_TIMESTAMP
                WHERE intake_status = 'AwaitingSecurityScan';

                ALTER TABLE source_document_occurrences
                    DROP CONSTRAINT IF EXISTS ck_source_document_occurrences_intake_status;

                ALTER TABLE source_document_occurrences
                    ADD CONSTRAINT ck_source_document_occurrences_intake_status
                    CHECK (intake_status IN (
                        'Accepted','Queued','Processing','Retryable','Resolved',
                        'ReviewRequired','Rejected','DeadLetter'));
                """);
        }
    }
}
