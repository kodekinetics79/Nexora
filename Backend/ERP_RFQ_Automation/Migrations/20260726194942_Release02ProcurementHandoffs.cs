using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release02ProcurementHandoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_OrderItems_ID_OrderID",
                table: "OrderItems",
                columns: new[] { "ID", "OrderID" });

            migrationBuilder.CreateTable(
                name: "procurement_handoffs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerOrderId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerOrderLineId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialDemandLineId = table.Column<long>(type: "bigint", nullable: false),
                    SourcingAwardId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuotedItemId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequiredQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SelectedUnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RequiredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DestinationType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: true),
                    DeliveryLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExternalSystemTarget = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalSupplierPoNumber = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ExternalSupplierPoLineNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ExternalOrderedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ExternalApprovedUnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ExternalExpectedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExternalStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LastSynchronizedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    SourceOfTruth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsAuthoritative = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_handoffs", x => x.Id);
                    table.UniqueConstraint("AK_procurement_handoffs_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_procurement_handoffs_Destination", "\"DestinationType\" IN ('WAREHOUSE','DROP_SHIP') AND (\"DestinationType\" <> 'WAREHOUSE' OR \"WarehouseId\" IS NOT NULL)");
                    table.CheckConstraint("CK_procurement_handoffs_Status", "\"Status\" IN ('CREATED','EXTERNAL_PO_CREATED','SUPPLIER_CONFIRMED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')");
                    table.CheckConstraint("CK_procurement_handoffs_Values", "\"RequiredQuantity\" > 0 AND \"SelectedUnitCost\" >= 0 AND (\"ExternalOrderedQuantity\" IS NULL OR \"ExternalOrderedQuantity\" > 0) AND (\"ExternalApprovedUnitCost\" IS NULL OR \"ExternalApprovedUnitCost\" >= 0)");
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_OrderItems_CustomerOrderLineId_Custome~",
                        columns: x => new { x.CustomerOrderLineId, x.CustomerOrderId },
                        principalTable: "OrderItems",
                        principalColumns: new[] { "ID", "OrderID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_Orders_BusinessUnitId_CustomerOrderId",
                        columns: x => new { x.BusinessUnitId, x.CustomerOrderId },
                        principalTable: "Orders",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_SourcingAwards_BusinessUnitId_Sourcing~",
                        columns: x => new { x.BusinessUnitId, x.SourcingAwardId },
                        principalTable: "SourcingAwards",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_SupplierQuotedItems_BusinessUnitId_Sup~",
                        columns: x => new { x.BusinessUnitId, x.SupplierQuotedItemId },
                        principalTable: "SupplierQuotedItems",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_Suppliers_SupplierId_BusinessUnitId",
                        columns: x => new { x.SupplierId, x.BusinessUnitId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "ID", "BUID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_Warehouses_BusinessUnitId_WarehouseId",
                        columns: x => new { x.BusinessUnitId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_procurement_handoffs_commercial_demand_lines_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.CommercialDemandLineId },
                        principalTable: "commercial_demand_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_CommercialDemandLineId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "CommercialDemandLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_CurrencyId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_CustomerOrderId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "CustomerOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_CustomerOrderLineId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "CustomerOrderLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_ExternalSupplierPoNumbe~",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "ExternalSupplierPoNumber", "ExternalSupplierPoLineNumber" },
                unique: true,
                filter: "\"ExternalSupplierPoNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_IdempotencyKey",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_SourcingAwardId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "SourcingAwardId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_SupplierQuotedItemId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "SupplierQuotedItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_BusinessUnitId_WarehouseId",
                table: "procurement_handoffs",
                columns: new[] { "BusinessUnitId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_CustomerOrderLineId_CustomerOrderId",
                table: "procurement_handoffs",
                columns: new[] { "CustomerOrderLineId", "CustomerOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_handoffs_SupplierId_BusinessUnitId",
                table: "procurement_handoffs",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.Sql("""
                ALTER TABLE public.procurement_handoffs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.procurement_handoffs FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public.procurement_handoffs
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                GRANT SELECT, INSERT, UPDATE ON TABLE public.procurement_handoffs TO nexora_tenant_app;
                REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.procurement_handoffs FROM nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."procurement_handoffs_Id_seq" TO nexora_tenant_app;

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

                CREATE TRIGGER trg_procurement_handoffs_protect_lineage
                    BEFORE UPDATE OR DELETE ON public.procurement_handoffs
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_protect_procurement_handoff_lineage();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_procurement_handoffs_protect_lineage ON public.procurement_handoffs;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.nexora_protect_procurement_handoff_lineage();");

            migrationBuilder.DropTable(
                name: "procurement_handoffs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_OrderItems_ID_OrderID",
                table: "OrderItems");
        }
    }
}
