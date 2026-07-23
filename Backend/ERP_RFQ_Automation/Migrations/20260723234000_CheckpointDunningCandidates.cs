using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CheckpointDunningCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DunningRunDecisions_BU_Run_Customer_Currency",
                table: "DunningRunDecisions");

            migrationBuilder.AddColumn<long>(
                name: "CustomerCollectionProfileId",
                table: "DunningRunDecisions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_BusinessUnitId_CustomerCollectionProfil~",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "CustomerCollectionProfileId" });

            migrationBuilder.CreateIndex(
                name: "UX_DunningRunDecisions_BU_Run_Profile",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "DunningRunId", "CustomerCollectionProfileId" },
                unique: true,
                filter: "\"CustomerCollectionProfileId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_DunningRunDecisions_CustomerCollectionProfiles_BusinessUnit~",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "CustomerCollectionProfileId" },
                principalTable: "CustomerCollectionProfiles",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE public."DunningRunDecisions"
                    ADD CONSTRAINT "CK_DunningRunDecisions_ProfileCheckpoint"
                    CHECK ("CustomerCollectionProfileId" IS NOT NULL) NOT VALID;

                CREATE OR REPLACE FUNCTION public.nexora_ar_verify_run_decision_profile()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM public."CustomerCollectionProfiles" profile
                        JOIN public."DunningRuns" run
                          ON run."BusinessUnitId" = profile."BusinessUnitId"
                         AND run."Id" = NEW."DunningRunId"
                         AND run."DunningPolicyId" = profile."DunningPolicyId"
                        WHERE profile."BusinessUnitId" = NEW."BusinessUnitId"
                          AND profile."Id" = NEW."CustomerCollectionProfileId"
                          AND profile."CustomerId" = NEW."CustomerId"
                          AND profile."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId") THEN
                        RAISE EXCEPTION 'the dunning decision profile does not match its run, customer, and currency checkpoint'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER trg_dunningrundecisions_verify_profile
                    BEFORE INSERT OR UPDATE OF "CustomerCollectionProfileId", "DunningRunId", "CustomerId", "CurrencyId"
                    ON public."DunningRunDecisions" FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_ar_verify_run_decision_profile();
                REVOKE ALL ON FUNCTION public.nexora_ar_verify_run_decision_profile() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_ar_verify_run_decision_profile() TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_dunningrundecisions_verify_profile ON public."DunningRunDecisions";
                DROP FUNCTION IF EXISTS public.nexora_ar_verify_run_decision_profile();
                ALTER TABLE public."DunningRunDecisions"
                    DROP CONSTRAINT IF EXISTS "CK_DunningRunDecisions_ProfileCheckpoint";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_DunningRunDecisions_CustomerCollectionProfiles_BusinessUnit~",
                table: "DunningRunDecisions");

            migrationBuilder.DropIndex(
                name: "IX_DunningRunDecisions_BusinessUnitId_CustomerCollectionProfil~",
                table: "DunningRunDecisions");

            migrationBuilder.DropIndex(
                name: "UX_DunningRunDecisions_BU_Run_Profile",
                table: "DunningRunDecisions");

            migrationBuilder.DropColumn(
                name: "CustomerCollectionProfileId",
                table: "DunningRunDecisions");

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_BU_Run_Customer_Currency",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "DunningRunId", "CustomerId", "CurrencyId" });
        }
    }
}
