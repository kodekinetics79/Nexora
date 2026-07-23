using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddFinanceOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinanceOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateVersion = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AvailableOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ProcessedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DeadLetteredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceOutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_FinanceOutbox_State", "\"AttemptCount\" >= 0 AND \"SchemaVersion\" > 0 AND \"AggregateId\" > 0 AND \"AggregateVersion\" >= 0 AND trim(\"AggregateType\") <> '' AND trim(\"EventType\") <> '' AND ((\"LeaseOwner\" IS NULL) = (\"LeaseUntil\" IS NULL)) AND ((\"LeaseToken\" IS NULL) = (\"LeaseUntil\" IS NULL)) AND NOT (\"ProcessedOn\" IS NOT NULL AND \"DeadLetteredOn\" IS NOT NULL) AND ((\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL) OR (\"LeaseOwner\" IS NULL AND \"LeaseUntil\" IS NULL AND \"LeaseToken\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_FinanceOutboxMessages_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceOutbox_Ready",
                table: "FinanceOutboxMessages",
                columns: new[] { "AvailableOn", "LeaseUntil", "OccurredOn", "Id" },
                filter: "\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_FinanceOutbox_AggregateVersionEvent",
                table: "FinanceOutboxMessages",
                columns: new[] { "BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "EventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FinanceOutbox_EventId",
                table: "FinanceOutboxMessages",
                column: "EventId",
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_finance_outbox_core_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' OR
                       (NEW."BusinessUnitId", NEW."EventId", NEW."AggregateType", NEW."AggregateId",
                        NEW."AggregateVersion", NEW."EventType", NEW."Payload", NEW."SchemaVersion", NEW."OccurredOn")
                       IS DISTINCT FROM
                       (OLD."BusinessUnitId", OLD."EventId", OLD."AggregateType", OLD."AggregateId",
                        OLD."AggregateVersion", OLD."EventType", OLD."Payload", OLD."SchemaVersion", OLD."OccurredOn") THEN
                        RAISE EXCEPTION 'finance outbox event identity and payload are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_finance_outbox_core_immutable
                    BEFORE UPDATE OR DELETE ON public."FinanceOutboxMessages"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_outbox_core_immutable();

                CREATE OR REPLACE FUNCTION public.nexora_write_finance_outbox(
                    business_unit_id bigint, aggregate_type text, aggregate_id bigint,
                    aggregate_version bigint, event_type text, event_payload jsonb,
                    event_time timestamp without time zone)
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE deterministic_event_id uuid;
                BEGIN
                    deterministic_event_id := md5(concat_ws(':', business_unit_id, aggregate_type,
                        aggregate_id, aggregate_version, event_type))::uuid;
                    INSERT INTO public."FinanceOutboxMessages"
                        ("BusinessUnitId", "EventId", "AggregateType", "AggregateId", "AggregateVersion",
                         "EventType", "Payload", "SchemaVersion", "OccurredOn", "AvailableOn", "AttemptCount")
                    VALUES (business_unit_id, deterministic_event_id, aggregate_type, aggregate_id,
                        aggregate_version, event_type, event_payload, 1, event_time, event_time, 0)
                    ON CONFLICT ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "EventType")
                    DO NOTHING;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone;
                DECLARE event_payload jsonb;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
                        event_type := 'finance.receivable.draft-created';
                        event_time := COALESCE(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" IN ('Issued', 'Cancelled') THEN
                        event_type := CASE NEW."Status"
                            WHEN 'Issued' THEN 'finance.receivable.issued'
                            ELSE 'finance.receivable.cancelled' END;
                        event_time := CASE NEW."Status" WHEN 'Issued' THEN NEW."IssuedOn" ELSE NEW."VoidedOn" END;
                    ELSE
                        RETURN NEW;
                    END IF;
                    event_payload := jsonb_build_object(
                        'Id', NEW."Id", 'OrderId', NEW."OrderId", 'Status', NEW."Status",
                        'DocumentNumber', NEW."DocumentNumber", 'Version', NEW."Version");
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'ReceivableDocument',
                        NEW."Id", NEW."Version", event_type, event_payload, event_time);
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_receivable_outbox_event
                    AFTER INSERT OR UPDATE ON public."ReceivableDocuments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_outbox_event();

                CREATE OR REPLACE FUNCTION public.nexora_payment_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Posted' THEN
                        event_type := 'finance.payment.posted';
                        event_time := COALESCE(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        event_type := 'finance.payment.reversed';
                        event_time := COALESCE(NEW."ReversedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                    ELSE
                        RETURN NEW;
                    END IF;
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerPayment',
                        NEW."Id", NEW."Version", event_type,
                        jsonb_build_object('Id', NEW."Id", 'Status', NEW."Status",
                            'ReceiptNumber', NEW."ReceiptNumber", 'Version', NEW."Version"), event_time);
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_payment_outbox_event
                    AFTER INSERT OR UPDATE ON public."CustomerPayments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_outbox_event();

                SELECT public.nexora_write_finance_outbox(
                    d."BusinessUnitId", 'ReceivableDocument', d."Id", d."Version",
                    CASE d."Status" WHEN 'Draft' THEN 'finance.receivable.draft-created'
                        WHEN 'Issued' THEN 'finance.receivable.issued'
                        ELSE 'finance.receivable.cancelled' END,
                    jsonb_build_object('Id', d."Id", 'OrderId', d."OrderId", 'Status', d."Status",
                        'DocumentNumber', d."DocumentNumber", 'Version', d."Version"),
                    COALESCE(d."IssuedOn", d."VoidedOn", d."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')))
                FROM public."ReceivableDocuments" d
                WHERE d."Status" IN ('Draft', 'Issued', 'Cancelled');

                SELECT public.nexora_write_finance_outbox(
                    p."BusinessUnitId", 'CustomerPayment', p."Id", p."Version",
                    CASE p."Status" WHEN 'Reversed' THEN 'finance.payment.reversed' ELSE 'finance.payment.posted' END,
                    jsonb_build_object('Id', p."Id", 'Status', p."Status",
                        'ReceiptNumber', p."ReceiptNumber", 'Version', p."Version"),
                    COALESCE(p."ReversedOn", p."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')))
                FROM public."CustomerPayments" p
                WHERE p."Status" IN ('Posted', 'Reversed');

                REVOKE ALL ON FUNCTION public.nexora_write_finance_outbox(bigint, text, bigint, bigint, text, jsonb, timestamp without time zone) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_receivable_outbox_event() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_payment_outbox_event() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_receivable_outbox_event() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_payment_outbox_event() TO nexora_tenant_app;

                ALTER TABLE public."FinanceOutboxMessages" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."FinanceOutboxMessages";
                CREATE POLICY nexora_tenant_isolation ON public."FinanceOutboxMessages" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                REVOKE INSERT, UPDATE, DELETE ON public."FinanceOutboxMessages" FROM nexora_tenant_app;
                REVOKE ALL ON SEQUENCE public."FinanceOutboxMessages_Id_seq" FROM nexora_tenant_app;
                GRANT SELECT ON public."FinanceOutboxMessages" TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.nexora_receivable_outbox_event() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_payment_outbox_event() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_write_finance_outbox(bigint, text, bigint, bigint, text, jsonb, timestamp without time zone) CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_finance_outbox_core_immutable() CASCADE;
                """);

            migrationBuilder.DropTable(
                name: "FinanceOutboxMessages");
        }
    }
}
