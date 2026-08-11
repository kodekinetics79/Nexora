using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Two grants for two reads/writes the application already performs and the database already
    /// refuses. Both are 42501s that no test could see, because the portable SQLite lane has neither
    /// roles nor column privileges.
    ///
    /// <para><b>ONE — the non-HTTP execution role cannot append to the master-data change
    /// audit</b>, so every Customer/Supplier/Product write from a worker or seeder fails.</para>
    ///
    /// <para><b>THE DEFECT.</b> <c>ErpRfqAutomationContext.SaveChanges</c> calls
    /// <c>MasterDataAuditInterceptor.Capture</c> on EVERY save — that is the design, and the right
    /// one: a controller-level audit is bypassed by the spreadsheet importers, the extraction
    /// worker and the seeders, none of which pass through a controller. So any save that touches a
    /// Customer, Supplier or Product also INSERTs into <c>public."MasterDataChangeEvents"</c> and
    /// <c>public."MasterDataFieldChanges"</c>.</para>
    ///
    /// <para>Those two tables were granted to <c>nexora_tenant_app</c> (SELECT, INSERT) and
    /// <c>nexora_purge_app</c> (SELECT, DELETE) and to nobody else.
    /// <c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c> returns <c>nexora_pipeline_app</c>
    /// whenever there is no HttpContext — which is every startup seeder and every background
    /// worker. That role holds SELECT/INSERT/UPDATE/DELETE on <c>"Customers"</c>, <c>"Products"</c>
    /// and <c>"Suppliers"</c> and had nothing at all on the audit tables, so the audit INSERT that
    /// EF issues in the same batch failed with
    /// <c>42501: permission denied for table MasterDataChangeEvents</c> and took the master-data
    /// write down with it.</para>
    ///
    /// <para><b>Reproduced</b> against postgres:16-alpine on 2026-08-12 by starting the API with
    /// <c>GoldenJourneySeed:Enabled=true</c>: <c>GoldenCommercialJourneySeeder.EnsureCustomerAsync</c>
    /// → <c>SaveChangesAsync</c> → 42501, an unhandled exception during startup that killed the
    /// process. That is <c>scripts/e2e/run-phase1-base-journey.sh</c> and the whole documented local
    /// E2E data path. It is not a seeder-only fault: the same INSERT is issued by any background
    /// path that writes master data, and the seeder is simply the one that runs on every E2E run.</para>
    ///
    /// <para><b>Why a grant and not a change to the interceptor.</b> Skipping the audit when the
    /// writer is a worker would make the audit exactly as bypassable as the controller-level one it
    /// replaced — a supplier created by the Excel importer would have no trail, which is the defect
    /// <c>MasterDataAuditInterceptor</c> exists to prevent. The audit must be written by whoever
    /// writes the row.</para>
    ///
    /// <para><b>Why this is not "more than it needs".</b> The grant is SELECT and INSERT, matching
    /// <c>nexora_tenant_app</c> exactly. No UPDATE and no DELETE: the trail stays append-only for
    /// this role, and <c>trg_master_data_audit_append_only</c> plus
    /// <c>MasterDataAuditInterceptor.ValidateAppendOnly</c> continue to be the controls on top of
    /// that. Nothing is granted on any other table, no role gains BYPASSRLS it did not have, and
    /// the deletion path stays with <c>nexora_purge_app</c> where offboarding put it.</para>
    ///
    /// <para><b>TWO — the tenant role cannot read the legal-hold fence</b>, so the tenant's own
    /// evidence-retention screen 500s. <c>GET /api/platform-governance/evidence-retention</c> runs as
    /// <c>nexora_tenant_app</c> and reaches <c>EvidenceRetentionService.TenantHoldBlocksAsync</c>,
    /// which must prove no legal hold is in force before it reports anything as reclaimable —
    /// deletion is irreversible, so "cannot see a hold" has to mean "purge nothing" rather than
    /// "purge freely". <c>platform."TenantLegalHolds"</c> was granted to <c>nexora_pipeline_app</c>
    /// alone, so the read raised <c>42501: permission denied for table TenantLegalHolds</c>.</para>
    ///
    /// <para><b>Reproduced</b> against postgres:16-alpine on 2026-08-12, and it is worth recording
    /// HOW, because the endpoint answers 200 in the obvious test. The fence resolves the platform
    /// tenant FIRST and returns early when the business unit has none — so on a business unit with
    /// no <c>platform.Tenants</c> row the hold table is never touched and the screen looks healthy.
    /// It fails only once the tenant is governed, which is every real customer.</para>
    ///
    /// <para><b>Why two columns and not the table.</b> <c>TenantLegalHoldFence.HasActiveAsync</c> is
    /// an <c>EXISTS</c> over <c>("TenantId", "ReleasedOn")</c> and reads nothing else. A table-level
    /// grant would additionally expose <c>Reason</c>, <c>Authority</c> and <c>EvidenceReference</c>
    /// — the operator's written record of WHY a customer is under legal hold — to the tenant plane.
    /// The column grant answers exactly the question the code asks ("is a hold in force?") and no
    /// more, and matches how 20260805105320 narrowed <c>platform."Tenants"</c> for the same role.
    /// SELECT only: placement and release stay on the platform plane, and the
    /// <c>tenant_legal_holds_immutable</c> trigger remains the control above that.</para>
    /// </summary>
    public partial class ExecutionRoleGrantsForMasterDataAuditAndLegalHoldFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql("""
                DO $pipeline_master_data_audit$
                BEGIN
                    -- The same guard every role-touching migration here carries: a database
                    -- provisioned without the execution roles (the portable lane, a partial
                    -- restore) has nothing to authorise and must not fail.
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    IF to_regclass('public."MasterDataChangeEvents"') IS NOT NULL THEN
                        GRANT SELECT, INSERT ON TABLE public."MasterDataChangeEvents"
                            TO nexora_pipeline_app;
                    END IF;

                    IF to_regclass('public."MasterDataFieldChanges"') IS NOT NULL THEN
                        GRANT SELECT, INSERT ON TABLE public."MasterDataFieldChanges"
                            TO nexora_pipeline_app;
                    END IF;

                    -- The legal-hold fence, for the tenant role only, and only the two columns
                    -- TenantLegalHoldFence.HasActiveAsync predicates on.
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app')
                       AND to_regclass('platform."TenantLegalHolds"') IS NOT NULL THEN
                        GRANT SELECT ("TenantId", "ReleasedOn")
                            ON TABLE platform."TenantLegalHolds" TO nexora_tenant_app;
                    END IF;
                END
                $pipeline_master_data_audit$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql("""
                DO $pipeline_master_data_audit_down$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    IF to_regclass('public."MasterDataChangeEvents"') IS NOT NULL THEN
                        REVOKE SELECT, INSERT ON TABLE public."MasterDataChangeEvents"
                            FROM nexora_pipeline_app;
                    END IF;

                    IF to_regclass('public."MasterDataFieldChanges"') IS NOT NULL THEN
                        REVOKE SELECT, INSERT ON TABLE public."MasterDataFieldChanges"
                            FROM nexora_pipeline_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app')
                       AND to_regclass('platform."TenantLegalHolds"') IS NOT NULL THEN
                        REVOKE SELECT ("TenantId", "ReleasedOn")
                            ON TABLE platform."TenantLegalHolds" FROM nexora_tenant_app;
                    END IF;
                END
                $pipeline_master_data_audit_down$;
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
