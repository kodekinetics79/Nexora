using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernProviderEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderSignature",
                table: "FinanceCommunicationContacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSignature",
                table: "DunningDeliveryAttempts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."FinanceCommunicationContacts"
                    ADD CONSTRAINT "CK_FinanceCommunicationContacts_ProviderSignature"
                    CHECK ("ProviderSignature" IS NULL OR "ProviderSignature" ~ '^[0-9a-f]{64}$');
                ALTER TABLE public."DunningDeliveryAttempts"
                    ADD CONSTRAINT "CK_DunningDeliveryAttempts_ProviderSignature"
                    CHECK ("ProviderSignature" IS NULL OR "ProviderSignature" ~ '^[0-9a-f]{64}$');

                CREATE OR REPLACE FUNCTION public.nexora_ar_verify_provider_evidence()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE provider_secret text;
                DECLARE canonical text;
                DECLARE expected_signature text;
                BEGIN
                    IF TG_TABLE_NAME = 'FinanceCommunicationContacts' THEN
                        SELECT "Secret" INTO provider_secret FROM public."FinanceProviderSecrets"
                         WHERE "Name" = 'ContactVerification';
                        canonical := NEW."BusinessUnitId"::text || E'\n' || NEW."CustomerId"::text || E'\n'
                            || NEW."Purpose" || E'\n' || NEW."Channel" || E'\n' || NEW."DestinationToken" || E'\n'
                            || NEW."MaskedDestination" || E'\n'
                            || floor(extract(epoch FROM NEW."EffectiveFrom" AT TIME ZONE 'UTC') * 1000)::bigint::text || E'\n'
                            || CASE WHEN NEW."EffectiveTo" IS NULL THEN '' ELSE
                                floor(extract(epoch FROM NEW."EffectiveTo" AT TIME ZONE 'UTC') * 1000)::bigint::text END || E'\n'
                            || NEW."VerificationEvidenceReference" || E'\n' || NEW."VerificationProviderEventId"::text;
                    ELSE
                        SELECT "Secret" INTO provider_secret FROM public."FinanceProviderSecrets"
                         WHERE "Name" = 'DunningDelivery';
                        canonical := NEW."BusinessUnitId"::text || E'\n' || NEW."DunningNoticeId"::text || E'\n'
                            || CASE WHEN NEW."Status" = 'Delivered' THEN 'true' ELSE 'false' END || E'\n'
                            || NEW."ProviderEventId"::text || E'\n' || coalesce(NEW."ProviderReference", '') || E'\n'
                            || floor(extract(epoch FROM NEW."ProviderOccurredOn" AT TIME ZONE 'UTC') * 1000)::bigint::text || E'\n'
                            || coalesce(NEW."FailureCode", '') || E'\n' || NEW."SignedEvidenceReference";
                    END IF;
                    IF provider_secret IS NULL THEN
                        RAISE EXCEPTION 'finance provider verification secret is not configured' USING ERRCODE = '55000';
                    END IF;
                    expected_signature := encode(hmac(convert_to(canonical, 'UTF8'),
                        convert_to(provider_secret, 'UTF8'), 'sha256'), 'hex');
                    IF NEW."ProviderSignature" IS DISTINCT FROM expected_signature THEN
                        RAISE EXCEPTION 'finance provider evidence signature is invalid' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_financecommunicationcontacts_verify_provider
                    BEFORE INSERT ON public."FinanceCommunicationContacts" FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_ar_verify_provider_evidence();
                CREATE TRIGGER trg_dunningdeliveryattempts_verify_provider
                    BEFORE INSERT ON public."DunningDeliveryAttempts" FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_ar_verify_provider_evidence();
                REVOKE ALL ON FUNCTION public.nexora_ar_verify_provider_evidence() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_ar_verify_provider_evidence() TO nexora_tenant_app;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_financecommunicationcontacts_verify_provider ON public."FinanceCommunicationContacts";
                DROP TRIGGER IF EXISTS trg_dunningdeliveryattempts_verify_provider ON public."DunningDeliveryAttempts";
                DROP FUNCTION IF EXISTS public.nexora_ar_verify_provider_evidence();
                """);
            migrationBuilder.DropColumn(
                name: "ProviderSignature",
                table: "FinanceCommunicationContacts");

            migrationBuilder.DropColumn(
                name: "ProviderSignature",
                table: "DunningDeliveryAttempts");
        }
    }
}
