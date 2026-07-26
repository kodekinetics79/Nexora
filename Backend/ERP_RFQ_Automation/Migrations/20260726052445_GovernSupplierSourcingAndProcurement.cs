using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernSupplierSourcingAndProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId",
                table: "SupplierQuotedItems");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "SupplierSolicitations",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "SupplierSolicitations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestedRfqItemIdsJson",
                table: "SupplierSolicitations",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "SupplierSolicitations",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<decimal>(
                name: "AvailableQuantity",
                table: "SupplierQuotedItems",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DutyCost",
                table: "SupplierQuotedItems",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FreightCost",
                table: "SupplierQuotedItems",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LandedUnitCost",
                table: "SupplierQuotedItems",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LeadTimeDays",
                table: "SupplierQuotedItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumOrderQuantity",
                table: "SupplierQuotedItems",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtherCost",
                table: "SupplierQuotedItems",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "ProductId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuoteRevision",
                table: "SupplierQuotedItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "ReliabilitySnapshot",
                table: "SupplierQuotedItems",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "SupplierQuotedItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseIdempotencyKey",
                table: "SupplierQuotedItems",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RfqId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RfqItemId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SupplierSolicitationId",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "SupplierQuotedItems",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "CurrencyId",
                table: "SourcingAwards",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "SourcingAwards",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LandedUnitCost",
                table: "SourcingAwards",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "SourcingAwards",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SourcingAwards",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "APPROVED");

            migrationBuilder.AddColumn<long>(
                name: "SupplierQuotedItemId",
                table: "SourcingAwards",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "SourcingAwards",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                UPDATE "SupplierSolicitations"
                SET "IdempotencyKey" = 'legacy-solicitation:' || "Id"::text,
                    "RequestHash" = repeat('0', 64)
                WHERE "IdempotencyKey" = '' OR "RequestHash" = '';

                UPDATE "SourcingAwards"
                SET "Status" = 'APPROVED'
                WHERE "Status" = '';

                WITH parsed AS (
                    SELECT quoted."Id",
                           substring(quoted."QuoteReference" from 'rfq=([0-9]+)')::bigint AS rfq_id,
                           substring(quoted."QuoteReference" from 'item=([0-9]+)')::bigint AS rfq_item_id,
                           NULLIF(substring(quoted."QuoteReference" from 'lead=([0-9]+)'), '')::integer AS lead_days
                    FROM "SupplierQuotedItems" quoted
                    WHERE quoted."BusinessUnitId" IS NOT NULL
                      AND quoted."QuoteReference" ~ '(^|;)rfq=[0-9]+;item=[0-9]+(;|$)'
                ), reconciled AS (
                    SELECT parsed."Id", parsed.rfq_id, parsed.rfq_item_id, parsed.lead_days, item."ProductID"
                    FROM parsed
                    JOIN "SupplierQuotedItems" quoted ON quoted."Id" = parsed."Id"
                    JOIN "RFQ" rfq ON rfq."ID" = parsed.rfq_id
                                   AND rfq."BusinessUnitID" = quoted."BusinessUnitId"
                    JOIN "RFQItems" item ON item."ID" = parsed.rfq_item_id
                                         AND item."RFQID" = parsed.rfq_id
                    JOIN "Suppliers" supplier ON supplier."ID" = quoted."SupplierId"
                                              AND supplier."BUID" = quoted."BusinessUnitId"
                )
                UPDATE "SupplierQuotedItems" quoted
                SET "RfqId" = reconciled.rfq_id,
                    "RfqItemId" = reconciled.rfq_item_id,
                    "LeadTimeDays" = reconciled.lead_days,
                    "ProductId" = reconciled."ProductID",
                    "ResponseIdempotencyKey" = 'legacy-quote:' || reconciled."Id"::text,
                    "RequestHash" = repeat('0', 64)
                FROM reconciled
                WHERE quoted."Id" = reconciled."Id";

                WITH solicitation_candidates AS (
                    SELECT quoted."Id" AS quote_id,
                           min(solicitation."Id") AS solicitation_id,
                           count(*) AS candidate_count
                    FROM "SupplierQuotedItems" quoted
                    JOIN "SupplierSolicitations" solicitation
                      ON solicitation."BusinessUnitId" = quoted."BusinessUnitId"
                     AND solicitation."RfqId" = quoted."RfqId"
                     AND solicitation."SupplierId" = quoted."SupplierId"
                    WHERE quoted."RfqId" IS NOT NULL
                      AND quoted."SupplierSolicitationId" IS NULL
                    GROUP BY quoted."Id"
                )
                UPDATE "SupplierQuotedItems" quoted
                SET "SupplierSolicitationId" = candidate.solicitation_id
                FROM solicitation_candidates candidate
                WHERE quoted."Id" = candidate.quote_id
                  AND candidate.candidate_count = 1;

                UPDATE "SupplierQuotedItems"
                SET "ResponseIdempotencyKey" = COALESCE("ResponseIdempotencyKey", 'legacy-quote:' || "Id"::text),
                    "RequestHash" = COALESCE("RequestHash", repeat('0', 64));

                WITH award_candidates AS (
                    SELECT award."Id" AS award_id,
                           min(quoted."Id") AS quote_id,
                           count(*) AS candidate_count
                    FROM "SourcingAwards" award
                    JOIN "SupplierQuotedItems" quoted
                      ON quoted."BusinessUnitId" = award."BusinessUnitId"
                     AND quoted."RfqId" = award."RfqId"
                     AND quoted."RfqItemId" IS NOT DISTINCT FROM award."RfqItemId"
                     AND quoted."SupplierId" = award."SupplierId"
                     AND quoted."UnitPrice" = award."UnitPrice"
                    WHERE award."SupplierQuotedItemId" IS NULL
                    GROUP BY award."Id"
                )
                UPDATE "SourcingAwards" award
                SET "SupplierQuotedItemId" = candidate.quote_id,
                    "CurrencyId" = quoted."CurrencyId",
                    "LandedUnitCost" = COALESCE(quoted."LandedUnitCost", quoted."UnitPrice"),
                    "IdempotencyKey" = 'legacy-award:' || award."Id"::text,
                    "RequestHash" = repeat('0', 64)
                FROM award_candidates candidate
                JOIN "SupplierQuotedItems" quoted ON quoted."Id" = candidate.quote_id
                WHERE award."Id" = candidate.award_id
                  AND candidate.candidate_count = 1;

                UPDATE "SourcingAwards"
                SET "IdempotencyKey" = COALESCE("IdempotencyKey", 'legacy-award:' || "Id"::text),
                    "RequestHash" = COALESCE("RequestHash", repeat('0', 64));

                DO $block$
                DECLARE
                    unresolved_quote_ids text;
                    unresolved_award_ids text;
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "SupplierQuotedItems"
                        WHERE "Quantity" <= 0 OR COALESCE("UnitPrice", 0) < 0)
                    THEN
                        RAISE EXCEPTION 'Legacy supplier quote quantities/prices are invalid; reconcile affected rows before upgrade';
                    END IF;
                    SELECT string_agg(unresolved."Id"::text, ',' ORDER BY unresolved."Id")
                    INTO unresolved_quote_ids
                    FROM (
                        SELECT quoted."Id"
                        FROM "SupplierQuotedItems" quoted
                        WHERE quoted."RfqId" IS NULL
                           OR quoted."RfqItemId" IS NULL
                           OR quoted."ProductId" IS NULL
                           OR quoted."SupplierSolicitationId" IS NULL
                           OR quoted."CurrencyId" IS NULL
                        ORDER BY quoted."Id"
                        LIMIT 20
                    ) unresolved;
                    IF unresolved_quote_ids IS NOT NULL THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23503',
                            MESSAGE = 'Unresolved legacy supplier quote lineage blocks procurement upgrade',
                            DETAIL = 'SupplierQuotedItems ids=' || unresolved_quote_ids,
                            HINT = 'Reconcile tenant, RFQ, RFQ item, product, solicitation, and currency references before retrying the migration.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "SupplierSolicitations" solicitation
                        LEFT JOIN "RFQ" rfq ON rfq."ID" = solicitation."RfqId"
                                           AND rfq."BusinessUnitID" = solicitation."BusinessUnitId"
                        LEFT JOIN "Suppliers" supplier ON supplier."ID" = solicitation."SupplierId"
                                                     AND supplier."BUID" = solicitation."BusinessUnitId"
                        WHERE rfq."ID" IS NULL OR supplier."ID" IS NULL)
                    THEN
                        RAISE EXCEPTION 'Supplier solicitation tenant lineage is invalid; reconcile affected rows before upgrade';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM "SourcingAwards" award
                        LEFT JOIN "RFQ" rfq ON rfq."ID" = award."RfqId"
                                           AND rfq."BusinessUnitID" = award."BusinessUnitId"
                        LEFT JOIN "Suppliers" supplier ON supplier."ID" = award."SupplierId"
                                                     AND supplier."BUID" = award."BusinessUnitId"
                        WHERE rfq."ID" IS NULL OR supplier."ID" IS NULL
                           OR (award."RfqItemId" IS NOT NULL AND NOT EXISTS (
                               SELECT 1 FROM "RFQItems" item
                               WHERE item."ID" = award."RfqItemId" AND item."RFQID" = award."RfqId")))
                    THEN
                        RAISE EXCEPTION 'Sourcing award tenant or RFQ-line lineage is invalid; reconcile affected rows before upgrade';
                    END IF;
                    SELECT string_agg(unresolved."Id"::text, ',' ORDER BY unresolved."Id")
                    INTO unresolved_award_ids
                    FROM (
                        SELECT award."Id"
                        FROM "SourcingAwards" award
                        WHERE award."RfqItemId" IS NULL
                           OR award."SupplierQuotedItemId" IS NULL
                           OR award."CurrencyId" IS NULL
                           OR award."LandedUnitCost" IS NULL
                           OR award."IdempotencyKey" IS NULL
                           OR award."RequestHash" IS NULL
                        ORDER BY award."Id"
                        LIMIT 20
                    ) unresolved;
                    IF unresolved_award_ids IS NOT NULL THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23503',
                            MESSAGE = 'Unresolved legacy sourcing award lineage blocks procurement upgrade',
                            DETAIL = 'SourcingAwards ids=' || unresolved_award_ids,
                            HINT = 'Reconcile RFQ item, supplier quote, currency, landed cost, and command lineage before retrying the migration.';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SupplierSolicitations_BusinessUnitId_Id",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourcingAwards_BusinessUnitId_Id",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_RFQItems_ID_RFQID",
                table: "RFQItems",
                columns: new[] { "ID", "RFQID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_RFQ_BusinessUnitID_ID",
                table: "RFQ",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.CreateTable(
                name: "procurement_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateVersion = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "procurement_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierSolicitationId = table.Column<long>(type: "bigint", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SentOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procurement_outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_procurement_outbox_SupplierSolicitations_BusinessUnitId_Sup~",
                        columns: x => new { x.BusinessUnitId, x.SupplierSolicitationId },
                        principalTable: "SupplierSolicitations",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_purchase_orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    PurchaseOrderNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalValue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ExpectedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_purchase_orders", x => x.Id);
                    table.UniqueConstraint("AK_supplier_purchase_orders_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_supplier_purchase_orders_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_orders_RFQ_BusinessUnitId_RfqId",
                        columns: x => new { x.BusinessUnitId, x.RfqId },
                        principalTable: "RFQ",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_orders_Suppliers_SupplierId_BusinessUnitId",
                        columns: x => new { x.SupplierId, x.BusinessUnitId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "ID", "BUID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "goods_receipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    ReceiptNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ReceivedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipts", x => x.Id);
                    table.UniqueConstraint("AK_goods_receipts_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_goods_receipts_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_goods_receipts_supplier_purchase_orders_BusinessUnitId_Supp~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId },
                        principalTable: "supplier_purchase_orders",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_purchase_order_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    SourcingAwardId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierQuotedItemId = table.Column<long>(type: "bigint", nullable: false),
                    RfqId = table.Column<long>(type: "bigint", nullable: false),
                    RfqItemId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: true),
                    IncomingInventoryId = table.Column<long>(type: "bigint", nullable: true),
                    OrderedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LandedUnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_purchase_order_lines", x => x.Id);
                    table.UniqueConstraint("AK_supplier_purchase_order_lines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_Inventory_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_RFQItems_RfqItemId_RfqId",
                        columns: x => new { x.RfqItemId, x.RfqId },
                        principalTable: "RFQItems",
                        principalColumns: new[] { "ID", "RFQID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_RFQ_BusinessUnitId_RfqId",
                        columns: x => new { x.BusinessUnitId, x.RfqId },
                        principalTable: "RFQ",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_SourcingAwards_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.SourcingAwardId },
                        principalTable: "SourcingAwards",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_SupplierQuotedItems_SupplierQ~",
                        column: x => x.SupplierQuotedItemId,
                        principalTable: "SupplierQuotedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_incoming_inventory_IncomingIn~",
                        column: x => x.IncomingInventoryId,
                        principalTable: "incoming_inventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_purchase_order_lines_supplier_purchase_orders_Busi~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId },
                        principalTable: "supplier_purchase_orders",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "goods_receipt_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    GoodsReceiptId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderLineId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryMovementId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods_receipt_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_goods_receipt_lines_goods_receipts_BusinessUnitId_GoodsRece~",
                        columns: x => new { x.BusinessUnitId, x.GoodsReceiptId },
                        principalTable: "goods_receipts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_goods_receipt_lines_inventory_movements_InventoryMovementId",
                        column: x => x.InventoryMovementId,
                        principalTable: "inventory_movements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_goods_receipt_lines_supplier_purchase_order_lines_BusinessU~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId },
                        principalTable: "supplier_purchase_order_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_IdempotencyKey",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_SupplierId_BusinessUnitId",
                table: "SupplierSolicitations",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_RfqId_RfqItemId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "RfqId", "RfqItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_ProductId",
                table: "SupplierQuotedItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_RfqItemId_RfqId",
                table: "SupplierQuotedItems",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_SupplierSolicitationId",
                table: "SupplierQuotedItems",
                column: "SupplierSolicitationId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierQuotedItems_BU_ResponseKey",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "ResponseIdempotencyKey" },
                unique: true,
                filter: "\"ResponseIdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BusinessUnitId_IdempotencyKey",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_BusinessUnitId_RfqItemId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "RfqItemId" },
                filter: "\"RfqItemId\" IS NOT NULL AND \"Status\" IN ('PROPOSED','APPROVED')");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_RfqItemId_RfqId",
                table: "SourcingAwards",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_SupplierId_BusinessUnitId",
                table: "SourcingAwards",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_SupplierQuotedItemId",
                table: "SourcingAwards",
                column: "SupplierQuotedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_GoodsReceiptId",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "GoodsReceiptId" });

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipt_lines_BusinessUnitId_SupplierPurchaseOrderLin~",
                table: "goods_receipt_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId" });

            migrationBuilder.CreateIndex(
                name: "UX_goods_receipt_lines_InventoryMovementId",
                table: "goods_receipt_lines",
                column: "InventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipts_BusinessUnitId_IdempotencyKey",
                table: "goods_receipts",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipts_BusinessUnitId_ReceiptNumber",
                table: "goods_receipts",
                columns: new[] { "BusinessUnitId", "ReceiptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipts_BusinessUnitId_SupplierPurchaseOrderId",
                table: "goods_receipts",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_goods_receipts_WarehouseId",
                table: "goods_receipts",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_procurement_events_BusinessUnitId_AggregateType_AggregateId~",
                table: "procurement_events",
                columns: new[] { "BusinessUnitId", "AggregateType", "AggregateId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_procurement_events_BusinessUnitId_EventType_IdempotencyKey",
                table: "procurement_events",
                columns: new[] { "BusinessUnitId", "EventType", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_procurement_outbox_BusinessUnitId_SupplierSolicitationId",
                table: "procurement_outbox",
                columns: new[] { "BusinessUnitId", "SupplierSolicitationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_procurement_outbox_Status_NextAttemptOn",
                table: "procurement_outbox",
                columns: new[] { "Status", "NextAttemptOn" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_RfqId",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_SourcingAwardId",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "SourcingAwardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_BusinessUnitId_SupplierPurcha~",
                table: "supplier_purchase_order_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_IncomingInventoryId",
                table: "supplier_purchase_order_lines",
                column: "IncomingInventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_InventoryId",
                table: "supplier_purchase_order_lines",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_ProductId",
                table: "supplier_purchase_order_lines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_RfqItemId_RfqId",
                table: "supplier_purchase_order_lines",
                columns: new[] { "RfqItemId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_SupplierQuotedItemId",
                table: "supplier_purchase_order_lines",
                column: "SupplierQuotedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_order_lines_WarehouseId",
                table: "supplier_purchase_order_lines",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_orders_BusinessUnitId_CurrencyId",
                table: "supplier_purchase_orders",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_orders_BusinessUnitId_IdempotencyKey",
                table: "supplier_purchase_orders",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_orders_BusinessUnitId_PurchaseOrderNumber",
                table: "supplier_purchase_orders",
                columns: new[] { "BusinessUnitId", "PurchaseOrderNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_orders_BusinessUnitId_RfqId",
                table: "supplier_purchase_orders",
                columns: new[] { "BusinessUnitId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_purchase_orders_SupplierId_BusinessUnitId",
                table: "supplier_purchase_orders",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_RFQItems_RfqItemId_RfqId",
                table: "SourcingAwards",
                columns: new[] { "RfqItemId", "RfqId" },
                principalTable: "RFQItems",
                principalColumns: new[] { "ID", "RFQID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_RFQ_BusinessUnitId_RfqId",
                table: "SourcingAwards",
                columns: new[] { "BusinessUnitId", "RfqId" },
                principalTable: "RFQ",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_SupplierQuotedItems_SupplierQuotedItemId",
                table: "SourcingAwards",
                column: "SupplierQuotedItemId",
                principalTable: "SupplierQuotedItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SourcingAwards_Suppliers_SupplierId_BusinessUnitId",
                table: "SourcingAwards",
                columns: new[] { "SupplierId", "BusinessUnitId" },
                principalTable: "Suppliers",
                principalColumns: new[] { "ID", "BUID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_Products_ProductId",
                table: "SupplierQuotedItems",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_RFQItems_RfqItemId_RfqId",
                table: "SupplierQuotedItems",
                columns: new[] { "RfqItemId", "RfqId" },
                principalTable: "RFQItems",
                principalColumns: new[] { "ID", "RFQID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_RFQ_BusinessUnitId_RfqId",
                table: "SupplierQuotedItems",
                columns: new[] { "BusinessUnitId", "RfqId" },
                principalTable: "RFQ",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierQuotedItems_SupplierSolicitations_SupplierSolicitat~",
                table: "SupplierQuotedItems",
                column: "SupplierSolicitationId",
                principalTable: "SupplierSolicitations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierSolicitations_RFQ_BusinessUnitId_RfqId",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "RfqId" },
                principalTable: "RFQ",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierSolicitations_Suppliers_SupplierId_BusinessUnitId",
                table: "SupplierSolicitations",
                columns: new[] { "SupplierId", "BusinessUnitId" },
                principalTable: "Suppliers",
                principalColumns: new[] { "ID", "BUID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE supplier_purchase_orders
                    ADD CONSTRAINT "CK_supplier_purchase_orders_TotalValue" CHECK ("TotalValue" >= 0),
                    ADD CONSTRAINT "CK_supplier_purchase_orders_Status" CHECK ("Status" IN ('DRAFT','ISSUED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED'));
                ALTER TABLE supplier_purchase_order_lines
                    ADD CONSTRAINT "CK_supplier_purchase_order_lines_Quantities" CHECK ("OrderedQuantity" > 0 AND "ReceivedQuantity" >= 0 AND "ReceivedQuantity" <= "OrderedQuantity"),
                    ADD CONSTRAINT "CK_supplier_purchase_order_lines_Costs" CHECK ("UnitCost" >= 0 AND "LandedUnitCost" >= "UnitCost");
                ALTER TABLE goods_receipt_lines
                    ADD CONSTRAINT "CK_goods_receipt_lines_Quantity" CHECK ("ReceivedQuantity" > 0);
                ALTER TABLE "SupplierQuotedItems"
                    ADD CONSTRAINT "CK_SupplierQuotedItems_ProcurementValues" CHECK (
                        ("UnitPrice" IS NULL OR "UnitPrice" >= 0) AND
                        ("Quantity" > 0) AND
                        ("LeadTimeDays" IS NULL OR "LeadTimeDays" >= 0) AND
                        ("AvailableQuantity" IS NULL OR "AvailableQuantity" >= 0) AND
                        "FreightCost" >= 0 AND "DutyCost" >= 0 AND "OtherCost" >= 0 AND
                        ("ReliabilitySnapshot" IS NULL OR ("ReliabilitySnapshot" >= 0 AND "ReliabilitySnapshot" <= 100)));

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'supplier_purchase_orders', 'supplier_purchase_order_lines',
                        'goods_receipts', 'goods_receipt_lines', 'procurement_events', 'procurement_outbox'
                    ]
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', table_name);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
                            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            table_name);
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO nexora_tenant_app', table_name);
                    END LOOP;
                END
                $block$;

                REVOKE UPDATE, DELETE ON TABLE public.procurement_events FROM nexora_tenant_app;
                REVOKE UPDATE, DELETE ON TABLE public.goods_receipts FROM nexora_tenant_app;
                REVOKE UPDATE, DELETE ON TABLE public.goods_receipt_lines FROM nexora_tenant_app;
                REVOKE DELETE ON TABLE public.procurement_outbox FROM nexora_tenant_app;
                REVOKE DELETE ON TABLE public.supplier_purchase_orders FROM nexora_tenant_app;
                REVOKE DELETE ON TABLE public.supplier_purchase_order_lines FROM nexora_tenant_app;

                DO $block$
                DECLARE
                    table_name text;
                    sequence_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY[
                        'supplier_purchase_orders', 'supplier_purchase_order_lines',
                        'goods_receipts', 'goods_receipt_lines',
                        'procurement_events', 'procurement_outbox'
                    ]
                    LOOP
                        sequence_name := pg_get_serial_sequence(format('public.%I', table_name), 'Id');
                        IF sequence_name IS NOT NULL THEN
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sequence_name);
                        END IF;
                    END LOOP;
                END
                $block$;

                CREATE OR REPLACE FUNCTION nexora_reject_procurement_event_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE = 'procurement events are append-only';
                END
                $function$;
                CREATE TRIGGER procurement_events_append_only
                    BEFORE UPDATE OR DELETE ON procurement_events
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_procurement_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS procurement_events_append_only ON procurement_events;
                DROP FUNCTION IF EXISTS nexora_reject_procurement_event_mutation();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_RFQItems_RfqItemId_RfqId",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_RFQ_BusinessUnitId_RfqId",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_SupplierQuotedItems_SupplierQuotedItemId",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_SourcingAwards_Suppliers_SupplierId_BusinessUnitId",
                table: "SourcingAwards");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_Products_ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_RFQItems_RfqItemId_RfqId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_RFQ_BusinessUnitId_RfqId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierQuotedItems_SupplierSolicitations_SupplierSolicitat~",
                table: "SupplierQuotedItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierSolicitations_RFQ_BusinessUnitId_RfqId",
                table: "SupplierSolicitations");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierSolicitations_Suppliers_SupplierId_BusinessUnitId",
                table: "SupplierSolicitations");

            migrationBuilder.DropTable(
                name: "goods_receipt_lines");

            migrationBuilder.DropTable(
                name: "procurement_events");

            migrationBuilder.DropTable(
                name: "procurement_outbox");

            migrationBuilder.DropTable(
                name: "goods_receipts");

            migrationBuilder.DropTable(
                name: "supplier_purchase_order_lines");

            migrationBuilder.DropTable(
                name: "supplier_purchase_orders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SupplierSolicitations_BusinessUnitId_Id",
                table: "SupplierSolicitations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierSolicitations_BusinessUnitId_IdempotencyKey",
                table: "SupplierSolicitations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierSolicitations_SupplierId_BusinessUnitId",
                table: "SupplierSolicitations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId_RfqId_RfqItemId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_RfqItemId_RfqId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "IX_SupplierQuotedItems_SupplierSolicitationId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropIndex(
                name: "UX_SupplierQuotedItems_BU_ResponseKey",
                table: "SupplierQuotedItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourcingAwards_BusinessUnitId_Id",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_BusinessUnitId_IdempotencyKey",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_BusinessUnitId_RfqItemId",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_RfqItemId_RfqId",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_SupplierId_BusinessUnitId",
                table: "SourcingAwards");

            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_SupplierQuotedItemId",
                table: "SourcingAwards");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RFQItems_ID_RFQID",
                table: "RFQItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RFQ_BusinessUnitID_ID",
                table: "RFQ");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "RequestedRfqItemIdsJson",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "AvailableQuantity",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "DutyCost",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "FreightCost",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "LandedUnitCost",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "LeadTimeDays",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "MinimumOrderQuantity",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "OtherCost",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "QuoteRevision",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "ReliabilitySnapshot",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "ResponseIdempotencyKey",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "RfqId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "RfqItemId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "SupplierSolicitationId",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SupplierQuotedItems");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "SourcingAwards");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "SourcingAwards");

            migrationBuilder.DropColumn(
                name: "LandedUnitCost",
                table: "SourcingAwards");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "SourcingAwards");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SourcingAwards");

            migrationBuilder.DropColumn(
                name: "SupplierQuotedItemId",
                table: "SourcingAwards");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SourcingAwards");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId",
                table: "SupplierQuotedItems",
                column: "BusinessUnitId");
        }
    }
}
