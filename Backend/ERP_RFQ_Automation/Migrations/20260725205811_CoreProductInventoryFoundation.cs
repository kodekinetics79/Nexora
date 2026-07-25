using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CoreProductInventoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AllocatedQuantity",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DamagedQuantity",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpiredQuantity",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "ProductId",
                table: "Inventory",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuarantineQuantity",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SafetyStockQuantity",
                table: "Inventory",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "incoming_inventory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: true),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    OrderedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AllocatedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ExpectedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incoming_inventory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incoming_inventory_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incoming_inventory_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_inventory_movements_Inventory_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movements_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movements_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_aliases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedValue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountId = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_aliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_aliases_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_supersessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SupersededProductId = table.Column<long>(type: "bigint", nullable: false),
                    ReplacementProductId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_supersessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_supersessions_Products_ReplacementProductId",
                        column: x => x.ReplacementProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_supersessions_Products_SupersededProductId",
                        column: x => x.SupersededProductId,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            // Link only unambiguous tenant-local part identities. Ambiguous and shared
            // catalog rows remain unresolved for explicit review.
            migrationBuilder.Sql("""
                WITH product_match AS (
                    SELECT "BUID" AS tenant_id,
                           upper(regexp_replace("PartNo", '[^[:alnum:]]', '', 'g')) AS normalized_part,
                           min("ID") AS product_id
                    FROM public."Products"
                    WHERE "BUID" IS NOT NULL AND NULLIF(trim("PartNo"), '') IS NOT NULL
                    GROUP BY "BUID", upper(regexp_replace("PartNo", '[^[:alnum:]]', '', 'g'))
                    HAVING count(*) = 1
                ), inventory_match AS (
                    SELECT "Buid" AS tenant_id,
                           upper(regexp_replace("PartNo", '[^[:alnum:]]', '', 'g')) AS normalized_part,
                           "WarehouseId", min("Id") AS inventory_id
                    FROM public."Inventory"
                    WHERE "Buid" IS NOT NULL AND NULLIF(trim("PartNo"), '') IS NOT NULL
                    GROUP BY "Buid", upper(regexp_replace("PartNo", '[^[:alnum:]]', '', 'g')), "WarehouseId"
                    HAVING count(*) = 1
                )
                UPDATE public."Inventory" inventory
                SET "ProductId" = product_match.product_id
                FROM product_match, inventory_match
                WHERE inventory."Id" = inventory_match.inventory_id
                  AND product_match.tenant_id = inventory_match.tenant_id
                  AND product_match.normalized_part = inventory_match.normalized_part;

                INSERT INTO public.product_aliases
                    ("BusinessUnitId", "ProductId", "Kind", "Value", "NormalizedValue", "AccountId", "IsActive", "CreatedOn", "CreatedBy")
                SELECT "BUID", "ID", 'ManufacturerPartNumber', "PartNo",
                       upper(regexp_replace("PartNo", '[^[:alnum:]]', '', 'g')), NULL, true, now(), 'migration'
                FROM public."Products"
                WHERE "BUID" IS NOT NULL AND NULLIF(trim("PartNo"), '') IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_ProductId",
                table: "Inventory",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "UX_Inventory_BU_Product_Warehouse",
                table: "Inventory",
                columns: new[] { "Buid", "ProductId", "WarehouseId" },
                unique: true,
                filter: "\"ProductId\" IS NOT NULL AND \"WarehouseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_BusinessUnitId_ProductId_ExpectedOn",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "ProductId", "ExpectedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_BusinessUnitId_SourceType_SourceId_Produ~",
                table: "incoming_inventory",
                columns: new[] { "BusinessUnitId", "SourceType", "SourceId", "ProductId", "WarehouseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_ProductId",
                table: "incoming_inventory",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_inventory_WarehouseId",
                table: "incoming_inventory",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_BusinessUnitId_IdempotencyKey",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_BusinessUnitId_ProductId_OccurredOn",
                table: "inventory_movements",
                columns: new[] { "BusinessUnitId", "ProductId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_InventoryId",
                table: "inventory_movements",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ProductId",
                table: "inventory_movements",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_WarehouseId",
                table: "inventory_movements",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_product_aliases_BusinessUnitId_Kind_NormalizedValue_Account~",
                table: "product_aliases",
                columns: new[] { "BusinessUnitId", "Kind", "NormalizedValue", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_aliases_BusinessUnitId_ProductId",
                table: "product_aliases",
                columns: new[] { "BusinessUnitId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_aliases_ProductId",
                table: "product_aliases",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_supersessions_BusinessUnitId_SupersededProductId_Re~",
                table: "product_supersessions",
                columns: new[] { "BusinessUnitId", "SupersededProductId", "ReplacementProductId", "EffectiveOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_supersessions_ReplacementProductId",
                table: "product_supersessions",
                column: "ReplacementProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_supersessions_SupersededProductId",
                table: "product_supersessions",
                column: "SupersededProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_Inventory_Products_ProductId",
                table: "Inventory",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_product_aliases_tenant_identity"
                    ON public.product_aliases ("BusinessUnitId", "Kind", "NormalizedValue", COALESCE("AccountId", 0));

                DO $govern$
                DECLARE governed_table text;
                BEGIN
                    FOREACH governed_table IN ARRAY ARRAY[
                        'incoming_inventory', 'inventory_movements', 'product_aliases', 'product_supersessions'
                    ] LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', governed_table);
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                            EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', governed_table);
                            EXECUTE format('CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)', governed_table);
                            EXECUTE format('GRANT SELECT, INSERT, UPDATE ON public.%I TO nexora_tenant_app', governed_table);
                        END IF;
                    END LOOP;
                END $govern$;

                CREATE OR REPLACE FUNCTION public.nexora_validate_inventory_tenant()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF TG_TABLE_NAME = 'Inventory' THEN
                        IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."Buid") THEN
                            RAISE EXCEPTION 'inventory product must belong to the same tenant';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_TABLE_NAME = 'product_supersessions' THEN
                        IF NOT EXISTS (SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."SupersededProductId" AND p."BUID" = NEW."BusinessUnitId")
                           OR NOT EXISTS (SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."ReplacementProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
                            RAISE EXCEPTION 'supersession products must belong to the same tenant';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'product must belong to the same tenant';
                    END IF;
                    IF TG_TABLE_NAME IN ('incoming_inventory', 'inventory_movements') THEN
                        IF NOT EXISTS (SELECT 1 FROM public."Warehouses" w WHERE w."ID" = NEW."WarehouseId" AND w."BusinessUnitID" = NEW."BusinessUnitId") THEN
                            RAISE EXCEPTION 'warehouse must belong to the same tenant';
                        END IF;
                        IF NEW."InventoryId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."Inventory" i WHERE i."Id" = NEW."InventoryId" AND i."Buid" = NEW."BusinessUnitId") THEN
                            RAISE EXCEPTION 'stock row must belong to the same tenant';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $fn$;

                CREATE TRIGGER inventory_tenant_integrity BEFORE INSERT OR UPDATE OF "Buid", "ProductId" ON public."Inventory"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
                CREATE TRIGGER product_aliases_tenant_integrity BEFORE INSERT OR UPDATE ON public.product_aliases
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
                CREATE TRIGGER product_supersessions_tenant_integrity BEFORE INSERT OR UPDATE ON public.product_supersessions
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
                CREATE TRIGGER incoming_inventory_tenant_integrity BEFORE INSERT OR UPDATE ON public.incoming_inventory
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();
                CREATE TRIGGER inventory_movements_tenant_integrity BEFORE INSERT ON public.inventory_movements
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_validate_inventory_tenant();

                CREATE TRIGGER inventory_movements_immutable BEFORE UPDATE OR DELETE ON public.inventory_movements
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS inventory_movements_immutable ON public.inventory_movements;
                DROP TRIGGER IF EXISTS inventory_movements_tenant_integrity ON public.inventory_movements;
                DROP TRIGGER IF EXISTS incoming_inventory_tenant_integrity ON public.incoming_inventory;
                DROP TRIGGER IF EXISTS product_supersessions_tenant_integrity ON public.product_supersessions;
                DROP TRIGGER IF EXISTS product_aliases_tenant_integrity ON public.product_aliases;
                DROP TRIGGER IF EXISTS inventory_tenant_integrity ON public."Inventory";
                DROP FUNCTION IF EXISTS public.nexora_validate_inventory_tenant();
                DROP INDEX IF EXISTS public."UX_product_aliases_tenant_identity";
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Inventory_Products_ProductId",
                table: "Inventory");

            migrationBuilder.DropTable(
                name: "incoming_inventory");

            migrationBuilder.DropTable(
                name: "inventory_movements");

            migrationBuilder.DropTable(
                name: "product_aliases");

            migrationBuilder.DropTable(
                name: "product_supersessions");

            migrationBuilder.DropIndex(
                name: "IX_Inventory_ProductId",
                table: "Inventory");

            migrationBuilder.DropIndex(
                name: "UX_Inventory_BU_Product_Warehouse",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "AllocatedQuantity",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "DamagedQuantity",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "ExpiredQuantity",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "QuarantineQuantity",
                table: "Inventory");

            migrationBuilder.DropColumn(
                name: "SafetyStockQuantity",
                table: "Inventory");
        }
    }
}
