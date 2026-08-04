using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Two procurement integrity fixes.
    ///
    /// 1. <c>SupplierSolicitations.DueOn</c> — the supplier response deadline the buyer commits
    ///    to. It was accepted by the API and then dropped, surviving only as prose inside the
    ///    free-text Notes column, so it could never be queried, listed, or chased.
    ///
    /// 2. <c>nexora_supplier_po_doc_seq</c> — server authority for legacy purchase-history PO
    ///    document numbers, mirroring <c>nexora_rfq_number_seq</c>. The previous generator read
    ///    MAX(PoDocId) and incremented it in application memory outside any transaction, so two
    ///    concurrent callers issued the same number. The sequence is seeded past the highest
    ///    number already persisted so no historical PO number can ever be reissued.
    /// </summary>
    public partial class Module05ProcurementDeadlineAndPoNumberAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DueOn",
                table: "SupplierSolicitations",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSolicitations_BU_DueOn",
                table: "SupplierSolicitations",
                columns: new[] { "BusinessUnitId", "DueOn" },
                filter: "\"DueOn\" IS NOT NULL");

            migrationBuilder.Sql(
                """
                CREATE SEQUENCE IF NOT EXISTS public.nexora_supplier_po_doc_seq
                    AS bigint
                    START WITH 1
                    INCREMENT BY 1;

                DO $reconcile$
                DECLARE
                    persisted_high_water bigint;
                    sequence_high_water bigint;
                BEGIN
                    SELECT max((regexp_match("PoDocId", '^PO([0-9]+)$'))[1]::bigint)
                    INTO persisted_high_water
                    FROM public."SupplierPurchaseHistory"
                    WHERE "PoDocId" ~ '^PO[0-9]+$';

                    SELECT last_value INTO sequence_high_water
                    FROM public.nexora_supplier_po_doc_seq;

                    IF persisted_high_water IS NOT NULL AND persisted_high_water >= sequence_high_water THEN
                        PERFORM setval('public.nexora_supplier_po_doc_seq', persisted_high_water, true);
                    END IF;
                END
                $reconcile$;

                DO $grant$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT USAGE ON SEQUENCE public.nexora_supplier_po_doc_seq TO nexora_tenant_app;
                    END IF;
                END
                $grant$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $revoke$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        REVOKE USAGE ON SEQUENCE public.nexora_supplier_po_doc_seq FROM nexora_tenant_app;
                    END IF;
                END
                $revoke$;
                -- The sequence itself is preserved across rollback so issued PO document
                -- numbers can never be reused by a later re-upgrade.
                """);

            migrationBuilder.DropIndex(
                name: "IX_SupplierSolicitations_BU_DueOn",
                table: "SupplierSolicitations");

            migrationBuilder.DropColumn(
                name: "DueOn",
                table: "SupplierSolicitations");
        }
    }
}
