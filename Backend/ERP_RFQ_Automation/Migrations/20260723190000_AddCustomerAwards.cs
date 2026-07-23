using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerAwards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM public."Orders"
                        WHERE "OrderNo" IS NOT NULL AND trim("OrderNo") <> ''
                        GROUP BY "BusinessUnitID", "OrderNo"
                        HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Customer-award upgrade blocked: duplicate (BusinessUnitID, OrderNo) values exist.'
                            USING ERRCODE = '23505';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM public."Quotes"
                        WHERE "RevisionOfQuoteId" IS NOT NULL
                        GROUP BY "BusinessUnitID", "RevisionOfQuoteId"
                        HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Customer-award upgrade blocked: a quote has multiple successor revisions.'
                            USING ERRCODE = '23505';
                    END IF;
                END
                $block$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_setUOM_BusinessUnitID",
                table: "setUOM");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_BusinessUnitID",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_RevisionOfQuoteId",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "UX_Orders_BU_QuoteID",
                table: "Orders");

            migrationBuilder.AddColumn<long>(
                name: "CustomerAwardID",
                table: "Orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "Orders",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.Sql("""
                UPDATE public."Orders"
                SET "SourceType" = CASE
                    WHEN "QuoteID" IS NOT NULL THEN 'LEGACY_QUOTE'
                    ELSE 'MANUAL'
                END;
                """);

            migrationBuilder.AddColumn<long>(
                name: "CustomerAwardLineAllocationID",
                table: "OrderItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_setUOM_BusinessUnitID_UomID",
                table: "setUOM",
                columns: new[] { "BusinessUnitID", "UomID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Quotes_BusinessUnitID_ID",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Currency_BusinessUnitID_ID",
                table: "Currency",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.CreateTable(
                name: "CustomerPurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    InternalNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalPoNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedExternalPoNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PoDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReceivedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPurchaseOrders", x => x.Id);
                    table.UniqueConstraint("AK_CustomerPurchaseOrders_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CustomerPurchaseOrders_Cancellation", "(\"Status\" = 'CANCELLED' AND length(trim(COALESCE(\"CancellationReason\", ''))) > 0) OR (\"Status\" <> 'CANCELLED' AND \"CancellationReason\" IS NULL)");
                    table.CheckConstraint("CK_CustomerPurchaseOrders_ExternalNumber", "length(trim(\"ExternalPoNumber\")) > 0 AND length(trim(\"NormalizedExternalPoNumber\")) > 0");
                    table.CheckConstraint("CK_CustomerPurchaseOrders_Status", "\"Status\" IN ('DRAFT','CONFIRMED','PARTIALLY_AWARDED','FULLY_AWARDED','CLOSED','CANCELLED')");
                    table.CheckConstraint("CK_CustomerPurchaseOrders_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrders_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrders_CommercialCases_BusinessUnitId_Comme~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrders_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderToCashAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateVersion = table.Column<long>(type: "bigint", nullable: false),
                    CommandType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PreviousState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    NewState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderToCashAuditEvents", x => x.Id);
                    table.CheckConstraint("CK_OrderToCashAudit_Identity", "\"AggregateId\" > 0 AND \"AggregateVersion\" >= 1 AND length(trim(\"AggregateType\")) > 0 AND length(trim(\"CommandType\")) > 0 AND length(trim(\"Actor\")) > 0 AND length(trim(\"IdempotencyKey\")) > 0 AND length(trim(\"CorrelationId\")) > 0");
                    table.ForeignKey(
                        name: "FK_OrderToCashAuditEvents_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderToCashDocumentCounters",
                columns: table => new
                {
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CalendarYear = table.Column<int>(type: "integer", nullable: false),
                    NextNumber = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderToCashDocumentCounters", x => new { x.BusinessUnitId, x.DocumentType, x.CalendarYear });
                    table.CheckConstraint("CK_OrderToCashDocumentCounters_Type", "\"DocumentType\" IN ('CPO','AWD','SO')");
                    table.CheckConstraint("CK_OrderToCashDocumentCounters_Values", "\"CalendarYear\" >= 2000 AND \"NextNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_OrderToCashDocumentCounters_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAwards",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AwardNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustomerPurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ConfirmedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAwards", x => x.Id);
                    table.UniqueConstraint("AK_CustomerAwards_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CustomerAwards_StateMetadata", "(\"Status\" = 'DRAFT' AND \"ConfirmedOn\" IS NULL AND \"ConfirmedBy\" IS NULL AND \"CancelledOn\" IS NULL AND \"CancelledBy\" IS NULL AND \"CancellationReason\" IS NULL) OR (\"Status\" IN ('CONFIRMED','ORDERED') AND \"ConfirmedOn\" IS NOT NULL AND length(trim(COALESCE(\"ConfirmedBy\", ''))) > 0 AND \"CancelledOn\" IS NULL AND \"CancelledBy\" IS NULL AND \"CancellationReason\" IS NULL) OR (\"Status\" = 'CANCELLED' AND \"CancelledOn\" IS NOT NULL AND length(trim(COALESCE(\"CancelledBy\", ''))) > 0 AND length(trim(COALESCE(\"CancellationReason\", ''))) > 0)");
                    table.CheckConstraint("CK_CustomerAwards_Status", "\"Status\" IN ('DRAFT','CONFIRMED','ORDERED','CANCELLED')");
                    table.CheckConstraint("CK_CustomerAwards_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_CustomerAwards_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAwards_CommercialCases_BusinessUnitId_CommercialCas~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAwards_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAwards_CustomerPurchaseOrders_BusinessUnitId_Custom~",
                        columns: x => new { x.BusinessUnitId, x.CustomerPurchaseOrderId },
                        principalTable: "CustomerPurchaseOrders",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAwards_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAwards_Quotes_BusinessUnitId_QuoteId",
                        columns: x => new { x.BusinessUnitId, x.QuoteId },
                        principalTable: "Quotes",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPurchaseOrderLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerPurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalLineReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OrderedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UomId = table.Column<int>(type: "integer", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    LineAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPurchaseOrderLines", x => x.Id);
                    table.UniqueConstraint("AK_CustomerPurchaseOrderLines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CustomerPurchaseOrderLines_Description", "length(trim(\"Description\")) > 0");
                    table.CheckConstraint("CK_CustomerPurchaseOrderLines_ExternalReference", "length(trim(\"ExternalLineReference\")) > 0");
                    table.CheckConstraint("CK_CustomerPurchaseOrderLines_QuantityMoney", "\"OrderedQuantity\" > 0 AND (\"UnitPrice\" IS NULL OR \"UnitPrice\" >= 0) AND (\"LineAmount\" IS NULL OR \"LineAmount\" >= 0)");
                    table.CheckConstraint("CK_CustomerPurchaseOrderLines_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrderLines_CustomerPurchaseOrders_BusinessU~",
                        columns: x => new { x.BusinessUnitId, x.CustomerPurchaseOrderId },
                        principalTable: "CustomerPurchaseOrders",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPurchaseOrderLines_setUOM_BusinessUnitId_UomId",
                        columns: x => new { x.BusinessUnitId, x.UomId },
                        principalTable: "setUOM",
                        principalColumns: new[] { "BusinessUnitID", "UomID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAwardLineAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerAwardId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerPurchaseOrderLineId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteItemId = table.Column<long>(type: "bigint", nullable: false),
                    AwardedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitPriceSnapshot = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountSnapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxSnapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalSnapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAwardLineAllocations", x => x.Id);
                    table.CheckConstraint("CK_CustomerAwardAllocations_QuantityMoney", "\"AwardedQuantity\" > 0 AND \"UnitPriceSnapshot\" >= 0 AND \"DiscountSnapshot\" >= 0 AND \"TaxSnapshot\" >= 0 AND \"TotalSnapshot\" >= 0");
                    table.CheckConstraint("CK_CustomerAwardAllocations_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_CustomerAwardLineAllocations_CustomerAwards_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.CustomerAwardId },
                        principalTable: "CustomerAwards",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerAwardLineAllocations_CustomerPurchaseOrderLines_Bus~",
                        columns: x => new { x.BusinessUnitId, x.CustomerPurchaseOrderLineId },
                        principalTable: "CustomerPurchaseOrderLines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAwardLineAllocations_QuoteItems_QuoteItemId",
                        column: x => x.QuoteItemId,
                        principalTable: "QuoteItems",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Orders_BU_CustomerAwardID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "CustomerAwardID" },
                unique: true,
                filter: "\"CustomerAwardID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_BU_RevisionOfQuoteId",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "RevisionOfQuoteId" },
                unique: true,
                filter: "\"RevisionOfQuoteId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Orders_BU_OrderNo",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "OrderNo" },
                unique: true,
                filter: "\"OrderNo\" IS NOT NULL AND btrim(\"OrderNo\") <> ''");

            migrationBuilder.CreateIndex(
                name: "UX_Orders_BU_LegacyQuoteID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "QuoteID" },
                unique: true,
                filter: "\"QuoteID\" IS NOT NULL AND \"CustomerAwardID\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_SourceIdentity",
                table: "Orders",
                sql: "(\"SourceType\" = 'MANUAL' AND \"QuoteID\" IS NULL AND \"CustomerAwardID\" IS NULL) OR (\"SourceType\" = 'LEGACY_QUOTE' AND \"QuoteID\" IS NOT NULL AND \"CustomerAwardID\" IS NULL) OR (\"SourceType\" = 'CUSTOMER_AWARD' AND \"QuoteID\" IS NOT NULL AND \"CustomerAwardID\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_SourceType",
                table: "Orders",
                sql: "\"SourceType\" IN ('MANUAL','LEGACY_QUOTE','CUSTOMER_AWARD')");

            migrationBuilder.CreateIndex(
                name: "UX_OrderItems_CustomerAwardLineAllocationID",
                table: "OrderItems",
                column: "CustomerAwardLineAllocationID",
                unique: true,
                filter: "\"CustomerAwardLineAllocationID\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwardAllocations_BU_QuoteItem",
                table: "CustomerAwardLineAllocations",
                columns: new[] { "BusinessUnitId", "QuoteItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwardLineAllocations_BusinessUnitId_CustomerAwardId",
                table: "CustomerAwardLineAllocations",
                columns: new[] { "BusinessUnitId", "CustomerAwardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwardLineAllocations_BusinessUnitId_CustomerPurchas~",
                table: "CustomerAwardLineAllocations",
                columns: new[] { "BusinessUnitId", "CustomerPurchaseOrderLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwardLineAllocations_QuoteItemId",
                table: "CustomerAwardLineAllocations",
                column: "QuoteItemId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerAwardAllocations_Award_POLine_QuoteItem",
                table: "CustomerAwardLineAllocations",
                columns: new[] { "CustomerAwardId", "CustomerPurchaseOrderLineId", "QuoteItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwards_BU_PO_Status",
                table: "CustomerAwards",
                columns: new[] { "BusinessUnitId", "CustomerPurchaseOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwards_BU_Quote_Status",
                table: "CustomerAwards",
                columns: new[] { "BusinessUnitId", "QuoteId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwards_BusinessUnitId_CommercialCaseId",
                table: "CustomerAwards",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwards_BusinessUnitId_CurrencyId",
                table: "CustomerAwards",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAwards_CustomerId",
                table: "CustomerAwards",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerAwards_BU_AwardNumber",
                table: "CustomerAwards",
                columns: new[] { "BusinessUnitId", "AwardNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPurchaseOrderLines_BusinessUnitId_UomId",
                table: "CustomerPurchaseOrderLines",
                columns: new[] { "BusinessUnitId", "UomId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPurchaseOrderLines_ProductId",
                table: "CustomerPurchaseOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPurchaseOrderLines_PO_ExternalReference",
                table: "CustomerPurchaseOrderLines",
                columns: new[] { "BusinessUnitId", "CustomerPurchaseOrderId", "ExternalLineReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPurchaseOrders_BU_Case_Status",
                table: "CustomerPurchaseOrders",
                columns: new[] { "BusinessUnitId", "CommercialCaseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPurchaseOrders_BusinessUnitId_CurrencyId",
                table: "CustomerPurchaseOrders",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPurchaseOrders_CustomerId",
                table: "CustomerPurchaseOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPurchaseOrders_BU_Customer_ExternalNumber",
                table: "CustomerPurchaseOrders",
                columns: new[] { "BusinessUnitId", "CustomerId", "NormalizedExternalPoNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPurchaseOrders_BU_InternalNumber",
                table: "CustomerPurchaseOrders",
                columns: new[] { "BusinessUnitId", "InternalNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderToCashAudit_BU_Correlation",
                table: "OrderToCashAuditEvents",
                columns: new[] { "BusinessUnitId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderToCashAudit_BU_Aggregate_Version",
                table: "OrderToCashAuditEvents",
                columns: new[] { "BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_OrderToCashAudit_BU_Command_Idempotency",
                table: "OrderToCashAuditEvents",
                columns: new[] { "BusinessUnitId", "CommandType", "IdempotencyKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_CustomerAwardLineAllocations_CustomerAwardLineAl~",
                table: "OrderItems",
                column: "CustomerAwardLineAllocationID",
                principalTable: "CustomerAwardLineAllocations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_CustomerAwards_BusinessUnitID_CustomerAwardID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "CustomerAwardID" },
                principalTable: "CustomerAwards",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_otc_validate_purchase_order()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    NEW."ExternalPoNumber" := btrim(NEW."ExternalPoNumber");
                    NEW."NormalizedExternalPoNumber" := upper(regexp_replace(NEW."ExternalPoNumber", '[[:space:]]+', ' ', 'g'));
                    IF NEW."NormalizedExternalPoNumber" = '' THEN
                        RAISE EXCEPTION 'external customer PO number must contain letters or digits' USING ERRCODE = '23514';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."Customers" c
                                   WHERE c."ID" = NEW."CustomerId" AND c."BUID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'customer does not belong to the purchase-order tenant' USING ERRCODE = '23503';
                    END IF;
                    IF NOT EXISTS (
                        SELECT 1
                        FROM public."Leads" l
                        JOIN public."RFQ" r ON r."LeadID" = l."ID"
                          AND r."BusinessUnitID" = l."BusinessUnitID"
                        WHERE l."CommercialCaseId" = NEW."CommercialCaseId"
                          AND l."BusinessUnitID" = NEW."BusinessUnitId"
                          AND r."CustomerID" = NEW."CustomerId") THEN
                        RAISE EXCEPTION 'purchase-order customer does not match the commercial case' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_validate_purchase_order_line()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM public."Products" p
                        WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'product does not belong to the purchase-order tenant' USING ERRCODE = '23503';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_validate_award()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE po_record record;
                DECLARE quote_record record;
                BEGIN
                    SELECT p."CommercialCaseId", p."CustomerId", p."CurrencyId", p."Status"
                    INTO po_record
                    FROM public."CustomerPurchaseOrders" p
                    WHERE p."Id" = NEW."CustomerPurchaseOrderId"
                      AND p."BusinessUnitId" = NEW."BusinessUnitId";
                    IF NOT FOUND OR po_record."CommercialCaseId" <> NEW."CommercialCaseId"
                       OR po_record."CustomerId" <> NEW."CustomerId"
                       OR po_record."CurrencyId" <> NEW."CurrencyId" THEN
                        RAISE EXCEPTION 'award identity does not match its customer purchase order' USING ERRCODE = '23514';
                    END IF;
                    IF po_record."Status" IN ('CLOSED', 'CANCELLED') THEN
                        RAISE EXCEPTION 'awards cannot be attached to a closed or cancelled purchase order' USING ERRCODE = '23514';
                    END IF;
                    SELECT l."CommercialCaseId", q."CustomerID", q."CurrencyID"
                    INTO quote_record
                    FROM public."Quotes" q
                    JOIN public."RFQ" r ON r."ID" = q."RFQID" AND r."BusinessUnitID" = q."BusinessUnitID"
                    JOIN public."Leads" l ON l."ID" = r."LeadID" AND l."BusinessUnitID" = q."BusinessUnitID"
                    WHERE q."ID" = NEW."QuoteId" AND q."BusinessUnitID" = NEW."BusinessUnitId";
                    IF NOT FOUND OR quote_record."CommercialCaseId" <> NEW."CommercialCaseId"
                       OR quote_record."CustomerID" <> NEW."CustomerId"
                       OR quote_record."CurrencyID" <> NEW."CurrencyId" THEN
                        RAISE EXCEPTION 'award identity does not match its quote' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_validate_allocation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE award_record record;
                DECLARE po_line_record record;
                DECLARE quote_item_record record;
                BEGIN
                    SELECT a."CustomerPurchaseOrderId", a."QuoteId", a."Status"
                    INTO award_record FROM public."CustomerAwards" a
                    WHERE a."Id" = NEW."CustomerAwardId" AND a."BusinessUnitId" = NEW."BusinessUnitId"
                    FOR UPDATE;
                    SELECT l."CustomerPurchaseOrderId", l."ProductId"
                    INTO po_line_record FROM public."CustomerPurchaseOrderLines" l
                    WHERE l."Id" = NEW."CustomerPurchaseOrderLineId" AND l."BusinessUnitId" = NEW."BusinessUnitId";
                    SELECT qi."QuoteID", qi."ProductID"
                    INTO quote_item_record FROM public."QuoteItems" qi
                    JOIN public."Quotes" q ON q."ID" = qi."QuoteID"
                    WHERE qi."ID" = NEW."QuoteItemId" AND q."BusinessUnitID" = NEW."BusinessUnitId";
                    IF award_record IS NULL OR po_line_record IS NULL OR quote_item_record IS NULL THEN
                        RAISE EXCEPTION 'allocation references do not belong to the award tenant' USING ERRCODE = '23503';
                    END IF;
                    IF award_record."CustomerPurchaseOrderId" <> po_line_record."CustomerPurchaseOrderId"
                       OR award_record."QuoteId" <> quote_item_record."QuoteID" THEN
                        RAISE EXCEPTION 'allocation crosses its award purchase order or quote' USING ERRCODE = '23514';
                    END IF;
                    IF po_line_record."ProductId" IS NOT NULL
                       AND quote_item_record."ProductID" IS DISTINCT FROM po_line_record."ProductId" THEN
                        RAISE EXCEPTION 'allocation product does not match the PO and quote lines' USING ERRCODE = '23514';
                    END IF;
                    IF award_record."Status" <> 'DRAFT' THEN
                        RAISE EXCEPTION 'allocations of finalized awards are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_award_transition_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE exceeded record;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."Status" <> 'DRAFT' THEN
                            RAISE EXCEPTION 'finalized awards are immutable' USING ERRCODE = '55000';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."Status" IN ('CONFIRMED', 'ORDERED') THEN
                        IF (NEW."BusinessUnitId", NEW."AwardNumber", NEW."CustomerPurchaseOrderId", NEW."QuoteId",
                            NEW."CommercialCaseId", NEW."CustomerId", NEW."CurrencyId", NEW."ConfirmedOn", NEW."ConfirmedBy")
                           IS DISTINCT FROM
                           (OLD."BusinessUnitId", OLD."AwardNumber", OLD."CustomerPurchaseOrderId", OLD."QuoteId",
                            OLD."CommercialCaseId", OLD."CustomerId", OLD."CurrencyId", OLD."ConfirmedOn", OLD."ConfirmedBy") THEN
                            RAISE EXCEPTION 'confirmed award identity and confirmation evidence are immutable' USING ERRCODE = '55000';
                        END IF;
                        IF OLD."Status" = 'ORDERED' OR NEW."Status" NOT IN ('ORDERED', 'CANCELLED') THEN
                            RAISE EXCEPTION 'invalid finalized award transition' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."Status" = 'DRAFT' AND NEW."Status" = 'CONFIRMED' THEN
                        IF NOT EXISTS (SELECT 1 FROM public."CustomerAwardLineAllocations" x
                                       WHERE x."BusinessUnitId" = NEW."BusinessUnitId" AND x."CustomerAwardId" = NEW."Id") THEN
                            RAISE EXCEPTION 'an award requires at least one allocation before confirmation' USING ERRCODE = '23514';
                        END IF;

                        PERFORM l."Id" FROM public."CustomerPurchaseOrderLines" l
                        JOIN public."CustomerAwardLineAllocations" x
                          ON x."BusinessUnitId" = l."BusinessUnitId" AND x."CustomerPurchaseOrderLineId" = l."Id"
                        WHERE x."BusinessUnitId" = NEW."BusinessUnitId" AND x."CustomerAwardId" = NEW."Id"
                        ORDER BY l."Id" FOR UPDATE OF l;
                        PERFORM qi."ID" FROM public."QuoteItems" qi
                        JOIN public."CustomerAwardLineAllocations" x ON x."QuoteItemId" = qi."ID"
                        WHERE x."BusinessUnitId" = NEW."BusinessUnitId" AND x."CustomerAwardId" = NEW."Id"
                        ORDER BY qi."ID" FOR UPDATE OF qi;

                        SELECT capacity."Kind", capacity."LineId", capacity."Allocated", capacity."Capacity"
                        INTO exceeded
                        FROM (
                            SELECT 'PO'::text AS "Kind", l."Id" AS "LineId", l."OrderedQuantity" AS "Capacity",
                                   sum(x."AwardedQuantity") AS "Allocated"
                            FROM public."CustomerPurchaseOrderLines" l
                            JOIN public."CustomerAwardLineAllocations" x
                              ON x."BusinessUnitId" = l."BusinessUnitId" AND x."CustomerPurchaseOrderLineId" = l."Id"
                            JOIN public."CustomerAwards" a
                              ON a."BusinessUnitId" = x."BusinessUnitId" AND a."Id" = x."CustomerAwardId"
                            WHERE l."BusinessUnitId" = NEW."BusinessUnitId"
                              AND (a."Status" IN ('CONFIRMED','ORDERED') OR a."Id" = NEW."Id")
                            GROUP BY l."Id", l."OrderedQuantity"
                            HAVING sum(x."AwardedQuantity") > l."OrderedQuantity"
                            UNION ALL
                            SELECT 'QUOTE', qi."ID", qi."Quantity", sum(x."AwardedQuantity")
                            FROM public."QuoteItems" qi
                            JOIN public."CustomerAwardLineAllocations" x ON x."QuoteItemId" = qi."ID"
                            JOIN public."CustomerAwards" a
                              ON a."BusinessUnitId" = x."BusinessUnitId" AND a."Id" = x."CustomerAwardId"
                            WHERE a."BusinessUnitId" = NEW."BusinessUnitId"
                              AND (a."Status" IN ('CONFIRMED','ORDERED') OR a."Id" = NEW."Id")
                            GROUP BY qi."ID", qi."Quantity"
                            HAVING sum(x."AwardedQuantity") > qi."Quantity"
                        ) capacity
                        LIMIT 1;
                        IF FOUND THEN
                            RAISE EXCEPTION '% line % allocation % exceeds capacity %',
                                exceeded."Kind", exceeded."LineId", exceeded."Allocated", exceeded."Capacity"
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_allocation_delete_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."CustomerAwards" a
                               WHERE a."Id" = OLD."CustomerAwardId" AND a."BusinessUnitId" = OLD."BusinessUnitId"
                                 AND a."Status" <> 'DRAFT') THEN
                        RAISE EXCEPTION 'allocations of finalized awards are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN OLD;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_audit_append_only()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION 'order-to-cash audit events are append-only' USING ERRCODE = '55000';
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_order_source_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE award_record record;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."CustomerAwardID" IS NOT NULL THEN
                            RAISE EXCEPTION 'award-derived orders and their source links are immutable' USING ERRCODE = '55000';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF TG_OP = 'UPDATE' AND (NEW."BusinessUnitID", NEW."SourceType", NEW."CustomerAwardID", NEW."QuoteID")
                       IS DISTINCT FROM (OLD."BusinessUnitID", OLD."SourceType", OLD."CustomerAwardID", OLD."QuoteID") THEN
                        RAISE EXCEPTION 'order source links are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."CustomerAwardID" IS NOT NULL THEN
                        SELECT a."QuoteId", a."CustomerId", a."CurrencyId", a."Status" INTO award_record
                        FROM public."CustomerAwards" a
                        WHERE a."Id" = NEW."CustomerAwardID" AND a."BusinessUnitId" = NEW."BusinessUnitID";
                        IF NOT FOUND OR award_record."Status" NOT IN ('CONFIRMED','ORDERED')
                           OR NEW."QuoteID" IS DISTINCT FROM award_record."QuoteId"
                           OR NEW."CustomerID" IS DISTINCT FROM award_record."CustomerId"
                           OR NEW."CurrencyID" IS DISTINCT FROM award_record."CurrencyId" THEN
                            RAISE EXCEPTION 'order does not match its confirmed customer award' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_order_item_source_guard()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE source_record record;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."CustomerAwardLineAllocationID" IS NOT NULL THEN
                            RAISE EXCEPTION 'award-derived order items and their source links are immutable' USING ERRCODE = '55000';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF TG_OP = 'UPDATE' AND NEW."CustomerAwardLineAllocationID"
                       IS DISTINCT FROM OLD."CustomerAwardLineAllocationID" THEN
                        RAISE EXCEPTION 'order-item award source link is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."CustomerAwardLineAllocationID" IS NOT NULL THEN
                        SELECT o."CustomerAwardID", x."CustomerAwardId", qi."ProductID"
                        INTO source_record
                        FROM public."Orders" o
                        JOIN public."CustomerAwardLineAllocations" x ON x."Id" = NEW."CustomerAwardLineAllocationID"
                        JOIN public."QuoteItems" qi ON qi."ID" = x."QuoteItemId"
                        WHERE o."ID" = NEW."OrderID" AND o."BusinessUnitID" = x."BusinessUnitId";
                        IF NOT FOUND OR source_record."CustomerAwardID" IS DISTINCT FROM source_record."CustomerAwardId"
                           OR NEW."ProductID" IS DISTINCT FROM source_record."ProductID" THEN
                            RAISE EXCEPTION 'order item does not match its award allocation' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_otc_purchase_order_validate BEFORE INSERT OR UPDATE ON public."CustomerPurchaseOrders"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_purchase_order();
                CREATE TRIGGER trg_otc_purchase_order_line_validate BEFORE INSERT OR UPDATE ON public."CustomerPurchaseOrderLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_purchase_order_line();
                CREATE TRIGGER trg_otc_award_validate BEFORE INSERT OR UPDATE ON public."CustomerAwards"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_award();
                CREATE TRIGGER trg_otc_award_transition_guard BEFORE UPDATE OR DELETE ON public."CustomerAwards"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_award_transition_guard();
                CREATE TRIGGER trg_otc_allocation_validate BEFORE INSERT OR UPDATE ON public."CustomerAwardLineAllocations"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_validate_allocation();
                CREATE TRIGGER trg_otc_allocation_delete_guard BEFORE DELETE ON public."CustomerAwardLineAllocations"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_allocation_delete_guard();
                CREATE TRIGGER trg_otc_audit_append_only BEFORE UPDATE OR DELETE ON public."OrderToCashAuditEvents"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_audit_append_only();
                CREATE TRIGGER trg_otc_order_source_guard BEFORE INSERT OR UPDATE OR DELETE ON public."Orders"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_order_source_guard();
                CREATE TRIGGER trg_otc_order_item_source_guard BEFORE INSERT OR UPDATE OR DELETE ON public."OrderItems"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_order_item_source_guard();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_write_otc_audit(
                    business_unit_id bigint, aggregate_type text, aggregate_id bigint,
                    aggregate_version bigint, command_type text, previous_state text, new_state text,
                    actor text, reason text, request_hash text, idempotency_key text,
                    result_json jsonb, correlation_id text, occurred_on timestamp without time zone)
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE stored_version bigint;
                DECLARE stored_state text;
                BEGIN
                    IF current_setting('role', true) = 'nexora_tenant_app' AND business_unit_id IS DISTINCT FROM
                       NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint THEN
                        RAISE EXCEPTION 'audit tenant does not match the active tenant' USING ERRCODE = '42501';
                    END IF;
                    IF aggregate_type = 'CUSTOMER_PURCHASE_ORDER' THEN
                        SELECT p."Version", p."Status" INTO stored_version, stored_state
                        FROM public."CustomerPurchaseOrders" p
                        WHERE p."BusinessUnitId" = business_unit_id AND p."Id" = aggregate_id;
                    ELSIF aggregate_type = 'CUSTOMER_AWARD' THEN
                        SELECT a."Version", a."Status" INTO stored_version, stored_state
                        FROM public."CustomerAwards" a
                        WHERE a."BusinessUnitId" = business_unit_id AND a."Id" = aggregate_id;
                    ELSE
                        RAISE EXCEPTION 'unsupported order-to-cash audit aggregate' USING ERRCODE = '23514';
                    END IF;
                    IF NOT FOUND OR stored_version <> aggregate_version OR stored_state <> new_state THEN
                        RAISE EXCEPTION 'audit does not match committed aggregate state' USING ERRCODE = '23514';
                    END IF;
                    IF NOT (
                        (command_type = 'CREATE_PURCHASE_ORDER' AND aggregate_type = 'CUSTOMER_PURCHASE_ORDER'
                            AND aggregate_version = 1 AND previous_state IS NULL) OR
                        (command_type = 'CREATE_AWARD' AND aggregate_type = 'CUSTOMER_AWARD'
                            AND aggregate_version = 1 AND previous_state IS NULL AND new_state = 'DRAFT') OR
                        (command_type = 'CONFIRM_AWARD' AND previous_state = 'DRAFT' AND new_state = 'CONFIRMED') OR
                        (command_type = 'CANCEL_AWARD' AND previous_state IN ('DRAFT','CONFIRMED') AND new_state = 'CANCELLED') OR
                        (command_type = 'CONVERT_AWARD_TO_ORDER' AND previous_state = 'CONFIRMED' AND new_state = 'ORDERED')) THEN
                        RAISE EXCEPTION 'invalid order-to-cash audit transition' USING ERRCODE = '23514';
                    END IF;
                    IF request_hash !~ '^[0-9a-f]{64}$' OR btrim(idempotency_key) = ''
                       OR btrim(actor) = '' OR btrim(correlation_id) = ''
                       OR jsonb_typeof(result_json) <> 'object' THEN
                        RAISE EXCEPTION 'invalid order-to-cash audit evidence' USING ERRCODE = '23514';
                    END IF;
                    INSERT INTO public."OrderToCashAuditEvents"
                        ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "CommandType",
                         "PreviousState", "NewState", "Actor", "Reason", "RequestHash", "IdempotencyKey",
                         "ResultJson", "CorrelationId", "OccurredOn")
                    VALUES (business_unit_id, aggregate_type, aggregate_id, aggregate_version, command_type,
                        previous_state, new_state, actor, reason, request_hash, idempotency_key,
                        result_json, correlation_id, occurred_on);
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_otc_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone := (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
                BEGIN
                    IF TG_TABLE_NAME = 'CustomerPurchaseOrders' THEN
                        IF TG_OP = 'INSERT' THEN
                            event_type := 'order-to-cash.customer-po.created';
                        ELSIF OLD."Status" IS DISTINCT FROM NEW."Status" THEN
                            event_type := 'order-to-cash.customer-po.' || lower(replace(NEW."Status", '_', '-'));
                        ELSE
                            RETURN NEW;
                        END IF;
                        PERFORM public.nexora_write_finance_outbox(
                            NEW."BusinessUnitId", 'CustomerPurchaseOrder', NEW."Id", NEW."Version", event_type,
                            jsonb_build_object('Id', NEW."Id", 'InternalNumber', NEW."InternalNumber",
                                'ExternalPoNumber', NEW."ExternalPoNumber", 'Status', NEW."Status",
                                'CustomerId', NEW."CustomerId", 'CommercialCaseId', NEW."CommercialCaseId",
                                'Version', NEW."Version"), COALESCE(NEW."ModifiedOn", NEW."CreatedOn", event_time));
                    ELSIF TG_TABLE_NAME = 'CustomerAwards' THEN
                        IF TG_OP = 'INSERT' THEN
                            event_type := 'order-to-cash.customer-award.created';
                        ELSIF OLD."Status" IS DISTINCT FROM NEW."Status" THEN
                            event_type := 'order-to-cash.customer-award.' || lower(NEW."Status");
                        ELSE
                            RETURN NEW;
                        END IF;
                        PERFORM public.nexora_write_finance_outbox(
                            NEW."BusinessUnitId", 'CustomerAward', NEW."Id", NEW."Version", event_type,
                            jsonb_build_object('Id', NEW."Id", 'AwardNumber', NEW."AwardNumber",
                                'CustomerPurchaseOrderId', NEW."CustomerPurchaseOrderId", 'QuoteId', NEW."QuoteId",
                                'Status', NEW."Status", 'Version', NEW."Version"),
                            COALESCE(NEW."ModifiedOn", NEW."ConfirmedOn", NEW."CancelledOn", NEW."CreatedOn", event_time));
                    ELSIF TG_TABLE_NAME = 'Orders' AND TG_OP = 'INSERT'
                          AND NEW."SourceType" = 'CUSTOMER_AWARD' THEN
                        PERFORM public.nexora_write_finance_outbox(
                            NEW."BusinessUnitID", 'Order', NEW."ID", 1, 'order-to-cash.customer-award.converted',
                            jsonb_build_object('Id', NEW."ID", 'OrderNo', NEW."OrderNo",
                                'CustomerAwardId', NEW."CustomerAwardID", 'QuoteId', NEW."QuoteID",
                                'SourceType', NEW."SourceType"), COALESCE(NEW."CreatedOn", event_time));
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_otc_purchase_order_outbox AFTER INSERT OR UPDATE ON public."CustomerPurchaseOrders"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();
                CREATE TRIGGER trg_otc_award_outbox AFTER INSERT OR UPDATE ON public."CustomerAwards"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();
                CREATE TRIGGER trg_otc_order_outbox AFTER INSERT ON public."Orders"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_otc_outbox_event();

                REVOKE ALL ON FUNCTION public.nexora_otc_outbox_event() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_otc_audit(bigint, text, bigint, bigint, text, text, text, text, text, text, text, jsonb, text, timestamp without time zone) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_otc_outbox_event() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_write_otc_audit(bigint, text, bigint, bigint, text, text, text, text, text, text, text, jsonb, text, timestamp without time zone) TO nexora_tenant_app;

                DO $block$
                DECLARE tenant_table text;
                BEGIN
                    FOREACH tenant_table IN ARRAY ARRAY[
                        'CustomerPurchaseOrders', 'CustomerPurchaseOrderLines', 'CustomerAwards',
                        'CustomerAwardLineAllocations', 'OrderToCashAuditEvents', 'OrderToCashDocumentCounters'
                    ] LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', tenant_table);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', tenant_table);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
                            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            tenant_table);
                    END LOOP;
                END
                $block$;

                GRANT SELECT, INSERT, UPDATE, DELETE ON public."CustomerPurchaseOrders" TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON public."CustomerPurchaseOrderLines" TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON public."CustomerAwards" TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON public."CustomerAwardLineAllocations" TO nexora_tenant_app;
                GRANT SELECT ON public."OrderToCashAuditEvents" TO nexora_tenant_app;
                REVOKE INSERT, UPDATE, DELETE ON public."OrderToCashAuditEvents" FROM nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE ON public."OrderToCashDocumentCounters" TO nexora_tenant_app;
                REVOKE DELETE ON public."OrderToCashDocumentCounters" FROM nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."CustomerPurchaseOrders_Id_seq" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."CustomerPurchaseOrderLines_Id_seq" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."CustomerAwards_Id_seq" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."CustomerAwardLineAllocations_Id_seq" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."OrderToCashAuditEvents_Id_seq" TO nexora_tenant_app;

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                VALUES ('Customer Awards', 'Governed customer purchase orders, awards, allocations, and conversion',
                        true, 'migration:customer-awards:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;

                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                       'migration:customer-awards:v1', now()
                FROM public."Setup_Master" role
                CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" = 'Customer Awards'
                  AND (upper(COALESCE(role."SetupCode", '')) ~ '(SUPER[ _-]*ADMIN|PLATFORM[ _-]*ADMIN)'
                       OR upper(COALESCE(role."SetupValue", '')) ~ '(SUPER[ _-]*ADMIN|PLATFORM[ _-]*ADMIN)')
                  AND NOT EXISTS (
                      SELECT 1 FROM public."RolePermissions" existing
                      WHERE existing."RoleID" = role."SetupID"
                        AND existing."BusinessUnitID" = role."BusinessUnitID"
                        AND existing."ModuleID" = module."ID");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions"
                WHERE "CreatedBy" = 'migration:customer-awards:v1';
                DELETE FROM public."Module"
                WHERE "CreatedBy" = 'migration:customer-awards:v1'
                  AND "ModuleName" = 'Customer Awards';

                DROP TRIGGER IF EXISTS trg_otc_purchase_order_outbox ON public."CustomerPurchaseOrders";
                DROP TRIGGER IF EXISTS trg_otc_award_outbox ON public."CustomerAwards";
                DROP TRIGGER IF EXISTS trg_otc_order_outbox ON public."Orders";
                DROP TRIGGER IF EXISTS trg_otc_purchase_order_validate ON public."CustomerPurchaseOrders";
                DROP TRIGGER IF EXISTS trg_otc_purchase_order_line_validate ON public."CustomerPurchaseOrderLines";
                DROP TRIGGER IF EXISTS trg_otc_award_validate ON public."CustomerAwards";
                DROP TRIGGER IF EXISTS trg_otc_award_transition_guard ON public."CustomerAwards";
                DROP TRIGGER IF EXISTS trg_otc_allocation_validate ON public."CustomerAwardLineAllocations";
                DROP TRIGGER IF EXISTS trg_otc_allocation_delete_guard ON public."CustomerAwardLineAllocations";
                DROP TRIGGER IF EXISTS trg_otc_audit_append_only ON public."OrderToCashAuditEvents";
                DROP TRIGGER IF EXISTS trg_otc_order_source_guard ON public."Orders";
                DROP TRIGGER IF EXISTS trg_otc_order_item_source_guard ON public."OrderItems";

                DROP FUNCTION IF EXISTS public.nexora_otc_outbox_event();
                DROP FUNCTION IF EXISTS public.nexora_write_otc_audit(bigint, text, bigint, bigint, text, text, text, text, text, text, text, jsonb, text, timestamp without time zone);
                DROP FUNCTION IF EXISTS public.nexora_otc_validate_purchase_order();
                DROP FUNCTION IF EXISTS public.nexora_otc_validate_purchase_order_line();
                DROP FUNCTION IF EXISTS public.nexora_otc_validate_award();
                DROP FUNCTION IF EXISTS public.nexora_otc_validate_allocation();
                DROP FUNCTION IF EXISTS public.nexora_otc_award_transition_guard();
                DROP FUNCTION IF EXISTS public.nexora_otc_allocation_delete_guard();
                DROP FUNCTION IF EXISTS public.nexora_otc_audit_append_only();
                DROP FUNCTION IF EXISTS public.nexora_otc_order_source_guard();
                DROP FUNCTION IF EXISTS public.nexora_otc_order_item_source_guard();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_CustomerAwardLineAllocations_CustomerAwardLineAl~",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_CustomerAwards_BusinessUnitID_CustomerAwardID",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "CustomerAwardLineAllocations");

            migrationBuilder.DropTable(
                name: "OrderToCashAuditEvents");

            migrationBuilder.DropTable(
                name: "OrderToCashDocumentCounters");

            migrationBuilder.DropTable(
                name: "CustomerAwards");

            migrationBuilder.DropTable(
                name: "CustomerPurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "CustomerPurchaseOrders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_setUOM_BusinessUnitID_UomID",
                table: "setUOM");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Quotes_BusinessUnitID_ID",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "UX_Orders_BU_CustomerAwardID",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "UX_Quotes_BU_RevisionOfQuoteId",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "UX_Orders_BU_OrderNo",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "UX_Orders_BU_LegacyQuoteID",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_SourceIdentity",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_SourceType",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "UX_OrderItems_CustomerAwardLineAllocationID",
                table: "OrderItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Currency_BusinessUnitID_ID",
                table: "Currency");

            migrationBuilder.DropColumn(
                name: "CustomerAwardID",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerAwardLineAllocationID",
                table: "OrderItems");

            migrationBuilder.CreateIndex(
                name: "IX_setUOM_BusinessUnitID",
                table: "setUOM",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BusinessUnitID",
                table: "Quotes",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_RevisionOfQuoteId",
                table: "Quotes",
                column: "RevisionOfQuoteId");

            migrationBuilder.CreateIndex(
                name: "UX_Orders_BU_QuoteID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "QuoteID" },
                unique: true,
                filter: "\"QuoteID\" IS NOT NULL");
        }
    }
}
