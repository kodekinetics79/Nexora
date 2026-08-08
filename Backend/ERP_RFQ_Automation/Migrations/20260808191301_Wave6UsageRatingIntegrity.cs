using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Wave6UsageRatingIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_RateCardId",
                schema: "platform",
                table: "UsageEvents",
                column: "RateCardId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_RateCardLineId",
                schema: "platform",
                table: "UsageEvents",
                column: "RateCardLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_UsageEvents_RateCardLines_RateCardLineId",
                schema: "platform",
                table: "UsageEvents",
                column: "RateCardLineId",
                principalSchema: "platform",
                principalTable: "RateCardLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UsageEvents_RateCards_RateCardId",
                schema: "platform",
                table: "UsageEvents",
                column: "RateCardId",
                principalSchema: "platform",
                principalTable: "RateCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform.nexora_guard_usage_event_insert()
                RETURNS trigger LANGUAGE plpgsql AS $guard$
                DECLARE
                    original platform."UsageEvents"%ROWTYPE;
                    prior_quantity numeric;
                    prior_cost numeric;
                    prior_rated numeric;
                    card platform."RateCards"%ROWTYPE;
                    line platform."RateCardLines"%ROWTYPE;
                    expected_meter text;
                BEGIN
                    IF NEW."Kind"='Consumption' THEN
                        IF NEW."OverageQuantity"<>GREATEST(NEW."Quantity"-NEW."AllowanceApplied",0) THEN
                            RAISE EXCEPTION 'usage overage does not reconcile';
                        END IF;
                    ELSE
                        SELECT * INTO original FROM platform."UsageEvents"
                         WHERE "TenantId"=NEW."TenantId" AND "UsageEventId"=NEW."AdjustsUsageEventId" FOR KEY SHARE;
                        IF NOT FOUND OR original."Kind"<>'Consumption' THEN RAISE EXCEPTION 'adjustment must reference same-tenant consumption'; END IF;
                        IF NEW."EventType"<>original."EventType" OR NEW."Unit"<>original."Unit" OR NEW."Currency"<>original."Currency"
                           OR NEW."RateCardId" IS DISTINCT FROM original."RateCardId" OR NEW."RateCardLineId" IS DISTINCT FROM original."RateCardLineId"
                           OR NEW."RateCardVersion" IS DISTINCT FROM original."RateCardVersion" OR NEW."UnitPrice" IS DISTINCT FROM original."UnitPrice"
                           OR NEW."RatingStatus"<>original."RatingStatus" OR NEW."AllowanceApplied"<>0 OR NEW."OverageQuantity"<>NEW."Quantity" THEN
                            RAISE EXCEPTION 'adjustment lineage does not match original usage';
                        END IF;
                        SELECT COALESCE(SUM("Quantity"),0),COALESCE(SUM("CostAmount"),0),COALESCE(SUM("RatedAmount"),0)
                          INTO prior_quantity,prior_cost,prior_rated FROM platform."UsageEvents"
                         WHERE "TenantId"=NEW."TenantId" AND "AdjustsUsageEventId"=NEW."AdjustsUsageEventId";
                        IF original."Quantity"+prior_quantity+NEW."Quantity"<0 OR original."CostAmount"+prior_cost+NEW."CostAmount"<0
                           OR (original."RatedAmount" IS NOT NULL AND original."RatedAmount"+prior_rated+COALESCE(NEW."RatedAmount",0)<0) THEN
                            RAISE EXCEPTION 'cumulative adjustment exceeds original usage';
                        END IF;
                    END IF;
                    IF NEW."RatingStatus"='Rated' THEN
                        IF NEW."RateCardId" IS NULL OR NEW."RateCardLineId" IS NULL OR NEW."RateCardVersion" IS NULL OR NEW."UnitPrice" IS NULL THEN
                            RAISE EXCEPTION 'rated usage requires complete rate-card lineage';
                        END IF;
                        SELECT * INTO card FROM platform."RateCards" WHERE "Id"=NEW."RateCardId";
                        SELECT * INTO line FROM platform."RateCardLines" WHERE "Id"=NEW."RateCardLineId";
                        expected_meter:=CASE NEW."EventType" WHEN 'ai.tokens' THEN 'ai.tokens.external' WHEN 'storage.gb-hours' THEN 'storage.gb' ELSE NEW."EventType" END;
                        IF card."Id" IS NULL OR line."Id" IS NULL OR line."RateCardId"<>card."Id" OR line."MeterKey"<>expected_meter
                           OR card."Version"<>NEW."RateCardVersion" OR card."Currency"<>NEW."Currency" OR NOT card."IsActive"
                           OR card."EffectiveFromUtc">(NEW."OccurredAtUtc" AT TIME ZONE 'UTC')
                           OR (card."EffectiveToUtc" IS NOT NULL AND card."EffectiveToUtc"<=(NEW."OccurredAtUtc" AT TIME ZONE 'UTC'))
                           OR line."UnitPrice"<>NEW."UnitPrice" OR NEW."AllowanceApplied">line."IncludedQuantity"
                           OR NEW."RatedAmount" IS DISTINCT FROM ROUND(NEW."OverageQuantity"*NEW."UnitPrice",6) THEN
                            RAISE EXCEPTION 'rated usage does not match the effective rate-card line';
                        END IF;
                    ELSIF NEW."RatedAmount" IS NOT NULL THEN
                        RAISE EXCEPTION 'unrated usage cannot carry a rated amount';
                    END IF;
                    RETURN NEW;
                END $guard$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_usage_event_insert() FROM PUBLIC;
                CREATE TRIGGER usage_events_insert_guard BEFORE INSERT ON platform."UsageEvents"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_usage_event_insert();
                ALTER TABLE platform."UsageEvents" ENABLE ALWAYS TRIGGER usage_events_insert_guard;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.nexora_guard_usage_event_insert() CASCADE;");

            migrationBuilder.DropForeignKey(
                name: "FK_UsageEvents_RateCardLines_RateCardLineId",
                schema: "platform",
                table: "UsageEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_UsageEvents_RateCards_RateCardId",
                schema: "platform",
                table: "UsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_RateCardId",
                schema: "platform",
                table: "UsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_RateCardLineId",
                schema: "platform",
                table: "UsageEvents");
        }
    }
}
