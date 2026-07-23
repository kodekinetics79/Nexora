using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CompleteBankReconciliationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceReference",
                table: "ReconciliationMatches",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchReason",
                table: "ReconciliationMatches",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RawPayload",
                table: "BankStatementImports",
                type: "bytea",
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."BankStatementImports" ADD CONSTRAINT "CK_BankStatementImports_RawEvidence"
                    CHECK ("RawPayload" IS NULL OR (octet_length("RawPayload") BETWEEN 1 AND 10485760));
                ALTER TABLE public."ReconciliationMatches" ADD CONSTRAINT "CK_ReconciliationMatches_ManualEvidence" CHECK (
                    ("MatchType" = 'Manual' AND length(trim("MatchReason")) >= 20
                        AND length(trim("EvidenceReference")) >= 8 AND "RuleCode" = 'MANUAL_REVIEWED_V1'
                        AND "RuleVersion" = 1 AND "Confidence" = 1)
                    OR ("MatchType" = 'DeterministicExact' AND "MatchReason" IS NULL
                        AND "EvidenceReference" IS NULL AND "RuleCode" = 'EXACT_AMOUNT_DIRECTION_V1'
                        AND "RuleVersion" = 1 AND "Confidence" = 1));

                CREATE OR REPLACE FUNCTION public.nexora_bank_guard_import()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF NEW."Status" <> 'Validated' OR NEW."RawPayload" IS NULL
                       OR octet_length(NEW."RawPayload") NOT BETWEEN 1 AND 10485760
                       OR encode(digest(NEW."RawPayload", 'sha256'), 'hex') <> NEW."SourceHash" THEN
                        RAISE EXCEPTION 'validated imports require retained source bytes matching the source digest' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER trg_bankimports_validate BEFORE INSERT ON public."BankStatementImports"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_import();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.nexora_bank_guard_import() CASCADE;
                ALTER TABLE public."ReconciliationMatches" DROP CONSTRAINT IF EXISTS "CK_ReconciliationMatches_ManualEvidence";
                ALTER TABLE public."BankStatementImports" DROP CONSTRAINT IF EXISTS "CK_BankStatementImports_RawEvidence";
                """);
            migrationBuilder.DropColumn(
                name: "EvidenceReference",
                table: "ReconciliationMatches");

            migrationBuilder.DropColumn(
                name: "MatchReason",
                table: "ReconciliationMatches");

            migrationBuilder.DropColumn(
                name: "RawPayload",
                table: "BankStatementImports");
        }
    }
}
