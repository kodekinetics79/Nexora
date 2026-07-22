using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class OperationalizeGovernedCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_value_history_custom_field_values_CustomFieldV~",
                table: "custom_field_value_history");

            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_values_custom_field_definitions_DefinitionId",
                table: "custom_field_values");

            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_values_custom_field_records_RecordId",
                table: "custom_field_values");

            migrationBuilder.DropIndex(
                name: "IX_custom_field_values_DefinitionId",
                table: "custom_field_values");

            migrationBuilder.DropIndex(
                name: "IX_custom_field_value_history_CustomFieldValueId",
                table: "custom_field_value_history");

            migrationBuilder.AddColumn<string>(
                name: "EditAccess",
                table: "custom_field_versions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViewAccess",
                table: "custom_field_versions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "custom_field_values",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "custom_field_value_history",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "custom_field_value_history",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "custom_field_definitions",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE custom_field_versions
                SET "ViewAccess" = CASE WHEN "IsSensitive" THEN 'ManagerOrAdmin' ELSE 'TenantUser' END,
                    "EditAccess" = CASE WHEN "IsSensitive" THEN 'ManagerOrAdmin' ELSE 'TenantUser' END
                WHERE "ViewAccess" IS NULL OR "EditAccess" IS NULL;

                UPDATE custom_field_values SET "Version" = 1 WHERE "Version" IS NULL;
                UPDATE custom_field_definitions SET "Version" = 1 WHERE "Version" IS NULL;

                UPDATE custom_field_value_history
                SET "IdempotencyKey" = 'legacy:' || "Id"::text,
                    "RequestHash" = repeat('0', 64)
                WHERE "IdempotencyKey" IS NULL OR "RequestHash" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "EditAccess", table: "custom_field_versions",
                type: "character varying(24)", maxLength: 24, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(24)", oldMaxLength: 24, oldNullable: true);
            migrationBuilder.AlterColumn<string>(
                name: "ViewAccess", table: "custom_field_versions",
                type: "character varying(24)", maxLength: 24, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(24)", oldMaxLength: 24, oldNullable: true);
            migrationBuilder.AlterColumn<long>(
                name: "Version", table: "custom_field_values", type: "bigint", nullable: false,
                oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey", table: "custom_field_value_history",
                type: "character varying(160)", maxLength: 160, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(160)", oldMaxLength: 160, oldNullable: true);
            migrationBuilder.AlterColumn<string>(
                name: "RequestHash", table: "custom_field_value_history",
                type: "character varying(64)", maxLength: 64, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true);
            migrationBuilder.AlterColumn<long>(
                name: "Version", table: "custom_field_definitions", type: "bigint", nullable: false,
                oldClrType: typeof(long), oldType: "bigint", oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_custom_field_versions_DefinitionId_VersionNumber",
                table: "custom_field_versions",
                columns: new[] { "DefinitionId", "VersionNumber" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_custom_field_values_BusinessUnitId_Id",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_custom_field_records_BusinessUnitId_Id",
                table: "custom_field_records",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_custom_field_definitions_BusinessUnitId_Id",
                table: "custom_field_definitions",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_BusinessUnitId_RecordId",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_DefinitionId_DefinitionVersion",
                table: "custom_field_values",
                columns: new[] { "DefinitionId", "DefinitionVersion" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_custom_field_values_reference_pair",
                table: "custom_field_values",
                sql: "(\"ReferenceType\" IS NULL AND \"ReferenceId\" IS NULL) OR\n(\"ReferenceType\" IS NOT NULL AND \"ReferenceId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_custom_field_values_typed_value",
                table: "custom_field_values",
                sql: "(CASE WHEN \"TextValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"IntegerValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"DecimalValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"BooleanValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"DateValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"TimestampValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"JsonValue\" IS NULL THEN 0 ELSE 1 END +\n CASE WHEN \"ReferenceId\" IS NULL THEN 0 ELSE 1 END) <= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_value_history_custom_field_values_BusinessUnit~",
                table: "custom_field_value_history",
                columns: new[] { "BusinessUnitId", "CustomFieldValueId" },
                principalTable: "custom_field_values",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_values_custom_field_definitions_BusinessUnitId~",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "DefinitionId" },
                principalTable: "custom_field_definitions",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_values_custom_field_records_BusinessUnitId_Rec~",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "RecordId" },
                principalTable: "custom_field_records",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_values_custom_field_versions_DefinitionId_Defi~",
                table: "custom_field_values",
                columns: new[] { "DefinitionId", "DefinitionVersion" },
                principalTable: "custom_field_versions",
                principalColumns: new[] { "DefinitionId", "VersionNumber" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_protect_custom_field_governance()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' OR TG_TABLE_NAME IN
                        ('custom_field_versions', 'custom_field_options', 'custom_field_rules',
                         'custom_field_dependencies', 'custom_field_value_history') THEN
                        RAISE EXCEPTION 'Governed custom-field records cannot be modified or deleted.';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER "TR_CustomFieldDefinitions_NoDelete"
                    BEFORE DELETE ON custom_field_definitions FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldRecords_NoDelete"
                    BEFORE DELETE ON custom_field_records FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldValues_NoDelete"
                    BEFORE DELETE ON custom_field_values FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldVersions_Immutable"
                    BEFORE UPDATE OR DELETE ON custom_field_versions FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldOptions_Immutable"
                    BEFORE UPDATE OR DELETE ON custom_field_options FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldRules_Immutable"
                    BEFORE UPDATE OR DELETE ON custom_field_rules FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldDependencies_Immutable"
                    BEFORE UPDATE OR DELETE ON custom_field_dependencies FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                CREATE TRIGGER "TR_CustomFieldValueHistory_Immutable"
                    BEFORE UPDATE OR DELETE ON custom_field_value_history FOR EACH ROW
                    EXECUTE FUNCTION nexora_protect_custom_field_governance();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_CustomFieldDefinitions_NoDelete" ON custom_field_definitions;
                DROP TRIGGER IF EXISTS "TR_CustomFieldRecords_NoDelete" ON custom_field_records;
                DROP TRIGGER IF EXISTS "TR_CustomFieldValues_NoDelete" ON custom_field_values;
                DROP TRIGGER IF EXISTS "TR_CustomFieldVersions_Immutable" ON custom_field_versions;
                DROP TRIGGER IF EXISTS "TR_CustomFieldOptions_Immutable" ON custom_field_options;
                DROP TRIGGER IF EXISTS "TR_CustomFieldRules_Immutable" ON custom_field_rules;
                DROP TRIGGER IF EXISTS "TR_CustomFieldDependencies_Immutable" ON custom_field_dependencies;
                DROP TRIGGER IF EXISTS "TR_CustomFieldValueHistory_Immutable" ON custom_field_value_history;
                DROP FUNCTION IF EXISTS nexora_protect_custom_field_governance();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_value_history_custom_field_values_BusinessUnit~",
                table: "custom_field_value_history");

            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_values_custom_field_definitions_BusinessUnitId~",
                table: "custom_field_values");

            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_values_custom_field_records_BusinessUnitId_Rec~",
                table: "custom_field_values");

            migrationBuilder.DropForeignKey(
                name: "FK_custom_field_values_custom_field_versions_DefinitionId_Defi~",
                table: "custom_field_values");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_custom_field_versions_DefinitionId_VersionNumber",
                table: "custom_field_versions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_custom_field_values_BusinessUnitId_Id",
                table: "custom_field_values");

            migrationBuilder.DropIndex(
                name: "IX_custom_field_values_BusinessUnitId_RecordId",
                table: "custom_field_values");

            migrationBuilder.DropIndex(
                name: "IX_custom_field_values_DefinitionId_DefinitionVersion",
                table: "custom_field_values");

            migrationBuilder.DropCheckConstraint(
                name: "CK_custom_field_values_reference_pair",
                table: "custom_field_values");

            migrationBuilder.DropCheckConstraint(
                name: "CK_custom_field_values_typed_value",
                table: "custom_field_values");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_custom_field_records_BusinessUnitId_Id",
                table: "custom_field_records");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_custom_field_definitions_BusinessUnitId_Id",
                table: "custom_field_definitions");

            migrationBuilder.DropColumn(
                name: "EditAccess",
                table: "custom_field_versions");

            migrationBuilder.DropColumn(
                name: "ViewAccess",
                table: "custom_field_versions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "custom_field_values");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "custom_field_value_history");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "custom_field_value_history");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "custom_field_definitions");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_DefinitionId",
                table: "custom_field_values",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_value_history_CustomFieldValueId",
                table: "custom_field_value_history",
                column: "CustomFieldValueId");

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_value_history_custom_field_values_CustomFieldV~",
                table: "custom_field_value_history",
                column: "CustomFieldValueId",
                principalTable: "custom_field_values",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_values_custom_field_definitions_DefinitionId",
                table: "custom_field_values",
                column: "DefinitionId",
                principalTable: "custom_field_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_custom_field_values_custom_field_records_RecordId",
                table: "custom_field_values",
                column: "RecordId",
                principalTable: "custom_field_records",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
