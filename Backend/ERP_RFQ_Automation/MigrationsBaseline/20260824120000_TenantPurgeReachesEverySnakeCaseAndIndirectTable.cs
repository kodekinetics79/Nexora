using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Gives <c>nexora_purge_app</c> reach over the twenty-five tenant tables a purge could not see,
/// and gives the offboarding record somewhere to say whether the tenant's stored BYTES are gone.
///
/// <para><b>DEFECT ONE — eleven tables spelled the tenant column differently.</b>
/// <c>20260811154500_TenantPurgeExecutionRole</c> discovered its grant targets with
/// <c>lower(attname) IN ('businessunitid', 'buid')</c>, which is the same predicate
/// <c>TenantPurgeExecutor</c> used. The evidence and extraction tables are snake_case —
/// <c>source_documents</c>, <c>canonical_inquiries</c>, <c>field_evidence</c>,
/// <c>extraction_runs</c> and seven more all carry <c>business_unit_id</c> — so they matched
/// neither spelling. No grant, no policy, and no target: a purge swept none of them, counted none
/// of them in the operator's preview, named none of them in its report, and returned success.
/// Measured against production: 380 rows for one business unit and 515 for another.</para>
///
/// <para><b>DEFECT TWO — fourteen tables carry no tenant column at all.</b>
/// <c>public."EmailIngests"</c> reaches a tenant only through <c>Email_Configurations</c>, and it
/// is not alone: <c>RFQItems</c>, <c>QuoteItems</c>, <c>LeadItems</c> and <c>OrderItems</c> are
/// every line of every enquiry, quote and order the tenant ever had, and each hangs off a parent
/// the purge destroys. Because the purge runs under
/// <c>session_replication_role = 'replica'</c>, the foreign keys that would have objected are
/// suspended, so those rows were not merely left behind — they were left ORPHANED against a parent
/// that no longer existed, silently.</para>
///
/// <para><b>What this migration does NOT decide.</b> Whether a table is the customer's data is
/// declared in <c>TenantPlaneDataMap</c>, in C#, next to the reason. This migration only makes the
/// database able to carry out that decision. The two are kept in step by
/// <c>TenantPurgeExecutor.AssertPurgeReachAsync</c>, which refuses to sweep anything it cannot
/// prove it can reach — so a table added here and not there, or there and not here, stops a purge
/// by name instead of producing a success report over rows that are still present.</para>
///
/// <para><b>Why the indirect policies name the parent's tenant column rather than trusting the
/// parent's own policy.</b> Row-level security does apply inside a policy's own subquery, so
/// <c>EXISTS (SELECT 1 FROM "Leads" p WHERE p."ID" = t."LeadID")</c> would already be scoped by
/// <c>Leads</c>'s purge policy. It is written out anyway for the chains that end at a business
/// unit, because a policy that depends on ANOTHER policy for its scoping fails open if that one is
/// ever dropped, and "fails open" on a destruction path means reaching a second customer. The
/// column is READ from the catalogue and never written out: this schema spells the same concept
/// <c>"BusinessUnitID"</c> on <c>Leads</c>, <c>"BUID"</c> on <c>Products</c> and
/// <c>"BusinessUnitId"</c> on <c>material_lots</c>, and the wrong one answers 42703 against the
/// real database while every portable test stays green.</para>
/// </summary>
[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20260824120000_TenantPurgeReachesEverySnakeCaseAndIndirectTable")]
public sealed class TenantPurgeReachesEverySnakeCaseAndIndirectTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!IsNpgsql(migrationBuilder))
        {
            AddStorageColumns(migrationBuilder);
            return;
        }

        AddStorageColumns(migrationBuilder);

        migrationBuilder.Sql("""
            DO $tenant_purge_reach$
            DECLARE
                target      record;
                scope_sql   constant text :=
                    'NULLIF(current_setting(''nexora.purge_business_unit_id'', true), '''')::bigint';
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                    RETURN;
                END IF;

                -- ---- 1. every business unit column, however it is spelled --------------------
                -- Identical to the loop in 20260811154500_TenantPurgeExecutionRole except that
                -- underscores are stripped before comparing. That single change is the whole of
                -- defect one: it turns the rule from "matches one team's casing habit" into
                -- "names a business unit".
                FOR target IN
                    SELECT n.nspname AS schema_name,
                           c.relname  AS table_name,
                           a.attname  AS tenant_column,
                           c.relrowsecurity AS rls_enabled,
                           c.oid      AS relation
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                    WHERE c.relkind = 'r'
                      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND lower(replace(a.attname, '_', '')) IN ('businessunitid', 'buid')
                    ORDER BY n.nspname, c.relname
                LOOP
                    EXECUTE format(
                        'GRANT SELECT, DELETE ON %I.%I TO nexora_purge_app',
                        target.schema_name, target.table_name);

                    IF target.rls_enabled AND NOT EXISTS (
                        SELECT 1 FROM pg_policy p
                        WHERE p.polrelid = target.relation
                          AND p.polname = 'nexora_tenant_purge') THEN
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_purge ON %I.%I '
                            'AS PERMISSIVE FOR ALL TO nexora_purge_app '
                            'USING (%I = %s)',
                            target.schema_name, target.table_name, target.tenant_column, scope_sql);
                    END IF;
                END LOOP;

                -- ---- 2. the tables reached only through a parent ----------------------------
                -- Transcribed from TenantPlaneDataMap, not discovered. A foreign key to a
                -- tenant-scoped table does not make a row that table's child: public."QuoteItems"
                -- has five, and four of them point at lookups (Setup_Master, Products) that would
                -- have scoped the row correctly and INCOMPLETELY, leaving every line whose lookup
                -- column is null. Each column named here is NOT NULL, which is what makes the
                -- predicate cover all of the tenant's rows rather than most of them.
                --
                -- Grouped by child table because one table can hang off two different parents:
                -- public."Attachments" is filed against leads AND against material lots, and a
                -- policy carrying only the first arm is what makes every lot certificate
                -- invisible to the tenant that owns it and immortal under its purge.
                FOR target IN
                    SELECT child_table, array_agg(arm ORDER BY ord) AS arms
                    FROM (
                        SELECT v.child_table,
                               v.ord,
                               CASE
                                   WHEN v.discriminator IS NULL THEN ''
                                   ELSE format('%I.%I = %L AND ',
                                               v.child_table, v.discriminator, v.discriminator_value)
                               END
                               || CASE
                                   WHEN parent.tenant_column IS NULL THEN format(
                                       'EXISTS (SELECT 1 FROM public.%I p WHERE p.%I = %I.%I)',
                                       v.parent_table, v.parent_key, v.child_table, v.fk_column)
                                   ELSE format(
                                       'EXISTS (SELECT 1 FROM public.%I p WHERE p.%I = %I.%I AND p.%I = %s)',
                                       v.parent_table, v.parent_key, v.child_table, v.fk_column,
                                       parent.tenant_column, scope_sql)
                               END AS arm
                        FROM (VALUES
                            ('EmailIngests',             1, 'EmailConfigurationID', 'Email_Configurations',     'ID', NULL::name, NULL::text),
                            ('LeadItems',                1, 'LeadID',               'Leads',                    'ID', NULL,       NULL),
                            ('RFQItems',                 1, 'RFQID',                'RFQ',                      'ID', NULL,       NULL),
                            ('QuoteItems',               1, 'QuoteID',              'Quotes',                   'ID', NULL,       NULL),
                            ('OrderItems',               1, 'OrderID',              'Orders',                   'ID', NULL,       NULL),
                            ('ShipmentItems',            1, 'ShipmentID',           'Shipments',                'ID', NULL,       NULL),
                            ('ShipmentStatusHistory',    1, 'ShipmentId',           'Shipments',                'ID', NULL,       NULL),
                            ('ProductAttachments',       1, 'InventoryID',          'Products',                 'ID', NULL,       NULL),
                            ('SupplierPurchaseHistory',  1, 'ProductId',            'Products',                 'ID', NULL,       NULL),
                            ('custom_field_versions',    1, 'DefinitionId',         'custom_field_definitions', 'Id', NULL,       NULL),
                            ('custom_field_options',     1, 'VersionId',            'custom_field_versions',    'Id', NULL,       NULL),
                            ('custom_field_rules',       1, 'VersionId',            'custom_field_versions',    'Id', NULL,       NULL),
                            ('custom_field_dependencies',1, 'VersionId',            'custom_field_versions',    'Id', NULL,       NULL),
                            ('Attachments',              1, 'ParentID',             'Leads',                    'ID', 'ParentType', 'Lead'),
                            ('Attachments',              2, 'ParentID',             'material_lots',            'Id', 'ParentType', 'MaterialLotCertificate')
                        ) AS v(child_table, ord, fk_column, parent_table, parent_key, discriminator, discriminator_value)
                        CROSS JOIN LATERAL (
                            -- The parent's business unit column, READ. Null when the parent is
                            -- itself reached through a parent (custom_field_options ->
                            -- custom_field_versions), in which case the parent's own purge policy
                            -- does the scoping and this arm is deliberately not repeating it.
                            SELECT (
                                SELECT a.attname
                                FROM pg_attribute a
                                WHERE a.attrelid = to_regclass(format('public.%I', v.parent_table))
                                  AND a.attnum > 0 AND NOT a.attisdropped
                                  AND lower(replace(a.attname, '_', '')) IN ('businessunitid', 'buid')
                                ORDER BY a.attnum
                                LIMIT 1) AS tenant_column
                        ) AS parent
                        WHERE to_regclass(format('public.%I', v.child_table)) IS NOT NULL
                          AND to_regclass(format('public.%I', v.parent_table)) IS NOT NULL
                    ) AS resolved
                    GROUP BY child_table
                    ORDER BY child_table
                LOOP
                    EXECUTE format(
                        'GRANT SELECT, DELETE ON public.%I TO nexora_purge_app', target.child_table);

                    IF NOT EXISTS (
                        SELECT 1 FROM pg_class c
                        WHERE c.oid = to_regclass(format('public.%I', target.child_table))
                          AND c.relrowsecurity) THEN
                        CONTINUE;
                    END IF;

                    EXECUTE format(
                        'DROP POLICY IF EXISTS nexora_tenant_purge ON public.%I', target.child_table);
                    EXECUTE format(
                        'CREATE POLICY nexora_tenant_purge ON public.%I '
                        'AS PERMISSIVE FOR ALL TO nexora_purge_app USING (%s)',
                        target.child_table, array_to_string(target.arms, ' OR '));
                END LOOP;

                -- Every parent read by a predicate above needs SELECT, or the child is a table the
                -- purge can delete from and cannot evaluate — 42501 in the middle of a destructive
                -- transaction. Derived from the same list rather than restated.
                FOR target IN
                    SELECT DISTINCT parent_table
                    FROM (VALUES
                        ('Email_Configurations'), ('Leads'), ('RFQ'), ('Quotes'), ('Orders'),
                        ('Shipments'), ('Products'), ('custom_field_definitions'),
                        ('custom_field_versions'), ('material_lots')
                    ) AS v(parent_table)
                    WHERE to_regclass(format('public.%I', parent_table)) IS NOT NULL
                LOOP
                    EXECUTE format(
                        'GRANT SELECT ON public.%I TO nexora_purge_app', target.parent_table);
                END LOOP;
            END
            $tenant_purge_reach$;
            """);
    }

    /// <summary>
    /// Where an offboarding records what happened to the tenant's stored BYTES.
    ///
    /// <para>Separate columns rather than a flag, because "the rows are gone" and "the files are
    /// gone" are two different facts and a purge used to assert the second by only ever checking
    /// the first. Object deletion is not transactional and cannot join the destructive
    /// transaction, so the inventory is committed BEFORE anything is deleted and the outcome is
    /// written after — which makes the step resumable rather than fire-and-forget, and makes a
    /// half-deleted bucket a state somebody can find rather than one nobody can see.</para>
    /// </summary>
    private static void AddStorageColumns(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "StoragePurgeInventory", schema: "platform", table: "TenantOffboardings",
            type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "StoragePurgeCompletedOn", schema: "platform", table: "TenantOffboardings",
            type: "timestamp without time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "StoragePurgeDetail", schema: "platform", table: "TenantOffboardings",
            type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "StoragePurgeOutstandingCount", schema: "platform", table: "TenantOffboardings",
            type: "integer", nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StoragePurgeInventory", schema: "platform", table: "TenantOffboardings");
        migrationBuilder.DropColumn(
            name: "StoragePurgeCompletedOn", schema: "platform", table: "TenantOffboardings");
        migrationBuilder.DropColumn(
            name: "StoragePurgeDetail", schema: "platform", table: "TenantOffboardings");
        migrationBuilder.DropColumn(
            name: "StoragePurgeOutstandingCount", schema: "platform", table: "TenantOffboardings");

        if (!IsNpgsql(migrationBuilder))
            return;

        // Only the policies this migration could have created. The grants are left standing: a
        // Down that revokes SELECT/DELETE from a role that legitimately held it before would
        // break the purge rather than restore it.
        migrationBuilder.Sql("""
            DO $tenant_purge_reach_down$
            DECLARE
                target record;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                    RETURN;
                END IF;

                FOR target IN
                    SELECT n.nspname AS schema_name, c.relname AS table_name
                    FROM pg_policy p
                    JOIN pg_class c ON c.oid = p.polrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE p.polname = 'nexora_tenant_purge'
                      AND (c.relname IN ('EmailIngests', 'LeadItems', 'RFQItems', 'QuoteItems',
                                         'OrderItems', 'ShipmentItems', 'ShipmentStatusHistory',
                                         'ProductAttachments', 'SupplierPurchaseHistory',
                                         'Attachments', 'custom_field_versions',
                                         'custom_field_options', 'custom_field_rules',
                                         'custom_field_dependencies')
                           OR EXISTS (
                               SELECT 1 FROM pg_attribute a
                               WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                                 AND a.attname = 'business_unit_id'))
                LOOP
                    EXECUTE format(
                        'DROP POLICY IF EXISTS nexora_tenant_purge ON %I.%I',
                        target.schema_name, target.table_name);
                END LOOP;
            END
            $tenant_purge_reach_down$;
            """);
    }

    private static bool IsNpgsql(MigrationBuilder migrationBuilder)
        => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}
