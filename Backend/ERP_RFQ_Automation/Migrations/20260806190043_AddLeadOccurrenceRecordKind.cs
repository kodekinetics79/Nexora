using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Distinguishes an occurrence that records a DOCUMENT ARRIVING from one that records a
    /// canonical identity baseline.
    ///
    /// <para><b>Why.</b> A LeadRevision cannot exist without an occurrence —
    /// <c>EstablishedByOccurrenceId</c> is NOT NULL. So giving a revision-less lead its identity
    /// must mint an occurrence describing no received document. Three readers count occurrences
    /// as real inbound documents: ingestion volume and leads-received
    /// (<c>GetAnalyticsAsync</c>), the touchless-decision KPI on the auditor-facing screen
    /// (<c>QualityAnalyticsService</c>), and the extraction-accuracy ground truth
    /// (<c>CaptureExtractionCorpusAsync</c>). Without this column a baseline silently inflates
    /// all three.</para>
    ///
    /// <para><b>The backfill corrects a pre-existing inaccuracy.</b> Release-01A synthesised 23
    /// occurrences for leads that predated the identity pipeline. Those rows carry a fabricated
    /// <c>md5()||md5()</c> value in a column that is supposed to hold a SHA-256 content
    /// fingerprint, a back-dated ingestion time, and <c>ProcessingPath = HumanReview</c> for a
    /// review no human performed. They are baselines, not ingestions, and are reclassified here.
    /// Governance figures for historical windows will move — they become correct.</para>
    ///
    /// <para>Additive and fully reversible. No data is destroyed.</para>
    /// </summary>
    public partial class AddLeadOccurrenceRecordKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing row is an ingestion — correct for all but the 23 reclassified below.
            migrationBuilder.AddColumn<string>(
                name: "RecordKind",
                table: "LeadIngestionOccurrences",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Ingestion");

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_RecordKind",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "RecordKind" });

            // The tenant-isolation policy on this table is FORCE'd, which subjects the table
            // OWNER to it too. A migration-role UPDATE would therefore match zero rows and
            // silently do nothing. Lift FORCE for the backfill and restore it immediately.
            migrationBuilder.Sql("""
                ALTER TABLE "LeadIngestionOccurrences" NO FORCE ROW LEVEL SECURITY;

                UPDATE "LeadIngestionOccurrences"
                   SET "RecordKind" = 'IdentityBaseline'
                 WHERE "PolicyVersion" = 'release-01a/legacy-backfill';

                ALTER TABLE "LeadIngestionOccurrences" FORCE ROW LEVEL SECURITY;
                """);

            // RecordKind joins the provenance that cannot be edited after the fact. Without this
            // the discriminator every analytics reader now trusts would be freely mutable.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_release01b_lead_occurrence_source_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."SourceDocumentOccurrenceId" IS DISTINCT FROM OLD."SourceDocumentOccurrenceId"
                       OR NEW."LogicalGroupKey" IS DISTINCT FROM OLD."LogicalGroupKey"
                       OR NEW."RecordKind" IS DISTINCT FROM OLD."RecordKind" THEN
                        RAISE EXCEPTION 'Lead occurrence source linkage is immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the guard to its pre-change body verbatim before dropping the column it
            // references, or the function would reference a column that no longer exists.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_release01b_lead_occurrence_source_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."SourceDocumentOccurrenceId" IS DISTINCT FROM OLD."SourceDocumentOccurrenceId"
                       OR NEW."LogicalGroupKey" IS DISTINCT FROM OLD."LogicalGroupKey" THEN
                        RAISE EXCEPTION 'Lead occurrence source linkage is immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_RecordKind",
                table: "LeadIngestionOccurrences");

            migrationBuilder.DropColumn(
                name: "RecordKind",
                table: "LeadIngestionOccurrences");
        }
    }
}
