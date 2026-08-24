using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Extends the <c>source_documents</c> tombstone shape to <c>EmailIngests</c>.
    ///
    /// <para><b>Why not just delete the row.</b> <c>EmailInquiryAssemblies.EmailIngestId</c> is a
    /// RESTRICT foreign key, so the moment a message produced an inquiry its ingest row is
    /// undeletable — a delete-the-row cleanup would succeed only on the messages nobody minds
    /// about and fail on every one that matters. Meanwhile <c>source_documents</c> has already
    /// solved this exact problem: keep the row, freeze its identity, destroy only the bytes, and
    /// let the tombstone be the proof. Applying that shape here is one vocabulary for both tables
    /// rather than a second, differently-shaped answer to the same question.</para>
    ///
    /// <para><b>Three columns, named after their source-document counterparts.</b>
    /// <c>bytes_purged_on</c>, <c>purged_by_user_id</c> and <c>purge_reason</c>. An auditor
    /// reading either table reads the same words.</para>
    ///
    /// <para><b>The constraint and the trigger are the point.</b> A check constraint refuses a
    /// half-written tombstone — a timestamp with no author, or an author with no reason — because
    /// half a record reads as a record while answering neither "who" nor "why". A forward-only
    /// trigger, mirroring <c>nexora_source_document_purge_forward_only</c>, refuses to un-stamp or
    /// rewrite one, and refuses to resurrect a <c>RawEmailPath</c> on a message whose stored copy
    /// has already been destroyed: a row pointing at bytes that no longer exist is the one state
    /// nothing downstream could repair.</para>
    ///
    /// <para>The message identity itself — MessageID, sender, subject, arrival time, triage
    /// verdict — is deliberately NOT frozen here. Triage legitimately re-decides an outcome after
    /// the fact, and freezing those columns would break the reprocess path for every message,
    /// purged or not, to protect a record that keeps its own evidence in the audit log.</para>
    ///
    /// <para>Row-level security and role grants need no change: policies and privileges attach to
    /// the table, and new columns inherit both.</para>
    /// </summary>
    /// <remarks>
    /// BOTH attributes are load-bearing. EF filters migration types on <c>[DbContext]</c> BEFORE
    /// reading the id, so a hand-written migration carrying only <c>[Migration]</c> is silently
    /// never seen rather than rejected.
    /// </remarks>
    [DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
    [Migration("20260824140000_EmailIngestPurgeTombstone")]
    public partial class EmailIngestPurgeTombstone : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<System.DateTime>(
                name: "bytes_purged_on", table: "EmailIngests",
                type: "timestamp without time zone", nullable: true);
            migrationBuilder.AddColumn<long>(
                name: "purged_by_user_id", table: "EmailIngests",
                type: "bigint", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "purge_reason", table: "EmailIngests",
                type: "character varying(1000)", maxLength: 1000, nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."EmailIngests"
                    ADD CONSTRAINT "CK_EmailIngests_purge_tombstone_complete"
                    CHECK (("bytes_purged_on" IS NULL AND "purged_by_user_id" IS NULL
                            AND "purge_reason" IS NULL)
                        OR ("bytes_purged_on" IS NOT NULL AND "purged_by_user_id" IS NOT NULL
                            AND length(trim("purge_reason")) > 0));
                """);

            // Finding the messages still holding a stored copy, and the ones already cleared, is
            // the read the cleanup does every time it draws the screen.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_EmailIngests_bytes_purged_on"
                    ON public."EmailIngests" ("bytes_purged_on");
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_email_ingest_purge_forward_only()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."bytes_purged_on" IS NOT NULL THEN
                        IF NEW."bytes_purged_on" IS DISTINCT FROM OLD."bytes_purged_on" THEN
                            RAISE EXCEPTION 'A recorded message purge timestamp is immutable'
                                USING ERRCODE = '23514';
                        END IF;
                        IF NEW."purged_by_user_id" IS DISTINCT FROM OLD."purged_by_user_id"
                           OR NEW."purge_reason" IS DISTINCT FROM OLD."purge_reason" THEN
                            RAISE EXCEPTION 'A recorded message purge author and reason are immutable'
                                USING ERRCODE = '23514';
                        END IF;
                        IF NEW."RawEmailPath" IS NOT NULL THEN
                            RAISE EXCEPTION 'A purged message cannot regain a stored copy'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END; $$;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_email_ingest_purge_forward_only ON public."EmailIngests";
                CREATE TRIGGER trg_email_ingest_purge_forward_only
                    BEFORE UPDATE ON public."EmailIngests"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_email_ingest_purge_forward_only();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_email_ingest_purge_forward_only ON public."EmailIngests";
                DROP FUNCTION IF EXISTS public.nexora_email_ingest_purge_forward_only();
                DROP INDEX IF EXISTS public."IX_EmailIngests_bytes_purged_on";
                ALTER TABLE public."EmailIngests"
                    DROP CONSTRAINT IF EXISTS "CK_EmailIngests_purge_tombstone_complete";
                """);
            migrationBuilder.DropColumn(name: "purge_reason", table: "EmailIngests");
            migrationBuilder.DropColumn(name: "purged_by_user_id", table: "EmailIngests");
            migrationBuilder.DropColumn(name: "bytes_purged_on", table: "EmailIngests");
        }
    }
}
