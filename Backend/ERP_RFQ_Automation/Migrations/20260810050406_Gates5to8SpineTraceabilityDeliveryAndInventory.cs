using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Gates5to8SpineTraceabilityDeliveryAndInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId",
                table: "QuotePriceAttestations");

            migrationBuilder.DropForeignKey(
                name: "FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId",
                table: "QuoteValidityExtensions");

            migrationBuilder.DropTable(
                name: "custom_field_value_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_quote_revisions_Values",
                table: "supplier_quote_revisions");

            migrationBuilder.DropIndex(
                name: "UX_SlaEvents_BU_DedupKey",
                table: "SlaEvents");

            migrationBuilder.DropIndex(
                name: "IX_SetCity_BUID",
                table: "SetCity");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_exception_cases_Source",
                table: "commercial_exception_cases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_exception_cases_SourceIdentity",
                table: "commercial_exception_cases");

            migrationBuilder.DropColumn(
                name: "SupplierInputTaxRecoverable",
                table: "CommercialMatchingPolicies");

            migrationBuilder.AddColumn<string>(
                name: "TaxRegistrationNumber",
                table: "Suppliers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "supplier_quote_revisions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DutyAmount",
                table: "supplier_quote_revisions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OtherAmount",
                table: "supplier_quote_revisions",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ShippedQuantity",
                table: "supplier_purchase_order_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "MaterialLotId",
                table: "stock_reservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuoteDecisionReminderDays",
                table: "SlaPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AcceptanceReference",
                table: "SlaEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeReason",
                table: "SlaEvents",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "SlaEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recipient",
                table: "SlaEvents",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SettledOn",
                table: "SlaEvents",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SlaEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Backfilled SENT, and the code default is CLAIMED — they differ deliberately.
                // The old release path DELETED any claim whose send did not report success, so a
                // row that survived to this migration is one whose send returned true. SENT is the
                // honest reading of history; the empty string EF scaffolds is not a status at all.
                // The default is dropped immediately below so new rows take the C# default instead.
                defaultValue: "SENT");

            migrationBuilder.AddColumn<int>(
                name: "DeliveryCityID",
                table: "Shipments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryStatus",
                table: "Shipments",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                // SCHEDULED here only satisfies NOT NULL at ALTER time. Existing rows are corrected
                // to DISPATCHED immediately below, because every shipment already in the table was
                // written by a path that issues stock in the same transaction — leaving them
                // SCHEDULED would silently un-despatch the entire open order book, and the
                // over-shipment ceiling and delivered-quantity ledger both read this column.
                defaultValue: "SCHEDULED");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryStatusChangedBy",
                table: "Shipments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryStatusChangedOn",
                table: "Shipments",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovalReason",
                table: "Quotes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedBy",
                table: "Quotes",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemovedOn",
                table: "Quotes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxCategory",
                table: "QuoteItems",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true,
                defaultValue: "STANDARD");

            migrationBuilder.AddColumn<string>(
                name: "TaxCategoryReason",
                table: "QuoteItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePercentApplied",
                table: "QuoteItems",
                type: "numeric(9,4)",
                precision: 9,
                scale: 4,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Quantity",
                table: "LeadItems",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<decimal>(
                name: "MaximumLevel",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumLevel",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AccountTeamId",
                table: "Customers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommercialRegistrationNumber",
                table: "Customers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RegionStateId",
                table: "Customers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sector",
                table: "Customers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegistrationNumber",
                table: "Customers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputTaxRatePercent",
                table: "CommercialMatchingPolicies",
                type: "numeric(9,4)",
                precision: 9,
                scale: 4,
                nullable: true,
                defaultValue: 15m);

            migrationBuilder.AddColumn<decimal>(
                name: "SupplierInputTaxRecoverablePercent",
                table: "CommercialMatchingPolicies",
                type: "numeric(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<long>(
                name: "DeliveryProofLineId",
                table: "commercial_exception_cases",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegistrationNumber",
                table: "BusinessUnits",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CurrencyId",
                table: "AgentPolicies",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Shipments_BusinessUnitID_ID",
                table: "Shipments",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ShipmentItems_ID_ShipmentID",
                table: "ShipmentItems",
                columns: new[] { "ID", "ShipmentID" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SetCity_BUID_CityID",
                table: "SetCity",
                columns: new[] { "BUID", "CityID" });

            migrationBuilder.CreateTable(
                name: "delivery_proofs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedByName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReceivedByContact = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivedByPosition = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SignatureEvidenceId = table.Column<long>(type: "bigint", nullable: true),
                    StampEvidenceId = table.Column<long>(type: "bigint", nullable: true),
                    PhotoEvidenceId = table.Column<long>(type: "bigint", nullable: true),
                    GpsLatitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    GpsLongitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    GpsAccuracyMeters = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    GpsCapturedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RecordedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_proofs", x => x.Id);
                    table.UniqueConstraint("AK_delivery_proofs_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_delivery_proofs_Gps", "(\"GpsLatitude\" IS NULL AND \"GpsLongitude\" IS NULL) OR (\"GpsLatitude\" IS NOT NULL AND \"GpsLongitude\" IS NOT NULL AND \"GpsLatitude\" BETWEEN -90 AND 90 AND \"GpsLongitude\" BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_delivery_proofs_GpsAccuracy", "\"GpsAccuracyMeters\" IS NULL OR \"GpsAccuracyMeters\" > 0");
                    table.CheckConstraint("CK_delivery_proofs_GpsCapturedOn", "\"GpsCapturedOn\" IS NULL OR \"GpsLatitude\" IS NOT NULL");
                    table.CheckConstraint("CK_delivery_proofs_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_delivery_proofs_Attachments_PhotoEvidenceId",
                        column: x => x.PhotoEvidenceId,
                        principalTable: "Attachments",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_Attachments_SignatureEvidenceId",
                        column: x => x.SignatureEvidenceId,
                        principalTable: "Attachments",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_Attachments_StampEvidenceId",
                        column: x => x.StampEvidenceId,
                        principalTable: "Attachments",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_CommercialCases_BusinessUnitId_CommercialCa~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_Shipments_BusinessUnitId_ShipmentId",
                        columns: x => new { x.BusinessUnitId, x.ShipmentId },
                        principalTable: "Shipments",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbound_logistics_policies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomsClearanceLeadDays = table.Column<int>(type: "integer", nullable: true),
                    PutawayLeadDays = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbound_logistics_policies", x => x.Id);
                    table.UniqueConstraint("AK_inbound_logistics_policies_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_inbound_logistics_policies_LeadDays", "(\"CustomsClearanceLeadDays\" IS NULL OR (\"CustomsClearanceLeadDays\" >= 0 AND \"CustomsClearanceLeadDays\" <= 365)) AND (\"PutawayLeadDays\" IS NULL OR (\"PutawayLeadDays\" >= 0 AND \"PutawayLeadDays\" <= 365))");
                    table.ForeignKey(
                        name: "FK_inbound_logistics_policies_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_reorder_alerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OnHandQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AvailableQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IncomingQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ProjectedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ThresholdQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ShortfallQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RaisedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    NotifiedCount = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    AcknowledgementReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResolvedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ResolutionReason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_reorder_alerts", x => x.Id);
                    table.CheckConstraint("CK_inventory_reorder_alerts_Acknowledgement", "(\"AcknowledgedOn\" IS NULL AND \"AcknowledgedBy\" IS NULL AND \"AcknowledgementReason\" IS NULL) OR (\"AcknowledgedOn\" IS NOT NULL AND \"AcknowledgedBy\" IS NOT NULL AND \"AcknowledgementReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_inventory_reorder_alerts_Kind", "\"Kind\" IN ('OUT_OF_STOCK','BELOW_MINIMUM','REORDER_POINT','OVERSTOCK')");
                    table.CheckConstraint("CK_inventory_reorder_alerts_Quantities", "\"OnHandQuantity\" >= 0 AND \"IncomingQuantity\" >= 0 AND \"AvailableQuantity\" >= 0 AND \"ThresholdQuantity\" > 0 AND \"ShortfallQuantity\" >= 0");
                    table.CheckConstraint("CK_inventory_reorder_alerts_Status", "\"Status\" IN ('OPEN','ACKNOWLEDGED','RESOLVED')");
                    table.ForeignKey(
                        name: "FK_inventory_reorder_alerts_Inventory_BusinessUnitId_Inventory~",
                        columns: x => new { x.BusinessUnitId, x.InventoryId },
                        principalTable: "Inventory",
                        principalColumns: new[] { "Buid", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MasterDataChangeEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    EntityLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ChangeType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: true),
                    ActorRoleId = table.Column<long>(type: "bigint", nullable: true),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangeSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FieldCount = table.Column<int>(type: "integer", nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterDataChangeEvents", x => x.Id);
                    table.UniqueConstraint("AK_MasterDataChangeEvents_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_MasterDataChangeEvents_ChangeType", "\"ChangeType\" IN ('CREATED', 'UPDATED', 'DELETED')");
                    table.CheckConstraint("CK_MasterDataChangeEvents_EntityType", "\"EntityType\" IN ('Customer', 'Supplier', 'Product')");
                    table.ForeignKey(
                        name: "FK_MasterDataChangeEvents_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "material_lots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TrackingMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderLineId = table.Column<long>(type: "bigint", nullable: false),
                    GoodsReceiptId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CountryOfOrigin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OrderedCountryOfOrigin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ManufacturerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ManufacturerPartNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SupplierBatchReference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ManufactureDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    QuantityReceived = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityConsumed = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false, defaultValue: 0m),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "AVAILABLE"),
                    QuarantineReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    QuarantineReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    QuarantinedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    QuarantinedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReleasedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReleasedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReceivedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_lots", x => x.Id);
                    table.UniqueConstraint("AK_material_lots_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_material_lots_ManufactureBeforeExpiry", "\"ManufactureDate\" IS NULL OR \"ExpiryDate\" IS NULL OR \"ExpiryDate\" >= \"ManufactureDate\"");
                    table.CheckConstraint("CK_material_lots_Quantities", "\"QuantityReceived\" > 0 AND \"QuantityConsumed\" >= 0 AND \"QuantityConsumed\" <= \"QuantityReceived\"");
                    table.CheckConstraint("CK_material_lots_Quarantine", "\"Status\" <> 'QUARANTINED' OR (\"QuarantinedOn\" IS NOT NULL AND \"QuarantinedBy\" IS NOT NULL AND \"QuarantineReasonCode\" IS NOT NULL AND \"QuarantineReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_material_lots_Release", "\"ReleasedOn\" IS NULL OR (\"ReleasedBy\" IS NOT NULL AND \"ReleaseReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_material_lots_SerialQuantity", "\"TrackingMode\" <> 'SERIAL' OR \"QuantityReceived\" = 1");
                    table.CheckConstraint("CK_material_lots_Status", "\"Status\" IN ('AVAILABLE','QUARANTINED')");
                    table.CheckConstraint("CK_material_lots_TrackingMode", "\"TrackingMode\" IN ('LOT','SERIAL','UNTRACKED')");
                    table.ForeignKey(
                        name: "FK_material_lots_CommercialCases_BusinessUnitId_CommercialCase~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_Inventory_BusinessUnitId_InventoryId",
                        columns: x => new { x.BusinessUnitId, x.InventoryId },
                        principalTable: "Inventory",
                        principalColumns: new[] { "Buid", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_Products_BusinessUnitId_ProductId",
                        columns: x => new { x.BusinessUnitId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "BUID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_Suppliers_SupplierId_BusinessUnitId",
                        columns: x => new { x.SupplierId, x.BusinessUnitId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "ID", "BUID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_Warehouses_BusinessUnitId_WarehouseId",
                        columns: x => new { x.BusinessUnitId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_goods_receipts_BusinessUnitId_GoodsReceiptId",
                        columns: x => new { x.BusinessUnitId, x.GoodsReceiptId },
                        principalTable: "goods_receipts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_supplier_purchase_order_lines_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId },
                        principalTable: "supplier_purchase_order_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lots_supplier_purchase_orders_BusinessUnitId_Suppl~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId },
                        principalTable: "supplier_purchase_orders",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ports_of_entry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    City = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ports_of_entry", x => x.Id);
                    table.UniqueConstraint("AK_ports_of_entry_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_ports_of_entry_Kind", "\"Kind\" IN ('SEAPORT','AIRPORT','DRY_PORT','LAND_BORDER')");
                    table.ForeignKey(
                        name: "FK_ports_of_entry_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRemovalRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteId = table.Column<long>(type: "bigint", nullable: false),
                    QuoteNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RemovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RemovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    StatusId = table.Column<long>(type: "bigint", nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    PriceAttestationCount = table.Column<int>(type: "integer", nullable: false),
                    ValidityExtensionCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRemovalRecords", x => x.Id);
                    table.CheckConstraint("CK_QuoteRemovalRecords_Mode", "\"Mode\" IN ('DRAFT_DISCARDED','WITHDRAWN')");
                    table.CheckConstraint("CK_QuoteRemovalRecords_Reason", "trim(\"Reason\") <> ''");
                    table.CheckConstraint("CK_QuoteRemovalRecords_RemovedBy", "trim(\"RemovedBy\") <> ''");
                    table.ForeignKey(
                        name: "FK_QuoteRemovalRecords_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportSubscriptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ReportKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Cadence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    WindowDays = table.Column<int>(type: "integer", nullable: false),
                    Recipients = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastRunOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastRunOutcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    LastRunDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_proof_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryProofId = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentId = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentItemId = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    DespatchedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AcceptedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ExceptionReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExceptionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_proof_lines", x => x.Id);
                    table.UniqueConstraint("AK_delivery_proof_lines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_delivery_proof_lines_Quantities", "\"DespatchedQuantity\" > 0 AND \"AcceptedQuantity\" >= 0 AND \"AcceptedQuantity\" <= \"DespatchedQuantity\"");
                    table.CheckConstraint("CK_delivery_proof_lines_Reason", "\"ExceptionReasonCode\" IS NULL OR \"ExceptionReasonCode\" IN ('SHORT_SHIPMENT','DAMAGED','REJECTED','LOST_IN_TRANSIT')");
                    table.CheckConstraint("CK_delivery_proof_lines_ShortfallHasReason", "(\"AcceptedQuantity\" < \"DespatchedQuantity\" AND \"ExceptionReasonCode\" IS NOT NULL) OR (\"AcceptedQuantity\" = \"DespatchedQuantity\" AND \"ExceptionReasonCode\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_delivery_proof_lines_OrderItems_OrderItemId_OrderId",
                        columns: x => new { x.OrderItemId, x.OrderId },
                        principalTable: "OrderItems",
                        principalColumns: new[] { "ID", "OrderID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proof_lines_Orders_BusinessUnitId_OrderId",
                        columns: x => new { x.BusinessUnitId, x.OrderId },
                        principalTable: "Orders",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proof_lines_ShipmentItems_ShipmentItemId_ShipmentId",
                        columns: x => new { x.ShipmentItemId, x.ShipmentId },
                        principalTable: "ShipmentItems",
                        principalColumns: new[] { "ID", "ShipmentID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proof_lines_Shipments_BusinessUnitId_ShipmentId",
                        columns: x => new { x.BusinessUnitId, x.ShipmentId },
                        principalTable: "Shipments",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proof_lines_delivery_proofs_BusinessUnitId_Deliver~",
                        columns: x => new { x.BusinessUnitId, x.DeliveryProofId },
                        principalTable: "delivery_proofs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MasterDataFieldChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ChangeEventId = table.Column<long>(type: "bigint", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BeforeValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AfterValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterDataFieldChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterDataFieldChanges_MasterDataChangeEvents_BusinessUnitI~",
                        columns: x => new { x.BusinessUnitId, x.ChangeEventId },
                        principalTable: "MasterDataChangeEvents",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "material_lot_certificates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    MaterialLotId = table.Column<long>(type: "bigint", nullable: false),
                    CertificateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CertificateNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IssuedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AttachmentId = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UploadedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_lot_certificates", x => x.Id);
                    table.UniqueConstraint("AK_material_lot_certificates_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_material_lot_certificates_Dates", "\"IssuedOn\" IS NULL OR \"ExpiresOn\" IS NULL OR \"ExpiresOn\" >= \"IssuedOn\"");
                    table.CheckConstraint("CK_material_lot_certificates_Type", "\"CertificateType\" IN ('MANUFACTURER','CERTIFICATE_OF_ORIGIN','CERTIFICATE_OF_CONFORMITY','SASO','SABER','MILL_TEST_REPORT','OTHER')");
                    table.ForeignKey(
                        name: "FK_material_lot_certificates_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lot_certificates_material_lots_BusinessUnitId_Mate~",
                        columns: x => new { x.BusinessUnitId, x.MaterialLotId },
                        principalTable: "material_lots",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "material_lot_consumptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    MaterialLotId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentId = table.Column<long>(type: "bigint", nullable: true),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    NexoraSerial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ComplianceStateAtDeclaration = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ComplianceOverrideReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ComplianceOverrideBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DeclaredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DeclaredBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_lot_consumptions", x => x.Id);
                    table.UniqueConstraint("AK_material_lot_consumptions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_material_lot_consumptions_Compliance", "\"ComplianceStateAtDeclaration\" IN ('COMPLIANT','CERTIFICATE_EXPIRED','NO_CERTIFICATE')");
                    table.CheckConstraint("CK_material_lot_consumptions_Override", "\"ComplianceStateAtDeclaration\" <> 'CERTIFICATE_EXPIRED' OR (\"ComplianceOverrideReason\" IS NOT NULL AND \"ComplianceOverrideBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_material_lot_consumptions_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_material_lot_consumptions_CommercialCases_BusinessUnitId_Co~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lot_consumptions_OrderItems_OrderItemId_OrderId",
                        columns: x => new { x.OrderItemId, x.OrderId },
                        principalTable: "OrderItems",
                        principalColumns: new[] { "ID", "OrderID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lot_consumptions_Orders_BusinessUnitId_OrderId",
                        columns: x => new { x.BusinessUnitId, x.OrderId },
                        principalTable: "Orders",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lot_consumptions_Shipments_BusinessUnitId_Shipment~",
                        columns: x => new { x.BusinessUnitId, x.ShipmentId },
                        principalTable: "Shipments",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_material_lot_consumptions_material_lots_BusinessUnitId_Mate~",
                        columns: x => new { x.BusinessUnitId, x.MaterialLotId },
                        principalTable: "material_lots",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_shipments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Milestone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MilestoneOccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReadyAtFactoryOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DepartedOriginOn = table.Column<DateOnly>(type: "date", nullable: true),
                    InTransitOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ArrivedSaudiPortOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomsClearanceOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReceivedAtWarehouseOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CancelledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PortOfEntryId = table.Column<long>(type: "bigint", nullable: true),
                    CarrierName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TrackingReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    TrackingSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EtaDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EtaUpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    EtaUpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MaterialAvailableDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MaterialAvailableBasisKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MaterialAvailableBasisDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AppliedCustomsClearanceDays = table.Column<int>(type: "integer", nullable: true),
                    AppliedPutawayDays = table.Column<int>(type: "integer", nullable: true),
                    MaterialAvailableComputedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    MaterialAvailableUnavailableReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_supplier_shipments", x => x.Id);
                    table.UniqueConstraint("AK_supplier_shipments_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_shipments_AppliedLeadDays", "(\"AppliedCustomsClearanceDays\" IS NULL OR \"AppliedCustomsClearanceDays\" >= 0) AND (\"AppliedPutawayDays\" IS NULL OR \"AppliedPutawayDays\" >= 0)");
                    table.CheckConstraint("CK_supplier_shipments_ArrivalLocation", "\"ArrivedSaudiPortOn\" IS NULL OR \"PortOfEntryId\" IS NOT NULL");
                    table.CheckConstraint("CK_supplier_shipments_Cancellation", "\"Milestone\" <> 'CANCELLED' OR (\"CancelledOn\" IS NOT NULL AND \"CancellationReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_supplier_shipments_Milestone", "\"Milestone\" IN ('READY_AT_FACTORY','DEPARTED_ORIGIN','IN_TRANSIT','ARRIVED_SAUDI_PORT','CUSTOMS_CLEARANCE','RECEIVED_AT_WAREHOUSE','CANCELLED')");
                    table.CheckConstraint("CK_supplier_shipments_TrackingSource", "\"TrackingSource\" IN ('MANUAL','CARRIER_API')");
                    table.ForeignKey(
                        name: "FK_supplier_shipments_ports_of_entry_BusinessUnitId_PortOfEntr~",
                        columns: x => new { x.BusinessUnitId, x.PortOfEntryId },
                        principalTable: "ports_of_entry",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_shipments_supplier_purchase_orders_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId },
                        principalTable: "supplier_purchase_orders",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_shortfall_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryProofLineId = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentId = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DecidedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_shortfall_decisions", x => x.Id);
                    table.UniqueConstraint("AK_delivery_shortfall_decisions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_delivery_shortfall_decisions_Decision", "\"Decision\" IN ('RESUPPLY','CREDIT')");
                    table.ForeignKey(
                        name: "FK_delivery_shortfall_decisions_Shipments_BusinessUnitId_Shipm~",
                        columns: x => new { x.BusinessUnitId, x.ShipmentId },
                        principalTable: "Shipments",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_shortfall_decisions_delivery_proof_lines_BusinessU~",
                        columns: x => new { x.BusinessUnitId, x.DeliveryProofLineId },
                        principalTable: "delivery_proof_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_shipment_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierShipmentId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierPurchaseOrderLineId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShippedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false, defaultValue: 0m),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_shipment_lines", x => x.Id);
                    table.UniqueConstraint("AK_supplier_shipment_lines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_supplier_shipment_lines_Quantity", "\"ShippedQuantity\" > 0");
                    table.CheckConstraint("CK_supplier_shipment_lines_ReceivedQuantity", "\"ReceivedQuantity\" >= 0 AND \"ReceivedQuantity\" <= \"ShippedQuantity\"");
                    table.ForeignKey(
                        name: "FK_supplier_shipment_lines_Products_BusinessUnitId_ProductId",
                        columns: x => new { x.BusinessUnitId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "BUID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_shipment_lines_supplier_purchase_order_lines_Busin~",
                        columns: x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId },
                        principalTable: "supplier_purchase_order_lines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_shipment_lines_supplier_shipments_BusinessUnitId_S~",
                        columns: x => new { x.BusinessUnitId, x.SupplierShipmentId },
                        principalTable: "supplier_shipments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BU_TaxRegistrationNumber",
                table: "Suppliers",
                columns: new[] { "BUID", "TaxRegistrationNumber" },
                filter: "\"TaxRegistrationNumber\" IS NOT NULL AND \"BUID\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Suppliers_TaxRegistrationNumber",
                table: "Suppliers",
                sql: "\"TaxRegistrationNumber\" IS NULL OR (\"TaxRegistrationNumber\" ~ '^[A-Z0-9./]{5,50}$' AND (\"TaxRegistrationNumber\" !~ '^3[0-9]*$' OR \"TaxRegistrationNumber\" ~ '^3[0-9]{13}3$'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_quote_revisions_Values",
                table: "supplier_quote_revisions",
                sql: "\"RevisionNumber\" > 0 AND \"FreightAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"DutyAmount\" >= 0 AND \"OtherAmount\" >= 0 AND \"DiscountAmount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_purchase_order_lines_ShippedQuantity",
                table: "supplier_purchase_order_lines",
                sql: "\"ShippedQuantity\" >= 0 AND \"ShippedQuantity\" <= \"OrderedQuantity\"");

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_lot",
                table: "stock_reservations",
                columns: new[] { "BusinessUnitId", "MaterialLotId", "Status" },
                filter: "\"MaterialLotId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_SlaEvents_BU_DedupKey",
                table: "SlaEvents",
                columns: new[] { "BusinessUnitId", "DedupKey" },
                unique: true,
                filter: "\"Status\" <> 'RELEASED'");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_BU_DeliveryStatus",
                table: "Shipments",
                columns: new[] { "BusinessUnitID", "DeliveryStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_BusinessUnitID_DeliveryCityID",
                table: "Shipments",
                columns: new[] { "BusinessUnitID", "DeliveryCityID" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_DeliveryStatus",
                table: "Shipments",
                sql: "\"DeliveryStatus\" IN ('SCHEDULED','DISPATCHED','IN_TRANSIT','DELIVERED','DELIVERY_EXCEPTION','CANCELLED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_DeliveryStatusAttribution",
                table: "Shipments",
                sql: "(\"DeliveryStatusChangedOn\" IS NULL AND \"DeliveryStatusChangedBy\" IS NULL) OR (\"DeliveryStatusChangedOn\" IS NOT NULL AND \"DeliveryStatusChangedBy\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BU_RemovedOn",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "RemovedOn" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Quotes_Removal",
                table: "Quotes",
                sql: "(\"RemovedOn\" IS NULL AND \"RemovedBy\" IS NULL AND \"RemovalReason\" IS NULL) OR (\"RemovedOn\" IS NOT NULL AND \"RemovedBy\" IS NOT NULL AND trim(\"RemovedBy\") <> '' AND \"RemovalReason\" IS NOT NULL AND trim(\"RemovalReason\") <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QuoteItems_TaxCategory",
                table: "QuoteItems",
                sql: "\"TaxCategory\" IS NULL OR \"TaxCategory\" IN ('STANDARD', 'ZERO_RATED_EXPORT', 'EXEMPT', 'OUT_OF_SCOPE_RCM')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QuoteItems_TaxCategoryReason",
                table: "QuoteItems",
                sql: "\"TaxCategory\" IS NULL OR \"TaxCategory\" = 'STANDARD' OR (\"TaxCategoryReason\" IS NOT NULL AND \"TaxCategoryReason\" <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Inventory_StockLevels",
                table: "Inventory",
                sql: "(\"MinimumLevel\" IS NULL OR \"MinimumLevel\" >= 0) AND (\"MaximumLevel\" IS NULL OR \"MaximumLevel\" >= 0) AND (\"MinimumLevel\" IS NULL OR \"MaximumLevel\" IS NULL OR \"MaximumLevel\" >= \"MinimumLevel\")");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AccountTeamId",
                table: "Customers",
                column: "AccountTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BU_AccountTeam",
                table: "Customers",
                columns: new[] { "BUID", "AccountTeamId" },
                filter: "\"AccountTeamId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BU_CommercialRegistrationNumber",
                table: "Customers",
                columns: new[] { "BUID", "CommercialRegistrationNumber" },
                filter: "\"CommercialRegistrationNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BU_RegionState",
                table: "Customers",
                columns: new[] { "BUID", "RegionStateId" },
                filter: "\"RegionStateId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BU_TaxRegistrationNumber",
                table: "Customers",
                columns: new[] { "BUID", "TaxRegistrationNumber" },
                filter: "\"TaxRegistrationNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_RegionStateId",
                table: "Customers",
                column: "RegionStateId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_CommercialRegistrationNumber",
                table: "Customers",
                sql: "\"CommercialRegistrationNumber\" IS NULL OR (\"CommercialRegistrationNumber\" ~ '^[A-Z0-9]{5,30}$' AND (\"CommercialRegistrationNumber\" !~ '^[0-9]+$' OR \"CommercialRegistrationNumber\" ~ '^[0-9]{10}$'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_Sector",
                table: "Customers",
                sql: "\"Sector\" IS NULL OR \"Sector\" IN ('GOVERNMENT', 'SEMI_GOVERNMENT', 'PRIVATE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_TaxRegistrationNumber",
                table: "Customers",
                sql: "\"TaxRegistrationNumber\" IS NULL OR (\"TaxRegistrationNumber\" ~ '^[A-Z0-9./]{5,50}$' AND (\"TaxRegistrationNumber\" !~ '^3[0-9]*$' OR \"TaxRegistrationNumber\" ~ '^3[0-9]{13}3$'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CommercialMatchingPolicies_InputTaxRecoverablePercent",
                table: "CommercialMatchingPolicies",
                sql: "CAST(\"SupplierInputTaxRecoverablePercent\" AS numeric) >= 0 AND CAST(\"SupplierInputTaxRecoverablePercent\" AS numeric) <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CommercialMatchingPolicies_OutputTaxRatePercent",
                table: "CommercialMatchingPolicies",
                sql: "\"OutputTaxRatePercent\" IS NULL OR (CAST(\"OutputTaxRatePercent\" AS numeric) >= 0 AND CAST(\"OutputTaxRatePercent\" AS numeric) <= 100)");

            migrationBuilder.CreateIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_DeliveryProofLine~",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "DeliveryProofLineId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_exception_cases_Source",
                table: "commercial_exception_cases",
                sql: "(\"ExceptionType\" = 'UnassignedLead' AND \"UnassignedWorkItemId\" IS NOT NULL AND \"FollowUpTaskId\" IS NULL AND \"DeliveryProofLineId\" IS NULL) OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"FollowUpTaskId\" IS NOT NULL AND \"UnassignedWorkItemId\" IS NULL AND \"DeliveryProofLineId\" IS NULL) OR (\"ExceptionType\" = 'DeliveryShortfall' AND \"DeliveryProofLineId\" IS NOT NULL AND \"FollowUpTaskId\" IS NULL AND \"UnassignedWorkItemId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_exception_cases_SourceIdentity",
                table: "commercial_exception_cases",
                sql: "(\"ExceptionType\" = 'UnassignedLead' AND \"SourceType\" = 'UnassignedWorkItem' AND \"SourceId\" = \"UnassignedWorkItemId\") OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"SourceType\" = 'FollowUpTask' AND \"SourceId\" = \"FollowUpTaskId\") OR (\"ExceptionType\" = 'DeliveryShortfall' AND \"SourceType\" = 'DeliveryProofLine' AND \"SourceId\" = \"DeliveryProofLineId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BusinessUnits_TaxRegistrationNumber",
                table: "BusinessUnits",
                sql: "\"TaxRegistrationNumber\" IS NULL OR (\"TaxRegistrationNumber\" ~ '^[A-Z0-9./]{5,50}$' AND (\"TaxRegistrationNumber\" !~ '^3[0-9]*$' OR \"TaxRegistrationNumber\" ~ '^3[0-9]{13}3$'))");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPolicies_BusinessUnitId_CurrencyId",
                table: "AgentPolicies",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_lines_BU_Order",
                table: "delivery_proof_lines",
                columns: new[] { "BusinessUnitId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_lines_BU_OrderItem",
                table: "delivery_proof_lines",
                columns: new[] { "BusinessUnitId", "OrderItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_lines_BU_Shipment",
                table: "delivery_proof_lines",
                columns: new[] { "BusinessUnitId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_lines_OrderItemId_OrderId",
                table: "delivery_proof_lines",
                columns: new[] { "OrderItemId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_lines_ShipmentItemId_ShipmentId",
                table: "delivery_proof_lines",
                columns: new[] { "ShipmentItemId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "UX_delivery_proof_lines_BU_Proof_ShipmentItem",
                table: "delivery_proof_lines",
                columns: new[] { "BusinessUnitId", "DeliveryProofId", "ShipmentItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_BU_CommercialCase",
                table: "delivery_proofs",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_PhotoEvidenceId",
                table: "delivery_proofs",
                column: "PhotoEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_SignatureEvidenceId",
                table: "delivery_proofs",
                column: "SignatureEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_StampEvidenceId",
                table: "delivery_proofs",
                column: "StampEvidenceId");

            migrationBuilder.CreateIndex(
                name: "UX_delivery_proofs_BU_IdempotencyKey",
                table: "delivery_proofs",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_delivery_proofs_BU_Shipment",
                table: "delivery_proofs",
                columns: new[] { "BusinessUnitId", "ShipmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_shortfall_decisions_BU_Shipment",
                table: "delivery_shortfall_decisions",
                columns: new[] { "BusinessUnitId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "UX_delivery_shortfall_decisions_BU_ProofLine",
                table: "delivery_shortfall_decisions",
                columns: new[] { "BusinessUnitId", "DeliveryProofLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inbound_logistics_policies_BusinessUnitId",
                table: "inbound_logistics_policies",
                column: "BusinessUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_reorder_alerts_status",
                table: "inventory_reorder_alerts",
                columns: new[] { "BusinessUnitId", "Status", "RaisedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_inventory_reorder_alerts_live",
                table: "inventory_reorder_alerts",
                columns: new[] { "BusinessUnitId", "InventoryId", "Kind" },
                unique: true,
                filter: "\"Status\" IN ('OPEN','ACKNOWLEDGED')");

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataChangeEvents_BU_Entity_OccurredOn",
                table: "MasterDataChangeEvents",
                columns: new[] { "BusinessUnitId", "EntityType", "EntityId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataChangeEvents_BU_OccurredOn",
                table: "MasterDataChangeEvents",
                columns: new[] { "BusinessUnitId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataFieldChanges_BU_ChangeEvent",
                table: "MasterDataFieldChanges",
                columns: new[] { "BusinessUnitId", "ChangeEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterDataFieldChanges_BU_FieldName",
                table: "MasterDataFieldChanges",
                columns: new[] { "BusinessUnitId", "FieldName" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_certificates_AttachmentId",
                table: "material_lot_certificates",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_certificates_BU_ExpiresOn",
                table: "material_lot_certificates",
                columns: new[] { "BusinessUnitId", "ExpiresOn" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_certificates_BU_Lot",
                table: "material_lot_certificates",
                columns: new[] { "BusinessUnitId", "MaterialLotId" });

            migrationBuilder.CreateIndex(
                name: "UX_material_lot_certificates_BU_Attachment",
                table: "material_lot_certificates",
                columns: new[] { "BusinessUnitId", "AttachmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_material_lot_certificates_BU_Lot_Type_Number",
                table: "material_lot_certificates",
                columns: new[] { "BusinessUnitId", "MaterialLotId", "CertificateType", "CertificateNumber" },
                unique: true,
                filter: "\"CertificateNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_consumptions_BU_CommercialCase",
                table: "material_lot_consumptions",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_consumptions_BU_Lot",
                table: "material_lot_consumptions",
                columns: new[] { "BusinessUnitId", "MaterialLotId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_consumptions_BU_Order",
                table: "material_lot_consumptions",
                columns: new[] { "BusinessUnitId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_consumptions_BU_OrderItem",
                table: "material_lot_consumptions",
                columns: new[] { "BusinessUnitId", "OrderItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_consumptions_BU_Shipment",
                table: "material_lot_consumptions",
                columns: new[] { "BusinessUnitId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lot_consumptions_OrderItemId_OrderId",
                table: "material_lot_consumptions",
                columns: new[] { "OrderItemId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "UX_material_lot_consumptions_BU_IdempotencyKey",
                table: "material_lot_consumptions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BU_CommercialCase",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BU_Inventory_Status",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "InventoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BU_Product_LotNumber",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "ProductId", "LotNumber" },
                unique: true,
                filter: "\"TrackingMode\" = 'SERIAL'");

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BU_Supplier_Status",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "SupplierId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BU_SupplierPurchaseOrder",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BusinessUnitId_SupplierPurchaseOrderLineId",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_BusinessUnitId_WarehouseId",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_material_lots_SupplierId_BusinessUnitId",
                table: "material_lots",
                columns: new[] { "SupplierId", "BusinessUnitId" });

            migrationBuilder.CreateIndex(
                name: "UX_material_lots_BU_Receipt_Line_LotNumber",
                table: "material_lots",
                columns: new[] { "BusinessUnitId", "GoodsReceiptId", "SupplierPurchaseOrderLineId", "LotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ports_of_entry_BusinessUnitId_Code",
                table: "ports_of_entry",
                columns: new[] { "BusinessUnitId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ports_of_entry_BusinessUnitId_IsActive_Kind",
                table: "ports_of_entry",
                columns: new[] { "BusinessUnitId", "IsActive", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRemovalRecords_BU_Quote",
                table: "QuoteRemovalRecords",
                columns: new[] { "BusinessUnitId", "QuoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRemovalRecords_BU_RemovedOn",
                table: "QuoteRemovalRecords",
                columns: new[] { "BusinessUnitId", "RemovedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSubscriptions_BU_Active_NextRun",
                table: "ReportSubscriptions",
                columns: new[] { "BusinessUnitId", "IsActive", "NextRunOn" });

            migrationBuilder.CreateIndex(
                name: "UX_ReportSubscriptions_BU_Report_Cadence_Format",
                table: "ReportSubscriptions",
                columns: new[] { "BusinessUnitId", "ReportKey", "Cadence", "Format" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipment_lines_BusinessUnitId_ProductId",
                table: "supplier_shipment_lines",
                columns: new[] { "BusinessUnitId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipment_lines_BusinessUnitId_SupplierPurchaseOrde~",
                table: "supplier_shipment_lines",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipment_lines_BusinessUnitId_SupplierShipmentId_S~",
                table: "supplier_shipment_lines",
                columns: new[] { "BusinessUnitId", "SupplierShipmentId", "SupplierPurchaseOrderLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipments_BusinessUnitId_IdempotencyKey",
                table: "supplier_shipments",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipments_BusinessUnitId_MaterialAvailableDate",
                table: "supplier_shipments",
                columns: new[] { "BusinessUnitId", "MaterialAvailableDate" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipments_BusinessUnitId_Milestone_EtaDate",
                table: "supplier_shipments",
                columns: new[] { "BusinessUnitId", "Milestone", "EtaDate" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipments_BusinessUnitId_PortOfEntryId",
                table: "supplier_shipments",
                columns: new[] { "BusinessUnitId", "PortOfEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipments_BusinessUnitId_ShipmentNumber",
                table: "supplier_shipments",
                columns: new[] { "BusinessUnitId", "ShipmentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_shipments_BusinessUnitId_SupplierPurchaseOrderId",
                table: "supplier_shipments",
                columns: new[] { "BusinessUnitId", "SupplierPurchaseOrderId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentPolicies_Currency_BusinessUnitId_CurrencyId",
                table: "AgentPolicies",
                columns: new[] { "BusinessUnitId", "CurrencyId" },
                principalTable: "Currency",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_commercial_exception_cases_delivery_proof_lines_BusinessUni~",
                table: "commercial_exception_cases",
                columns: new[] { "BusinessUnitId", "DeliveryProofLineId" },
                principalTable: "delivery_proof_lines",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_AccountTeam",
                table: "Customers",
                column: "AccountTeamId",
                principalTable: "Teams",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_RegionState",
                table: "Customers",
                column: "RegionStateId",
                principalTable: "SetState",
                principalColumn: "StateID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId",
                table: "QuotePriceAttestations",
                columns: new[] { "BusinessUnitId", "QuoteId" },
                principalTable: "Quotes",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId",
                table: "QuoteValidityExtensions",
                columns: new[] { "BusinessUnitId", "QuoteId" },
                principalTable: "Quotes",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_SetCity_BusinessUnitID_DeliveryCityID",
                table: "Shipments",
                columns: new[] { "BusinessUnitID", "DeliveryCityID" },
                principalTable: "SetCity",
                principalColumns: new[] { "BUID", "CityID" },
                onDelete: ReferentialAction.Restrict);

            // Existing shipments predate this column and were every one of them written by a path
            // that issues stock in the same transaction. SCHEDULED above only satisfied NOT NULL.
            migrationBuilder.Sql("""UPDATE public."Shipments" SET "DeliveryStatus" = 'DISPATCHED';""");

            // The backfill above is history; new rows must take the C# default (CLAIMED), so the
            // database default is removed rather than left to contradict the entity.
            migrationBuilder.Sql("""ALTER TABLE public."SlaEvents" ALTER COLUMN "Status" DROP DEFAULT;""");

            // Row-level security for every tenant-owned table added across Gates 5 to 8.
            //
            // A policy alone is not the control. The schema is deny-by-default — an earlier
            // migration revoked the schema default privileges — so a table with a policy and no
            // GRANT is not "more isolated", it is a table nobody can read: PostgreSQL raises 42501
            // on the privilege check before it ever evaluates a row predicate. Three tables shipped
            // exactly that defect in a single gate and every test passed, because the isolation
            // test asserted only that a policy existed. Both halves are here, and the PostgreSQL
            // lane now asserts both directions.
            //
            // Every policy names "BusinessUnitId" as actually spelled. The legacy "BusinessUnitID"
            // belongs to Quotes, Shipments, RFQ and Orders; a policy naming the wrong one compiles,
            // deploys, and silently matches no row.
            migrationBuilder.Sql("""
                ALTER TABLE public."delivery_proofs" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."delivery_proofs" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."delivery_proofs" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."delivery_proof_lines" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."delivery_proof_lines" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."delivery_proof_lines" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."delivery_shortfall_decisions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."delivery_shortfall_decisions" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."delivery_shortfall_decisions" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."inbound_logistics_policies" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."inbound_logistics_policies" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."inbound_logistics_policies" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."ports_of_entry" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ports_of_entry" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."ports_of_entry" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."supplier_shipments" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."supplier_shipments" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."supplier_shipments" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."supplier_shipment_lines" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."supplier_shipment_lines" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."supplier_shipment_lines" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."inventory_reorder_alerts" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."inventory_reorder_alerts" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."inventory_reorder_alerts" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."MasterDataChangeEvents" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."MasterDataChangeEvents" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."MasterDataChangeEvents" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."MasterDataFieldChanges" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."MasterDataFieldChanges" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."MasterDataFieldChanges" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."QuoteRemovalRecords" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."QuoteRemovalRecords" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."QuoteRemovalRecords" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."ReportSubscriptions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ReportSubscriptions" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."ReportSubscriptions" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."material_lots" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."material_lots" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."material_lots" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."material_lot_certificates" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."material_lot_certificates" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."material_lot_certificates" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."material_lot_consumptions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."material_lot_consumptions" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."material_lot_consumptions" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                    public."delivery_proofs",
                    public."delivery_proof_lines",
                    public."delivery_shortfall_decisions",
                    public."inbound_logistics_policies",
                    public."ports_of_entry",
                    public."supplier_shipments",
                    public."supplier_shipment_lines",
                    public."inventory_reorder_alerts",
                    public."MasterDataChangeEvents",
                    public."MasterDataFieldChanges",
                    public."QuoteRemovalRecords",
                    public."ReportSubscriptions",
                    public."material_lots",
                    public."material_lot_certificates",
                    public."material_lot_consumptions"
                TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentPolicies_Currency_BusinessUnitId_CurrencyId",
                table: "AgentPolicies");

            migrationBuilder.DropForeignKey(
                name: "FK_commercial_exception_cases_delivery_proof_lines_BusinessUni~",
                table: "commercial_exception_cases");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_AccountTeam",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_RegionState",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId",
                table: "QuotePriceAttestations");

            migrationBuilder.DropForeignKey(
                name: "FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId",
                table: "QuoteValidityExtensions");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_SetCity_BusinessUnitID_DeliveryCityID",
                table: "Shipments");

            migrationBuilder.DropTable(
                name: "delivery_shortfall_decisions");

            migrationBuilder.DropTable(
                name: "inbound_logistics_policies");

            migrationBuilder.DropTable(
                name: "inventory_reorder_alerts");

            migrationBuilder.DropTable(
                name: "MasterDataFieldChanges");

            migrationBuilder.DropTable(
                name: "material_lot_certificates");

            migrationBuilder.DropTable(
                name: "material_lot_consumptions");

            migrationBuilder.DropTable(
                name: "QuoteRemovalRecords");

            migrationBuilder.DropTable(
                name: "ReportSubscriptions");

            migrationBuilder.DropTable(
                name: "supplier_shipment_lines");

            migrationBuilder.DropTable(
                name: "delivery_proof_lines");

            migrationBuilder.DropTable(
                name: "MasterDataChangeEvents");

            migrationBuilder.DropTable(
                name: "material_lots");

            migrationBuilder.DropTable(
                name: "supplier_shipments");

            migrationBuilder.DropTable(
                name: "delivery_proofs");

            migrationBuilder.DropTable(
                name: "ports_of_entry");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BU_TaxRegistrationNumber",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Suppliers_TaxRegistrationNumber",
                table: "Suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_quote_revisions_Values",
                table: "supplier_quote_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_purchase_order_lines_ShippedQuantity",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_stock_reservations_lot",
                table: "stock_reservations");

            migrationBuilder.DropIndex(
                name: "UX_SlaEvents_BU_DedupKey",
                table: "SlaEvents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Shipments_BusinessUnitID_ID",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_BU_DeliveryStatus",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_BusinessUnitID_DeliveryCityID",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipments_DeliveryStatus",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipments_DeliveryStatusAttribution",
                table: "Shipments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ShipmentItems_ID_ShipmentID",
                table: "ShipmentItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SetCity_BUID_CityID",
                table: "SetCity");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_BU_RemovedOn",
                table: "Quotes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Quotes_Removal",
                table: "Quotes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QuoteItems_TaxCategory",
                table: "QuoteItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QuoteItems_TaxCategoryReason",
                table: "QuoteItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Inventory_StockLevels",
                table: "Inventory");

            migrationBuilder.DropIndex(
                name: "IX_Customers_AccountTeamId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_BU_AccountTeam",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_BU_CommercialRegistrationNumber",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_BU_RegionState",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_BU_TaxRegistrationNumber",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_RegionStateId",
                table: "Customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_CommercialRegistrationNumber",
                table: "Customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_Sector",
                table: "Customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_TaxRegistrationNumber",
                table: "Customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CommercialMatchingPolicies_InputTaxRecoverablePercent",
                table: "CommercialMatchingPolicies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CommercialMatchingPolicies_OutputTaxRatePercent",
                table: "CommercialMatchingPolicies");

            migrationBuilder.DropIndex(
                name: "IX_commercial_exception_cases_BusinessUnitId_DeliveryProofLine~",
                table: "commercial_exception_cases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_exception_cases_Source",
                table: "commercial_exception_cases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_commercial_exception_cases_SourceIdentity",
                table: "commercial_exception_cases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BusinessUnits_TaxRegistrationNumber",
                table: "BusinessUnits");

            migrationBuilder.DropIndex(
                name: "IX_AgentPolicies_BusinessUnitId_CurrencyId",
                table: "AgentPolicies");

            migrationBuilder.DropColumn(
                name: "TaxRegistrationNumber",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "supplier_quote_revisions");

            migrationBuilder.DropColumn(
                name: "DutyAmount",
                table: "supplier_quote_revisions");

            migrationBuilder.DropColumn(
                name: "OtherAmount",
                table: "supplier_quote_revisions");

            migrationBuilder.DropColumn(
                name: "ShippedQuantity",
                table: "supplier_purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "MaterialLotId",
                table: "stock_reservations");

            migrationBuilder.DropColumn(
                name: "QuoteDecisionReminderDays",
                table: "SlaPolicies");

            migrationBuilder.DropColumn(
                name: "AcceptanceReference",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "OutcomeReason",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "Recipient",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "SettledOn",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SlaEvents");

            migrationBuilder.DropColumn(
                name: "DeliveryCityID",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "DeliveryStatusChangedBy",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "DeliveryStatusChangedOn",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "RemovalReason",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "RemovedBy",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "RemovedOn",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "TaxCategory",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "TaxCategoryReason",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "TaxRatePercentApplied",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "MaximumLevel",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "MinimumLevel",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "AccountTeamId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CommercialRegistrationNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "RegionStateId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Sector",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TaxRegistrationNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "OutputTaxRatePercent",
                table: "CommercialMatchingPolicies");

            migrationBuilder.DropColumn(
                name: "SupplierInputTaxRecoverablePercent",
                table: "CommercialMatchingPolicies");

            migrationBuilder.DropColumn(
                name: "DeliveryProofLineId",
                table: "commercial_exception_cases");

            migrationBuilder.DropColumn(
                name: "TaxRegistrationNumber",
                table: "BusinessUnits");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "AgentPolicies");

            migrationBuilder.AlterColumn<int>(
                name: "Quantity",
                table: "LeadItems",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupplierInputTaxRecoverable",
                table: "CommercialMatchingPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "custom_field_value_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ChangeType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CustomFieldValueId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_value_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_value_history_custom_field_values_BusinessUnit~",
                        columns: x => new { x.BusinessUnitId, x.CustomFieldValueId },
                        principalTable: "custom_field_values",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_quote_revisions_Values",
                table: "supplier_quote_revisions",
                sql: "\"RevisionNumber\" > 0 AND \"FreightAmount\" >= 0 AND \"TaxAmount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "UX_SlaEvents_BU_DedupKey",
                table: "SlaEvents",
                columns: new[] { "BusinessUnitId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SetCity_BUID",
                table: "SetCity",
                column: "BUID");

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_exception_cases_Source",
                table: "commercial_exception_cases",
                sql: "(\"ExceptionType\" = 'UnassignedLead' AND \"UnassignedWorkItemId\" IS NOT NULL AND \"FollowUpTaskId\" IS NULL) OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"FollowUpTaskId\" IS NOT NULL AND \"UnassignedWorkItemId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_commercial_exception_cases_SourceIdentity",
                table: "commercial_exception_cases",
                sql: "(\"ExceptionType\" = 'UnassignedLead' AND \"SourceType\" = 'UnassignedWorkItem' AND \"SourceId\" = \"UnassignedWorkItemId\") OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"SourceType\" = 'FollowUpTask' AND \"SourceId\" = \"FollowUpTaskId\")");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_value_history_BusinessUnitId_CustomFieldValueI~",
                table: "custom_field_value_history",
                columns: new[] { "BusinessUnitId", "CustomFieldValueId", "ChangedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_value_history_BusinessUnitId_IdempotencyKey",
                table: "custom_field_value_history",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_QuotePriceAttestations_Quotes_BusinessUnitId_QuoteId",
                table: "QuotePriceAttestations",
                columns: new[] { "BusinessUnitId", "QuoteId" },
                principalTable: "Quotes",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_QuoteValidityExtensions_Quotes_BusinessUnitId_QuoteId",
                table: "QuoteValidityExtensions",
                columns: new[] { "BusinessUnitId", "QuoteId" },
                principalTable: "Quotes",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
