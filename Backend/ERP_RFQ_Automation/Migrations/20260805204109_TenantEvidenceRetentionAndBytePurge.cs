using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Tenant-controlled retention for uploaded document BYTES — and only the bytes.
    ///
    /// <para><b>The constraint that shapes this migration.</b> The evidence tables refuse to
    /// forget. <c>trg_source_documents_no_delete</c> raises 55000 on any DELETE from
    /// <c>source_documents</c>; <c>document_pages</c>, <c>document_regions</c>,
    /// <c>field_evidence</c>, <c>canonical_inquiries</c> and <c>canonical_line_items</c> are
    /// append-only under <c>nexora_evidence_append_only</c>; and
    /// <c>trg_protect_source_document_identity</c> freezes each document's hash, filename,
    /// size and creation time, plus its object identity once the document is Cleared. Nothing
    /// here disables, drops or works around any of them — they are the tamper-evidence the
    /// product's compliance story rests on.</para>
    ///
    /// <para><b>So the design purges bytes and keeps the record.</b> Everything added to
    /// <c>source_documents</c> is a NEW nullable column plus one defaulted state column.
    /// <c>content_hash</c>, <c>original_file_name</c>, <c>byte_size</c>, <c>object_bucket</c>,
    /// <c>object_key</c>, <c>object_version</c> and <c>created_on</c> are never written by the
    /// purge — blanking any of them would both raise 23514 and destroy the answer the
    /// tombstone exists to give. After a purge, lineage still resolves: <i>this field came
    /// from document X, SHA-256 Y, purged on DATE under policy N by user Z</i> — and because
    /// the platform cannot rewrite that row even under insider action, the tombstone is
    /// stronger proof than the file it replaces.</para>
    ///
    /// <para><b>New trigger, deliberately.</b> <c>trg_source_documents_purge_forward_only</c>
    /// makes purge state monotonic and re-freezes the identity columns against the retention
    /// path specifically. It <i>strengthens</i> tamper-evidence: it means "purged" cannot be
    /// walked back to "present" to fake the continued existence of a file, by anyone,
    /// including the application.</para>
    /// </summary>
    public partial class TenantEvidenceRetentionAndBytePurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bytes_purged_on",
                table: "source_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purge_policy_code",
                table: "source_documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purge_reason",
                table: "source_documents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "purge_requested_on",
                table: "source_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purge_state",
                table: "source_documents",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Present");

            migrationBuilder.AddColumn<long>(
                name: "purged_by_user_id",
                table: "source_documents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "purged_byte_count",
                table: "source_documents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "evidence_retention_policies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 90),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_retention_policies", x => x.Id);
                    table.CheckConstraint("CK_evidence_retention_policies_retention_days", "\"RetentionDays\" >= 30 AND \"RetentionDays\" <= 3650");
                    table.ForeignKey(
                        name: "FK_evidence_retention_policies_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_documents_tenant_purge_state",
                table: "source_documents",
                columns: new[] { "business_unit_id", "purge_state" },
                filter: "purge_state <> 'Present'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_documents_purge_completion",
                table: "source_documents",
                sql: "(purge_state = 'Purged') = (bytes_purged_on IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_documents_purge_intent",
                table: "source_documents",
                sql: "(purge_state = 'Present') = (purge_requested_on IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_documents_purge_state",
                table: "source_documents",
                sql: "purge_state IN ('Present','PurgeRequested','Purged')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_documents_purged_byte_count",
                table: "source_documents",
                sql: "purged_byte_count >= 0");

            migrationBuilder.CreateIndex(
                name: "UX_evidence_retention_policies_BusinessUnit",
                table: "evidence_retention_policies",
                column: "BusinessUnitId",
                unique: true);

            // The tenant-isolation sweep migrations enabled RLS by scanning for tenant
            // columns at the time they ran, so a table added later must opt in explicitly or
            // the tenant role reads it across tenants. A table that decides which documents
            // get destroyed is the last one that should be cross-readable.
            migrationBuilder.Sql("""
                ALTER TABLE public.evidence_retention_policies ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public.evidence_retention_policies;
                CREATE POLICY nexora_tenant_isolation ON public.evidence_retention_policies
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                -- No DELETE for the tenant role: a retention policy is superseded (a tracked
                -- version bump with an actor, a timestamp and a written reason), never erased.
                -- "Which rule was in force when that document was destroyed?" must stay answerable.
                GRANT SELECT, INSERT, UPDATE ON TABLE public.evidence_retention_policies
                    TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.evidence_retention_policies
                    TO nexora_pipeline_app;
                -- USAGE only for the tenant role: nextval() needs USAGE, and the production
                -- dialect guard forbids nexora_tenant_app holding SELECT or UPDATE on ANY
                -- sequence (currval/last_value leaks other tenants' row-creation volume, and
                -- UPDATE would let a tenant reset the counter).
                GRANT USAGE ON SEQUENCE public."evidence_retention_policies_Id_seq"
                    TO nexora_tenant_app;
                GRANT USAGE, SELECT ON SEQUENCE public."evidence_retention_policies_Id_seq"
                    TO nexora_pipeline_app;
                """);

            // Purge state is monotonic and the lineage columns stay frozen through it.
            //
            // This is additive tamper-evidence, not a relaxation. Without it, a row could be
            // walked from 'Purged' back to 'Present' — asserting that a file still exists
            // when it was destroyed — or its hash/filename/size/object identity could be
            // rewritten alongside a purge, which is precisely how a purge would become
            // indistinguishable from evidence tampering. UPDATE on source_documents is legal
            // today (trg_source_documents_no_delete guards DELETE only), so the guard has to
            // be stated explicitly.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_source_document_purge_forward_only()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE
                    old_rank int;
                    new_rank int;
                BEGIN
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.business_unit_id IS DISTINCT FROM OLD.business_unit_id
                       OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
                       OR NEW.original_file_name IS DISTINCT FROM OLD.original_file_name
                       OR NEW.byte_size IS DISTINCT FROM OLD.byte_size
                       OR NEW.created_on IS DISTINCT FROM OLD.created_on THEN
                        RAISE EXCEPTION 'Source document lineage is immutable across a byte purge'
                            USING ERRCODE = '23514';
                    END IF;

                    IF OLD.purge_state IS DISTINCT FROM 'Present'
                       AND (NEW.object_bucket IS DISTINCT FROM OLD.object_bucket
                            OR NEW.object_key IS DISTINCT FROM OLD.object_key
                            OR NEW.object_version IS DISTINCT FROM OLD.object_version) THEN
                        RAISE EXCEPTION 'Purged source object identity is immutable'
                            USING ERRCODE = '23514';
                    END IF;

                    old_rank := CASE OLD.purge_state
                        WHEN 'Present' THEN 0 WHEN 'PurgeRequested' THEN 1 WHEN 'Purged' THEN 2 END;
                    new_rank := CASE NEW.purge_state
                        WHEN 'Present' THEN 0 WHEN 'PurgeRequested' THEN 1 WHEN 'Purged' THEN 2 END;
                    IF new_rank IS NULL THEN
                        RAISE EXCEPTION 'Unknown source document purge state %', NEW.purge_state
                            USING ERRCODE = '23514';
                    END IF;
                    IF new_rank < old_rank THEN
                        RAISE EXCEPTION 'Source document purge state cannot move backwards from % to %',
                            OLD.purge_state, NEW.purge_state USING ERRCODE = '23514';
                    END IF;

                    IF OLD.bytes_purged_on IS NOT NULL
                       AND NEW.bytes_purged_on IS DISTINCT FROM OLD.bytes_purged_on THEN
                        RAISE EXCEPTION 'A recorded byte purge timestamp is immutable'
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NEW;
                END; $function$;

                DROP TRIGGER IF EXISTS trg_source_documents_purge_forward_only ON source_documents;
                CREATE TRIGGER trg_source_documents_purge_forward_only
                    BEFORE UPDATE ON source_documents
                    FOR EACH ROW EXECUTE FUNCTION nexora_source_document_purge_forward_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_source_documents_purge_forward_only ON source_documents;
                DROP FUNCTION IF EXISTS nexora_source_document_purge_forward_only();
                """);

            migrationBuilder.DropTable(
                name: "evidence_retention_policies");

            migrationBuilder.DropIndex(
                name: "ix_source_documents_tenant_purge_state",
                table: "source_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_documents_purge_completion",
                table: "source_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_documents_purge_intent",
                table: "source_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_documents_purge_state",
                table: "source_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_documents_purged_byte_count",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "bytes_purged_on",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "purge_policy_code",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "purge_reason",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "purge_requested_on",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "purge_state",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "purged_by_user_id",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "purged_byte_count",
                table: "source_documents");
        }
    }
}
