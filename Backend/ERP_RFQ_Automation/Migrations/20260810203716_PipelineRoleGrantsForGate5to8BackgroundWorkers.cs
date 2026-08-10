using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The background workers' half of the Gate 5-8 grants, which 20260810050406 left out.
    ///
    /// <para><b>The defect, observed rather than reasoned about.</b> A local run of the platform
    /// console logged this every sweep, forever:</para>
    /// <code>
    /// 42501: permission denied for table ReportSubscriptions
    ///   at ScheduledReportWorker.ResolveBusinessUnitsWithDueReportsAsync
    /// </code>
    /// <para>20260810050406 created fifteen tenant-owned tables and granted every one of them to
    /// <c>nexora_tenant_app</c> alone. That is correct for anything reached from an HTTP request,
    /// and wrong for the four that a hosted BackgroundService also touches: those run with no
    /// tenant scope and therefore execute as <c>nexora_pipeline_app</c>
    /// (<c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c>), which held no privilege on them at
    /// all. Every sweep since has failed on the privilege check.</para>
    ///
    /// <para>The irony is worth recording so it is not repeated a third time: the migration that
    /// shipped this wrote a comment warning about precisely this failure mode — "a table with a
    /// policy and no GRANT is not 'more isolated', it is a table nobody can read: PostgreSQL raises
    /// 42501 on the privilege check before it ever evaluates a row predicate. Three tables shipped
    /// exactly that defect in a single gate and every test passed" — and then shipped it again for
    /// the other role. The test suite cannot see it: the PostgreSQL lane builds its own grants, and
    /// nothing exercises a worker sweep against a role-separated database.</para>
    ///
    /// <para><b>Only the four, and only the verbs each one uses.</b> The pipeline role is
    /// <c>BYPASSRLS</c>, so a grant here is a grant across every tenant at once. The other eleven
    /// tables from that migration are reached only from the request path and are deliberately left
    /// alone; granting all fifteen "for symmetry" would widen the bypass surface to eleven tables
    /// nothing runs against.</para>
    /// <list type="bullet">
    ///   <item><c>ReportSubscriptions</c> — ScheduledReportWorker reads due subscriptions and writes
    ///   back LastRunOn/LastRunOutcome/NextRunOn. SELECT, UPDATE.</item>
    ///   <item><c>inventory_reorder_alerts</c> — ReorderAlertSweepWorker reads open alerts and marks
    ///   them notified through an ExecuteUpdate. SELECT, UPDATE.</item>
    ///   <item><c>supplier_shipments</c>, <c>supplier_shipment_lines</c> — SlaSweepWorker reads both
    ///   AsNoTracking to find delivery-risk candidates and never writes either. SELECT only.</item>
    /// </list>
    /// <para>No INSERT and no DELETE anywhere: no worker creates or removes a row in these four, and
    /// 20260810110923 had already revoked DELETE on inventory_reorder_alerts from the tenant role as
    /// a deliberate append-only decision. Handing the bypass role a verb the application never
    /// issues is how a REVOKE elsewhere stops meaning anything.</para>
    /// </summary>
    public partial class PipelineRoleGrantsForGate5to8BackgroundWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            // Guarded on the role existing, like every other grant block in this project: a
            // single-role development database has none of these roles, and asking to GRANT to a
            // role that is absent raises 42704 and takes the whole migration down with it.
            migrationBuilder.Sql("""
                DO $pipeline_gate5to8_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    GRANT SELECT, UPDATE ON TABLE
                        public."ReportSubscriptions",
                        public."inventory_reorder_alerts"
                        TO nexora_pipeline_app;

                    GRANT SELECT ON TABLE
                        public."supplier_shipments",
                        public."supplier_shipment_lines"
                        TO nexora_pipeline_app;

                    -- Stated rather than assumed. The four tables carry bigint identity keys, and a
                    -- worker that only reads and updates never draws from their sequences — but an
                    -- INSERT added later would fail on the sequence rather than the table, which is
                    -- a confusing place to discover a missing grant. No sequence grant is issued
                    -- because no INSERT privilege is issued.
                    REVOKE INSERT, DELETE, TRUNCATE ON TABLE
                        public."ReportSubscriptions",
                        public."inventory_reorder_alerts",
                        public."supplier_shipments",
                        public."supplier_shipment_lines"
                        FROM nexora_pipeline_app;
                END
                $pipeline_gate5to8_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $pipeline_gate5to8_grants_down$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    REVOKE SELECT, UPDATE ON TABLE
                        public."ReportSubscriptions",
                        public."inventory_reorder_alerts",
                        public."supplier_shipments",
                        public."supplier_shipment_lines"
                        FROM nexora_pipeline_app;
                END
                $pipeline_gate5to8_grants_down$;
                """);
        }
    }
}
