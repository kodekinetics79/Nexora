using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Exposes the legal-number allocator as a tenant-scoped privileged operation. The tenant runtime
/// role deliberately cannot insert or update LegalDocumentCounters directly; payments and customer
/// statements must therefore allocate through this function rather than weakening the counter ACL.
/// </summary>
[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20260829220000_GovernLegalDocumentNumberAllocation")]
public partial class GovernLegalDocumentNumberAllocation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.nexora_allocate_legal_document_number(
                requested_business_unit_id bigint,
                requested_document_type text,
                requested_fiscal_year integer)
            RETURNS bigint
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
            DECLARE allocated_number bigint;
            DECLARE request_business_unit_id bigint;
            BEGIN
                request_business_unit_id := NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint;
                IF request_business_unit_id IS NULL
                   OR request_business_unit_id IS DISTINCT FROM requested_business_unit_id THEN
                    RAISE EXCEPTION 'legal number allocation requires the authenticated tenant scope'
                        USING ERRCODE = '42501';
                END IF;
                IF requested_document_type NOT IN ('Receipt', 'Statement') THEN
                    RAISE EXCEPTION 'unsupported application-allocated legal document type'
                        USING ERRCODE = '22023';
                END IF;
                IF requested_fiscal_year NOT BETWEEN 2000 AND 2200 THEN
                    RAISE EXCEPTION 'invalid legal document fiscal year' USING ERRCODE = '22023';
                END IF;

                INSERT INTO public."LegalDocumentCounters"
                    ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                VALUES
                    (requested_business_unit_id, requested_document_type, requested_fiscal_year, 2)
                ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
                DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
                RETURNING "NextNumber" - 1 INTO allocated_number;

                RETURN allocated_number;
            END
            $function$;

            REVOKE ALL ON FUNCTION public.nexora_allocate_legal_document_number(bigint, text, integer) FROM PUBLIC;
            DO $grant$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                    GRANT EXECUTE ON FUNCTION public.nexora_allocate_legal_document_number(bigint, text, integer)
                        TO nexora_tenant_app;
                END IF;
            END
            $grant$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS public.nexora_allocate_legal_document_number(bigint, text, integer);
            """);
    }
}
