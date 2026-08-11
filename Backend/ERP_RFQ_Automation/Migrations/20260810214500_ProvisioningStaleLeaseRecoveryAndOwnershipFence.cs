using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Makes a stuck provisioning execution recoverable through a governed path, and makes the
    /// ungoverned path impossible.
    ///
    /// <para><b>What was missing.</b> A provisioning execution is claimed under a lease that
    /// EXPIRES, so a runner whose process died does not park a tenant forever — another runner
    /// picks the work up once <c>LeaseUntil</c> passes. That is enough for the machine and not
    /// enough for a person. An operator looking at an execution stuck in <c>Running</c> could see
    /// only a deadline, which is derived from a configuration value that may have been edited
    /// since; there was no record of when a runner was last demonstrably ALIVE, so "is this
    /// abandoned?" could not be answered from the row. The only way out was a hand-written UPDATE
    /// against the lease columns — no evidence, no reason, no audit, and nothing at all preventing
    /// that UPDATE from landing on an execution a healthy runner was in the middle of.</para>
    ///
    /// <para><b>What this adds.</b> A heartbeat, so silence is an observation instead of an
    /// inference; four columns recording who took ownership away, when and why, on the row where
    /// the next operator will read them; and a trigger that refuses to let ANY statement, from any
    /// role, move a LIVE lease to a different owner. The trigger is the load-bearing half: it
    /// turns "two runners must never work one execution" from a convention the application follows
    /// into an invariant the database holds.</para>
    /// </summary>
    public partial class ProvisioningStaleLeaseRecoveryAndOwnershipFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseHeartbeatAt",
                schema: "platform",
                table: "ProvisioningExecutions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecoveredAttemptCount",
                schema: "platform",
                table: "ProvisioningExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRecoveredOn",
                schema: "platform",
                table: "ProvisioningExecutions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRecoveredBy",
                schema: "platform",
                table: "ProvisioningExecutions",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRecoveryReason",
                schema: "platform",
                table: "ProvisioningExecutions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            InstallLeaseTransferFence(migrationBuilder);
            GrantProvisioningRecoveryPrivileges(migrationBuilder);
        }

        /// <summary>
        /// The invariant, in the only place it can actually be held.
        ///
        /// <para><b>What it refuses.</b> An UPDATE that changes <c>LeaseOwner</c> or
        /// <c>LeaseToken</c>, or shortens <c>LeaseUntil</c>, on a row that is <c>Running</c> with an
        /// unexpired lease. Exactly one exception is allowed: the holder standing down, which is
        /// recognisable because it nulls all three lease columns AND moves the execution to a
        /// state where nothing further runs without a fresh decision (<c>Succeeded</c>,
        /// <c>Failed</c> or <c>Cancelled</c>). Anything else is a lease STEAL, and there is no
        /// legitimate caller for one — the whole point of the lease is that a runner mid-step owns
        /// its execution until it lets go or its deadline passes.</para>
        ///
        /// <para><b>Why a trigger rather than trusting the service.</b> Application guards bind the
        /// code paths that exist today. This table is reachable from a psql session, a data-fix
        /// script and any future service somebody writes in a hurry at 2am to unstick a customer —
        /// which is precisely when the reasoning above is least likely to be reconstructed. A
        /// BEFORE UPDATE trigger binds all of them, including the owner connection.</para>
        ///
        /// <para><c>ENABLE ALWAYS</c> is deliberate and matches the append-only guards installed by
        /// 20260807022229: it is what makes the trigger fire under
        /// <c>session_replication_role = 'replica'</c>, which the purge path uses and where an
        /// ordinary trigger silently does not run. A superuser can still drop it; that is inherent
        /// and is not the threat model. The threat model is this application and the scripts we
        /// write against it.</para>
        ///
        /// <para><b>What it deliberately does NOT refuse.</b> Taking a LAPSED lease. That is the
        /// runner's ordinary claim, and it is settled correctly by the <c>Version</c> concurrency
        /// token: two runners racing for an abandoned execution produce exactly one winner. The
        /// database cannot tell an operator's governed recovery from a hand-written UPDATE in that
        /// case, because both are the same statement — what separates them is the staleness
        /// evidence and the audit record, and those live in <c>IProvisioningLeaseRecovery</c>.
        /// Stated here rather than left implied: this trigger makes the DANGEROUS write
        /// impossible, and makes the merely UNGOVERNED one the operator's own recorded choice.</para>
        /// </summary>
        private static void InstallLeaseTransferFence(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform.nexora_guard_provisioning_lease_transfer()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    -- Only a LIVE lease is protected. Timestamps in this schema are
                    -- 'timestamp without time zone' holding UTC, so the comparison must be made
                    -- against UTC and never against the server's local now().
                    IF OLD."State" <> 'Running'
                       OR OLD."LeaseToken" IS NULL
                       OR OLD."LeaseUntil" IS NULL
                       OR OLD."LeaseUntil" <= (now() AT TIME ZONE 'utc') THEN
                        RETURN NEW;
                    END IF;

                    IF NEW."LeaseOwner" IS NOT DISTINCT FROM OLD."LeaseOwner"
                       AND NEW."LeaseToken" IS NOT DISTINCT FROM OLD."LeaseToken"
                       AND NEW."LeaseUntil" >= OLD."LeaseUntil" THEN
                        -- Renewal, or any write that leaves ownership alone. This is the runner
                        -- marking a step, which happens several times per execution.
                        RETURN NEW;
                    END IF;

                    -- The holder standing down: all three lease columns released together, and the
                    -- execution parked where nothing runs again without a fresh decision.
                    IF NEW."LeaseOwner" IS NULL
                       AND NEW."LeaseToken" IS NULL
                       AND NEW."LeaseUntil" IS NULL
                       AND NEW."State" IN ('Succeeded', 'Failed', 'Cancelled') THEN
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION
                        'Provisioning execution % is leased by % until % (UTC) and its ownership '
                        'cannot be changed: a live lease means a runner is presumed to be mid-step, '
                        'and a second runner on the same half-built tenant would write the same rows '
                        'twice. Wait for the lease to lapse, then recover it through '
                        'IProvisioningLeaseRecovery so the transfer carries evidence and an audit '
                        'record.',
                        OLD."Id", OLD."LeaseOwner", OLD."LeaseUntil"
                        USING ERRCODE = '55006';
                END
                $function$;
                """);

            migrationBuilder.Sql(
                "REVOKE ALL ON FUNCTION platform.nexora_guard_provisioning_lease_transfer() FROM PUBLIC;");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS provisioning_executions_lease_transfer_guard
                    ON platform."ProvisioningExecutions";
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER provisioning_executions_lease_transfer_guard
                    BEFORE UPDATE ON platform."ProvisioningExecutions"
                    FOR EACH ROW
                    EXECUTE FUNCTION platform.nexora_guard_provisioning_lease_transfer();
                """);

            // ENABLE ALWAYS, not the default ENABLE ORIGIN: the default does not fire under
            // session_replication_role = 'replica', which is exactly the mode a bulk repair or the
            // tenant purge path runs in.
            migrationBuilder.Sql("""
                ALTER TABLE platform."ProvisioningExecutions"
                    ENABLE ALWAYS TRIGGER provisioning_executions_lease_transfer_guard;
                """);
        }

        /// <summary>
        /// Privileges for the columns and the function this migration adds.
        ///
        /// <para><b>Why a grant block for five columns on a table that is already granted.</b>
        /// Table-level UPDATE does cover a column added later — but this codebase has been bitten
        /// by point-in-time grants twice, most recently this week when background workers hit 42501
        /// on <c>ReportSubscriptions</c> (fixed in 20260810203716). The block is therefore explicit
        /// and idempotent: it restates what the runner needs, and re-asserts the REVOKE that keeps
        /// the tenant and identity planes out of the onboarding pipeline entirely — an execution
        /// row now also carries who declared a customer's provisioning attempt dead and why, which
        /// is internal correspondence about a customer and not tenant data.</para>
        ///
        /// <para>The role-existence guard is the same one every grant block in this project opens
        /// with: a developer database created before the role topology existed must migrate
        /// cleanly rather than fail on a GRANT to a role that is not there.</para>
        /// </summary>
        private static void GrantProvisioningRecoveryPrivileges(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $provisioning_recovery_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        RETURN;
                    END IF;

                    -- The runner, the platform plane and the recovery service all execute as
                    -- nexora_pipeline_app: TenantRlsCommandInterceptor routes /api/platform there
                    -- when there is no tenant scope, and a background worker with no HttpContext
                    -- lands on the same role.
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                        platform."ProvisioningExecutions", platform."ProvisioningSteps"
                        TO nexora_pipeline_app;

                    -- A recovery writes a PlatformAuditLogs row in the SAME transaction as the
                    -- ownership change. That table is append-only by REVOKE and by trigger; the
                    -- pipeline role needs INSERT on it and nothing more. Restated because a
                    -- recovery that could move ownership but not record it would commit the one
                    -- half of this operation that must never travel alone.
                    GRANT SELECT, INSERT ON TABLE platform."PlatformAuditLogs"
                        TO nexora_pipeline_app;
                    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformAuditLogs"
                        FROM nexora_pipeline_app;

                    GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                        platform."ProvisioningExecutions_Id_seq",
                        platform."ProvisioningSteps_Id_seq"
                        TO nexora_pipeline_app;

                    -- The guard function is invoked by the trigger, which runs with the
                    -- definer-independent privileges of the statement; no role needs EXECUTE on it
                    -- directly, and granting one would let it be called out of context.
                    REVOKE ALL PRIVILEGES ON TABLE
                        platform."ProvisioningExecutions", platform."ProvisioningSteps"
                        FROM nexora_tenant_app, nexora_identity_app;
                END
                $provisioning_recovery_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS provisioning_executions_lease_transfer_guard
                        ON platform."ProvisioningExecutions";
                    """);
                migrationBuilder.Sql(
                    "DROP FUNCTION IF EXISTS platform.nexora_guard_provisioning_lease_transfer();");
            }

            migrationBuilder.DropColumn(
                name: "LastRecoveryReason", schema: "platform", table: "ProvisioningExecutions");
            migrationBuilder.DropColumn(
                name: "LastRecoveredBy", schema: "platform", table: "ProvisioningExecutions");
            migrationBuilder.DropColumn(
                name: "LastRecoveredOn", schema: "platform", table: "ProvisioningExecutions");
            migrationBuilder.DropColumn(
                name: "RecoveredAttemptCount", schema: "platform", table: "ProvisioningExecutions");
            migrationBuilder.DropColumn(
                name: "LeaseHeartbeatAt", schema: "platform", table: "ProvisioningExecutions");
        }
    }
}
