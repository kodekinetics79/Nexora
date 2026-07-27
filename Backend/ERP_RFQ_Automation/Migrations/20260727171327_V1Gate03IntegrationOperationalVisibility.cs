using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V1Gate03IntegrationOperationalVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_procurement_handoffs_Status",
                table: "procurement_handoffs");

            migrationBuilder.DropIndex(
                name: "IX_EmailIngests_EmailConfigurationID",
                table: "EmailIngests");

            migrationBuilder.DropIndex(
                name: "UQ__EmailIng__C87C037D5950F99E",
                table: "EmailIngests");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeadLetteredOn",
                table: "procurement_outbox",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "procurement_outbox",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "procurement_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                table: "procurement_outbox",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginCorrelationId",
                table: "procurement_outbox",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "procurement_outbox",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredOn",
                table: "procurement_handoffs",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchedOn",
                table: "procurement_handoffs",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSalesOrderNumber",
                table: "procurement_handoffs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCorrelationId",
                table: "procurement_handoffs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastExternalEventId",
                table: "procurement_handoffs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupplierConfirmedOn",
                table: "procurement_handoffs",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "procurement_callback_receipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ProcurementHandoffId = table.Column<long>(type: "bigint", nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExternalEventId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RejectionCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ObservedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ObservedUnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ObservedStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ObservedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReceivedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AppliedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_callback_receipts", x => x.Id);
                    table.UniqueConstraint("AK_procurement_callback_receipts_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_procurement_callback_receipts_Status", "\"Status\" IN ('APPLIED','REJECTED')");
                    table.ForeignKey(
                        name: "FK_procurement_callback_receipts_procurement_handoffs_Business~",
                        columns: x => new { x.BusinessUnitId, x.ProcurementHandoffId },
                        principalTable: "procurement_handoffs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_outbox_BusinessUnitId_DeadLetteredOn_NextAttemp~",
                table: "procurement_outbox",
                columns: new[] { "BusinessUnitId", "DeadLetteredOn", "NextAttemptOn" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_procurement_handoffs_Status",
                table: "procurement_handoffs",
                sql: "\"Status\" IN ('CREATED','EXTERNAL_PO_CREATED','SUPPLIER_CONFIRMED','DISPATCHED','DELIVERED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')");

            migrationBuilder.CreateIndex(
                name: "UQ_EmailIngests_EmailConfigurationID_MessageID",
                table: "EmailIngests",
                columns: new[] { "EmailConfigurationID", "MessageID" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_procurement_callback_receipts_BusinessUnitId_ProcurementHan~",
                table: "procurement_callback_receipts",
                columns: new[] { "BusinessUnitId", "ProcurementHandoffId", "ReceivedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_callback_receipts_BusinessUnitId_SourceSystem_E~",
                table: "procurement_callback_receipts",
                columns: new[] { "BusinessUnitId", "SourceSystem", "ExternalEventId" },
                unique: true);

            migrationBuilder.Sql("""
                UPDATE public.procurement_outbox
                SET "OriginCorrelationId" = 'legacy-outbox:' || "Id"::text
                WHERE "OriginCorrelationId" IS NULL;

                UPDATE public.procurement_outbox
                SET "ProviderName" = 'legacy-unverified', "ProviderReference" = NULL
                WHERE "Status" = 'SENT' AND "ProviderReference" LIKE 'legacy-notification-acceptance:%';

                ALTER TABLE public.procurement_callback_receipts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.procurement_callback_receipts FORCE ROW LEVEL SECURITY;

                DO $rls$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        CREATE POLICY nexora_tenant_isolation ON public.procurement_callback_receipts
                            TO nexora_tenant_app
                            USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                            WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                        GRANT SELECT, INSERT ON TABLE public.procurement_callback_receipts TO nexora_tenant_app;
                        REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
                            ON TABLE public.procurement_callback_receipts FROM nexora_tenant_app;
                        REVOKE SELECT, UPDATE ON SEQUENCE public."procurement_callback_receipts_Id_seq"
                            FROM nexora_tenant_app;
                        GRANT USAGE ON SEQUENCE public."procurement_callback_receipts_Id_seq"
                            TO nexora_tenant_app;
                    END IF;
                END
                $rls$;

                CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_callback_receipt()
                RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    RAISE EXCEPTION 'procurement callback receipts are append-only' USING ERRCODE = '23514';
                END
                $body$;

                CREATE TRIGGER trg_procurement_callback_receipts_append_only
                    BEFORE UPDATE OR DELETE ON public.procurement_callback_receipts
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_procurement_callback_receipt();

                CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_handoff_lineage()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $body$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Procurement handoff records are append-preserving.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                       OR NEW."CustomerOrderId" IS DISTINCT FROM OLD."CustomerOrderId"
                       OR NEW."CustomerOrderLineId" IS DISTINCT FROM OLD."CustomerOrderLineId"
                       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
                       OR NEW."SourcingAwardId" IS DISTINCT FROM OLD."SourcingAwardId"
                       OR NEW."SupplierQuotedItemId" IS DISTINCT FROM OLD."SupplierQuotedItemId"
                       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
                       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
                       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
                       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId"
                       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
                       OR NEW."RequiredQuantity" IS DISTINCT FROM OLD."RequiredQuantity"
                       OR NEW."SelectedUnitCost" IS DISTINCT FROM OLD."SelectedUnitCost"
                       OR NEW."RequiredOn" IS DISTINCT FROM OLD."RequiredOn"
                       OR NEW."DestinationType" IS DISTINCT FROM OLD."DestinationType"
                       OR NEW."WarehouseId" IS DISTINCT FROM OLD."WarehouseId"
                       OR NEW."DeliveryLocation" IS DISTINCT FROM OLD."DeliveryLocation"
                       OR NEW."ExternalSystemTarget" IS DISTINCT FROM OLD."ExternalSystemTarget"
                       OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                       OR NEW."RequestHash" IS DISTINCT FROM OLD."RequestHash" THEN
                        RAISE EXCEPTION 'Procurement handoff commercial lineage is immutable.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND NOT (
                        (OLD."Status" = 'CREATED' AND NEW."Status" IN ('EXTERNAL_PO_CREATED','CANCELLED')) OR
                        (OLD."Status" = 'EXTERNAL_PO_CREATED' AND NEW."Status" IN ('SUPPLIER_CONFIRMED','CANCELLED')) OR
                        (OLD."Status" = 'SUPPLIER_CONFIRMED' AND NEW."Status" IN ('DISPATCHED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
                        (OLD."Status" = 'DISPATCHED' AND NEW."Status" IN ('DELIVERED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
                        (OLD."Status" = 'DELIVERED' AND NEW."Status" IN ('PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
                        (OLD."Status" = 'PARTIALLY_RECEIVED' AND NEW."Status" IN ('RECEIVED','CANCELLED'))
                    ) THEN
                        RAISE EXCEPTION 'Invalid procurement handoff status transition.' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public.procurement_callback_receipts LIMIT 1) THEN
                        RAISE EXCEPTION
                            'Gate 3 contains provider callback evidence; restore the verified pre-upgrade backup instead of downgrading.'
                            USING ERRCODE = '55000';
                    END IF;
                    IF EXISTS (
                        SELECT 1
                        FROM public."EmailIngests"
                        GROUP BY "MessageID"
                        HAVING count(DISTINCT "EmailConfigurationID") > 1
                    ) THEN
                        RAISE EXCEPTION
                            'Gate 3 mailbox identity cannot be represented by the previous global Message-ID index; restore the verified pre-upgrade backup.'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $body$;

                DROP TRIGGER IF EXISTS trg_procurement_callback_receipts_append_only
                    ON public.procurement_callback_receipts;
                DROP FUNCTION IF EXISTS public.nexora_protect_procurement_callback_receipt();

                ALTER TABLE public.procurement_handoffs
                    DISABLE TRIGGER trg_procurement_handoffs_protect_lineage;
                UPDATE public.procurement_handoffs
                SET "Status" = CASE
                    WHEN "Status" = 'DISPATCHED' THEN 'SUPPLIER_CONFIRMED'
                    WHEN "Status" = 'DELIVERED' THEN 'RECEIVED'
                    ELSE "Status"
                END,
                "ExternalStatus" = CASE
                    WHEN "ExternalStatus" = 'DISPATCHED' THEN 'SUPPLIER_CONFIRMED'
                    WHEN "ExternalStatus" = 'DELIVERED' THEN 'RECEIVED'
                    ELSE "ExternalStatus"
                END;
                ALTER TABLE public.procurement_handoffs
                    ENABLE TRIGGER trg_procurement_handoffs_protect_lineage;
                """);

            migrationBuilder.DropTable(
                name: "procurement_callback_receipts");

            migrationBuilder.DropIndex(
                name: "IX_procurement_outbox_BusinessUnitId_DeadLetteredOn_NextAttemp~",
                table: "procurement_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_procurement_handoffs_Status",
                table: "procurement_handoffs");

            migrationBuilder.DropIndex(
                name: "UQ_EmailIngests_EmailConfigurationID_MessageID",
                table: "EmailIngests");

            migrationBuilder.DropColumn(
                name: "DeadLetteredOn",
                table: "procurement_outbox");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "procurement_outbox");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "procurement_outbox");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "procurement_outbox");

            migrationBuilder.DropColumn(
                name: "OriginCorrelationId",
                table: "procurement_outbox");

            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "procurement_outbox");

            migrationBuilder.DropColumn(
                name: "DeliveredOn",
                table: "procurement_handoffs");

            migrationBuilder.DropColumn(
                name: "DispatchedOn",
                table: "procurement_handoffs");

            migrationBuilder.DropColumn(
                name: "ExternalSalesOrderNumber",
                table: "procurement_handoffs");

            migrationBuilder.DropColumn(
                name: "LastCorrelationId",
                table: "procurement_handoffs");

            migrationBuilder.DropColumn(
                name: "LastExternalEventId",
                table: "procurement_handoffs");

            migrationBuilder.DropColumn(
                name: "SupplierConfirmedOn",
                table: "procurement_handoffs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_procurement_handoffs_Status",
                table: "procurement_handoffs",
                sql: "\"Status\" IN ('CREATED','EXTERNAL_PO_CREATED','SUPPLIER_CONFIRMED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')");

            migrationBuilder.CreateIndex(
                name: "IX_EmailIngests_EmailConfigurationID",
                table: "EmailIngests",
                column: "EmailConfigurationID");

            migrationBuilder.CreateIndex(
                name: "UQ__EmailIng__C87C037D5950F99E",
                table: "EmailIngests",
                column: "MessageID",
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_handoff_lineage()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $body$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Procurement handoff records are append-preserving.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                       OR NEW."CustomerOrderId" IS DISTINCT FROM OLD."CustomerOrderId"
                       OR NEW."CustomerOrderLineId" IS DISTINCT FROM OLD."CustomerOrderLineId"
                       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
                       OR NEW."SourcingAwardId" IS DISTINCT FROM OLD."SourcingAwardId"
                       OR NEW."SupplierQuotedItemId" IS DISTINCT FROM OLD."SupplierQuotedItemId"
                       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
                       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
                       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
                       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId"
                       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
                       OR NEW."RequiredQuantity" IS DISTINCT FROM OLD."RequiredQuantity"
                       OR NEW."SelectedUnitCost" IS DISTINCT FROM OLD."SelectedUnitCost"
                       OR NEW."RequiredOn" IS DISTINCT FROM OLD."RequiredOn"
                       OR NEW."DestinationType" IS DISTINCT FROM OLD."DestinationType"
                       OR NEW."WarehouseId" IS DISTINCT FROM OLD."WarehouseId"
                       OR NEW."DeliveryLocation" IS DISTINCT FROM OLD."DeliveryLocation"
                       OR NEW."ExternalSystemTarget" IS DISTINCT FROM OLD."ExternalSystemTarget"
                       OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                       OR NEW."RequestHash" IS DISTINCT FROM OLD."RequestHash" THEN
                        RAISE EXCEPTION 'Procurement handoff commercial lineage is immutable.' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND NOT (
                        (OLD."Status" = 'CREATED' AND NEW."Status" IN ('EXTERNAL_PO_CREATED','CANCELLED')) OR
                        (OLD."Status" = 'EXTERNAL_PO_CREATED' AND NEW."Status" IN ('SUPPLIER_CONFIRMED','CANCELLED')) OR
                        (OLD."Status" = 'SUPPLIER_CONFIRMED' AND NEW."Status" IN ('PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
                        (OLD."Status" = 'PARTIALLY_RECEIVED' AND NEW."Status" IN ('RECEIVED','CANCELLED'))
                    ) THEN
                        RAISE EXCEPTION 'Invalid procurement handoff status transition.' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                """);
        }
    }
}
