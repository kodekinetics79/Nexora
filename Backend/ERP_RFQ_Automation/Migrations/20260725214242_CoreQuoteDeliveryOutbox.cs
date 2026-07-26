using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

public partial class CoreQuoteDeliveryOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "quote_delivery_requests",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                QuoteId = table.Column<long>(type: "bigint", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                Body = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                FromEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                AttachmentFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                RequestedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                AvailableOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastAttemptOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                LeaseOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                DeadLetteredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                LastErrorCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quote_delivery_requests", x => x.Id);
                table.CheckConstraint("CK_quote_delivery_requests_state", "\"AttemptCount\" >= 0 AND \"Version\" > 0 AND trim(\"IdempotencyKey\") <> '' AND trim(\"RecipientEmail\") <> '' AND trim(\"Subject\") <> '' AND trim(\"AttachmentFileName\") <> '' AND ((\"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseUntil\" IS NULL) OR (\"LeaseOwner\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL)) AND NOT (\"CompletedOn\" IS NOT NULL AND \"DeadLetteredOn\" IS NOT NULL) AND ((\"CompletedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL) OR (\"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseUntil\" IS NULL))");
                table.ForeignKey(
                    name: "FK_quote_delivery_requests_Quotes_BusinessUnitId_QuoteId",
                    columns: x => new { x.BusinessUnitId, x.QuoteId },
                    principalTable: "Quotes",
                    principalColumns: new[] { "BusinessUnitID", "ID" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_quote_delivery_requests_BusinessUnitId_IdempotencyKey",
            table: "quote_delivery_requests",
            columns: new[] { "BusinessUnitId", "IdempotencyKey" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_quote_delivery_requests_BusinessUnitId_QuoteId",
            table: "quote_delivery_requests",
            columns: new[] { "BusinessUnitId", "QuoteId" });
        migrationBuilder.CreateIndex(
            name: "IX_quote_delivery_requests_CompletedOn_DeadLetteredOn_Availabl~",
            table: "quote_delivery_requests",
            columns: new[] { "CompletedOn", "DeadLetteredOn", "AvailableOn", "LeaseUntil" });

        migrationBuilder.Sql("""
            ALTER TABLE public.quote_delivery_requests ENABLE ROW LEVEL SECURITY;
            DO $govern$
            DECLARE delivery_sequence text;
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                    DROP POLICY IF EXISTS nexora_tenant_isolation ON public.quote_delivery_requests;
                    CREATE POLICY nexora_tenant_isolation ON public.quote_delivery_requests
                        TO nexora_tenant_app
                        USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                        WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                    REVOKE ALL ON public.quote_delivery_requests FROM nexora_tenant_app;
                    GRANT SELECT, INSERT, UPDATE ON public.quote_delivery_requests TO nexora_tenant_app;
                    SELECT pg_get_serial_sequence('public.quote_delivery_requests', 'Id') INTO delivery_sequence;
                    IF delivery_sequence IS NOT NULL THEN
                        EXECUTE 'REVOKE ALL ON SEQUENCE ' || delivery_sequence || ' FROM nexora_tenant_app';
                        EXECUTE 'GRANT USAGE ON SEQUENCE ' || delivery_sequence || ' TO nexora_tenant_app';
                    END IF;
                END IF;
            END $govern$;

            CREATE OR REPLACE FUNCTION public.nexora_guard_quote_delivery_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'quote delivery requests cannot be deleted';
                END IF;
                IF OLD."CompletedOn" IS NOT NULL OR OLD."DeadLetteredOn" IS NOT NULL THEN
                    RAISE EXCEPTION 'terminal quote delivery requests are immutable';
                END IF;
                IF NEW."BusinessUnitId" <> OLD."BusinessUnitId"
                   OR NEW."QuoteId" <> OLD."QuoteId"
                   OR NEW."IdempotencyKey" <> OLD."IdempotencyKey"
                   OR NEW."RecipientEmail" <> OLD."RecipientEmail"
                   OR NEW."Subject" <> OLD."Subject"
                   OR NEW."Body" <> OLD."Body"
                   OR NEW."FromEmail" IS DISTINCT FROM OLD."FromEmail"
                   OR NEW."AttachmentFileName" <> OLD."AttachmentFileName"
                   OR NEW."RequestedOn" <> OLD."RequestedOn"
                   OR NEW."AttemptCount" < OLD."AttemptCount"
                   OR NEW."Version" <= OLD."Version" THEN
                    RAISE EXCEPTION 'quote delivery identity and payload are immutable';
                END IF;
                RETURN NEW;
            END $fn$;
            CREATE TRIGGER quote_delivery_update_guard BEFORE UPDATE ON public.quote_delivery_requests
                FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_quote_delivery_mutation();
            CREATE TRIGGER quote_delivery_delete_guard BEFORE DELETE ON public.quote_delivery_requests
                FOR EACH ROW EXECUTE FUNCTION public.nexora_guard_quote_delivery_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS quote_delivery_delete_guard ON public.quote_delivery_requests;
            DROP TRIGGER IF EXISTS quote_delivery_update_guard ON public.quote_delivery_requests;
            DROP FUNCTION IF EXISTS public.nexora_guard_quote_delivery_mutation();
            """);
        migrationBuilder.DropTable(name: "quote_delivery_requests");
    }
}
