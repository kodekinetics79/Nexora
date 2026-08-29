using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Background ingestion runs customer resolution as nexora_pipeline_app. The original candidate
/// table migration granted only nexora_tenant_app, so a production-style fresh database rejected
/// the worker at runtime even though tenant-scoped requests succeeded.
/// </summary>
[DbContext(typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext))]
[Migration("20260829180000_GrantPipelineCustomerResolutionCandidateAccess")]
public sealed class GrantPipelineCustomerResolutionCandidateAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $security$
            DECLARE candidate_sequence text;
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                    GRANT SELECT, INSERT, UPDATE, DELETE
                        ON TABLE public.lead_customer_match_candidates
                        TO nexora_pipeline_app;

                    candidate_sequence := pg_get_serial_sequence(
                        'public.lead_customer_match_candidates', 'Id');
                    IF candidate_sequence IS NOT NULL THEN
                        EXECUTE format(
                            'GRANT USAGE ON SEQUENCE %s TO nexora_pipeline_app',
                            candidate_sequence);
                    END IF;

                    REVOKE TRUNCATE
                        ON TABLE public.lead_customer_match_candidates
                        FROM nexora_pipeline_app;
                END IF;
            END
            $security$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $security$
            DECLARE candidate_sequence text;
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                    REVOKE SELECT, INSERT, UPDATE, DELETE, TRUNCATE
                        ON TABLE public.lead_customer_match_candidates
                        FROM nexora_pipeline_app;

                    candidate_sequence := pg_get_serial_sequence(
                        'public.lead_customer_match_candidates', 'Id');
                    IF candidate_sequence IS NOT NULL THEN
                        EXECUTE format(
                            'REVOKE USAGE ON SEQUENCE %s FROM nexora_pipeline_app',
                            candidate_sequence);
                    END IF;
                END IF;
            END
            $security$;
            """);
    }
}
