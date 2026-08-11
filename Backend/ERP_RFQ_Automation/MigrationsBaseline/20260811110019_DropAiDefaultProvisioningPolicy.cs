using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Removes <c>nexora_ai_default_provisioning</c> from public."AiProcessingPolicies".
    ///
    /// <para>THE DEFECT. The table carried TWO permissive policies, and PostgreSQL ORs
    /// permissive policies together — satisfying EITHER one admits the row.
    /// <c>nexora_tenant_isolation</c> pins <c>"BusinessUnitId"</c> to
    /// <c>current_setting('nexora.business_unit_id')</c>, which is correct.
    /// <c>nexora_ai_default_provisioning</c> (cmd INSERT, TO PUBLIC) pinned nine columns —
    /// IsEnabled, ExternalProcessingAllowed, AllowedPurposes, AllowedProvider, AllowedModel,
    /// MonthlySoftTokenLimit, MonthlyHardTokenLimit, Version, UpdatedBy — and did NOT pin
    /// BusinessUnitId. Any RLS-bound role holding INSERT could therefore write an AI
    /// governance row for ANOTHER tenant by reproducing those nine constants, choosing the
    /// unpinned remainder freely: RetentionDays, DataResidency, EgressPolicy,
    /// RedactionRequired, PrivacyReviewRequired, InputOutputAuditAllowed. Reproduced against
    /// a live database: tenant 9001 inserted a row owning tenant 9002 with
    /// RetentionDays=3650, DataResidency='ATTACKER-REGION', EgressPolicy='AllFieldsRaw'.
    /// This was the only such break among the 232 policies in the baseline.
    ///
    /// <para>WHY DROPPING IT IS THE WHOLE FIX, rather than adding the missing BusinessUnitId
    /// predicate to it. The policy is not load-bearing in ANY configuration, which was
    /// established by experiment rather than by reading:</para>
    ///
    /// <para>The row it was written for is not written by application code. It is written by
    /// <c>nexora_create_default_ai_policy()</c>, the SECURITY DEFINER trigger function fired
    /// AFTER INSERT ON "BusinessUnits" (MigrationsBaseline/Sql/02_functions.sql,
    /// Sql/06_triggers.sql). Inside a SECURITY DEFINER function the effective role is the
    /// FUNCTION OWNER, so the policy is consulted only when that owner is itself bound by
    /// RLS. Two cases, and the policy is dead in both:</para>
    ///
    /// <para>(1) Owner bypasses RLS — superuser, or BYPASSRLS. This is the only configuration
    /// in which tenant provisioning actually works today. RLS is never evaluated for the
    /// trigger's INSERT at all, so the policy is never consulted. Verified: dropping the
    /// policy and then provisioning a business unit under both nexora_pipeline_app and
    /// nexora_tenant_app still created the default AI policy row with the correct fail-closed
    /// defaults (RetentionDays=30, DataResidency='TenantApprovedRegion',
    /// EgressPolicy='RedactedFieldsOnly').</para>
    ///
    /// <para>(2) Owner is bound by RLS — the shape Program.cs ValidateRuntimeDatabaseRoleAsync
    /// requires of the runtime role (NOINHERIT, non-superuser, non-BYPASSRLS, member of the
    /// three nexora_*_app roles). Here the policy does not help either, because the trigger's
    /// statement is <c>INSERT ... ON CONFLICT ("BusinessUnitId") DO NOTHING</c> and an
    /// ON CONFLICT arbiter check additionally requires a policy that permits reading the
    /// target. nexora_ai_default_provisioning is FOR INSERT only and can never satisfy that.
    /// Verified against a live database under exactly that role topology: the plain INSERT
    /// passes the WITH CHECK, the identical INSERT with ON CONFLICT DO NOTHING is refused
    /// with "new row violates row-level security policy" WITH the policy still in place.
    /// Provisioning is therefore already broken in this configuration, with or without the
    /// policy, and this migration neither causes nor worsens that.</para>
    ///
    /// <para>Nor did the C# fallback need it. ProvisioningStepExecutor.CreateAiPolicyAsync and
    /// the legacy TenantsController path both write <c>UpdatedBy = &lt;operator email&gt;</c>,
    /// which cannot satisfy a WITH CHECK demanding 'tenant-provisioning'. They succeed only
    /// because they run as nexora_pipeline_app, which is BYPASSRLS. And nexora_tenant_app
    /// holds no INSERT grant on the table at all (Sql/09_privileges.sql), which is the
    /// control that masked this hole in the shipped configuration — table privileges are
    /// checked before RLS, so the cross-tenant write was refused with
    /// "permission denied for table" rather than by the isolation predicate. That mask is one
    /// GRANT away from being removed, and TO PUBLIC extends the hole to any future
    /// RLS-bound role that is given INSERT. The policy bought nothing and stood to cost a
    /// tenant's AI data-residency and egress governance.</para>
    ///
    /// <para>Deliberately NOT made RESTRICTIVE: restrictive policies AND with the permissive
    /// set, so converting it would constrain every other write to the table — including the
    /// tenant's own legitimate policy UPDATE — rather than closing the INSERT hole.</para>
    /// </summary>
    public partial class DropAiDefaultProvisioningPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            // to_regclass guard rather than a bare DROP POLICY: the portable lane
            // (SQLite / SQL Server) never builds this table, and a database restored
            // from a partial baseline must not take the whole migration down with a 42P01.
            migrationBuilder.Sql("""
                DO $drop_ai_default_provisioning$
                BEGIN
                    IF to_regclass('public."AiProcessingPolicies"') IS NULL THEN
                        RETURN;
                    END IF;

                    DROP POLICY IF EXISTS nexora_ai_default_provisioning
                        ON public."AiProcessingPolicies";
                END
                $drop_ai_default_provisioning$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!IsNpgsql(migrationBuilder))
                return;

            // Restores the policy exactly as 20260723140000_AddAiGovernanceLedger created it,
            // BusinessUnitId still unpinned — a Down() that "fixes" what it restores does not
            // return the database to the state the previous migration left, and would make
            // this migration non-reversible in the only sense that matters.
            //
            // The pg_roles guard is present for the same reason every RLS migration in this
            // repo carries one: a database provisioned without the execution roles reaches
            // this statement, and CREATE POLICY on a table whose sibling policies reference
            // nexora_tenant_app is only meaningful where that role exists.
            migrationBuilder.Sql("""
                DO $restore_ai_default_provisioning$
                BEGIN
                    IF to_regclass('public."AiProcessingPolicies"') IS NULL THEN
                        RETURN;
                    END IF;

                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        RETURN;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM pg_policy policy
                        JOIN pg_class target ON target.oid = policy.polrelid
                        WHERE target.relname = 'AiProcessingPolicies'
                          AND policy.polname = 'nexora_ai_default_provisioning') THEN
                        RETURN;
                    END IF;

                    CREATE POLICY nexora_ai_default_provisioning
                        ON public."AiProcessingPolicies"
                        FOR INSERT TO PUBLIC
                        WITH CHECK (
                            "IsEnabled" = TRUE
                            AND "ExternalProcessingAllowed" = FALSE
                            AND "AllowedPurposes" = 'RfqExtraction,BoqDraft'
                            AND "AllowedProvider" IS NULL
                            AND "AllowedModel" IS NULL
                            AND "MonthlySoftTokenLimit" IS NULL
                            AND "MonthlyHardTokenLimit" IS NULL
                            AND "Version" = 1
                            AND "UpdatedBy" = 'tenant-provisioning');
                END
                $restore_ai_default_provisioning$;
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
