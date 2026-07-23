using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <inheritdoc />
public partial class CompleteTenantRlsCoverage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $block$
            DECLARE
                tenant_table record;
                nullable_access text;
                tenant_setting constant text :=
                    'NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint';
            BEGIN
                FOR tenant_table IN
                    SELECT c.table_name,
                           c.column_name,
                           c.is_nullable = 'YES' AS is_nullable
                    FROM information_schema.columns c
                    WHERE c.table_schema = 'public'
                      AND c.table_name IN (
                          SELECT t.table_name
                          FROM information_schema.tables t
                          WHERE t.table_schema = 'public' AND t.table_type = 'BASE TABLE')
                      AND c.column_name IN (
                          'BusinessUnitID', 'BusinessUnitId', 'business_unit_id',
                          'BUID', 'Buid', 'buid')
                LOOP
                    nullable_access := CASE
                        WHEN tenant_table.is_nullable
                         AND tenant_table.table_name IN ('Customers', 'Suppliers', 'Products', 'Inventory')
                        THEN format('%I IS NULL OR ', tenant_table.column_name)
                        ELSE ''
                    END;

                    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', tenant_table.table_name);
                    EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', tenant_table.table_name);
                    EXECUTE format(
                        'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                        'USING (%s%I = %s) WITH CHECK (%I = %s)',
                        tenant_table.table_name,
                        nullable_access,
                        tenant_table.column_name,
                        tenant_setting,
                        tenant_table.column_name,
                        tenant_setting);
                END LOOP;
            END
            $block$;

            REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory" FROM nexora_tenant_app;

            ALTER TABLE public."BusinessUnits" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."BusinessUnits";
            CREATE POLICY nexora_tenant_isolation ON public."BusinessUnits" TO nexora_tenant_app
                USING ("ID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                WITH CHECK ("ID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

            DO $block$
            DECLARE child_table record;
            DECLARE tenant_setting constant text :=
                'NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint';
            BEGIN
                FOR child_table IN
                    SELECT * FROM (VALUES
                        ('LeadItems', 'Leads', 'LeadID'),
                        ('RFQItems', 'RFQ', 'RFQID'),
                        ('QuoteItems', 'Quotes', 'QuoteID'),
                        ('OrderItems', 'Orders', 'OrderID'),
                        ('ShipmentItems', 'Shipments', 'ShipmentID')
                    ) AS mapping(child_name, parent_name, foreign_key)
                LOOP
                    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', child_table.child_name);
                    EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', child_table.child_name);
                    EXECUTE format(
                        'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
                        'USING (EXISTS (SELECT 1 FROM public.%I parent '
                        'WHERE parent."ID" = %I.%I AND parent."BusinessUnitID" = %s)) '
                        'WITH CHECK (EXISTS (SELECT 1 FROM public.%I parent '
                        'WHERE parent."ID" = %I.%I AND parent."BusinessUnitID" = %s))',
                        child_table.child_name,
                        child_table.parent_name,
                        child_table.child_name,
                        child_table.foreign_key,
                        tenant_setting,
                        child_table.parent_name,
                        child_table.child_name,
                        child_table.foreign_key,
                        tenant_setting);
                END LOOP;
            END
            $block$;

            ALTER TABLE public."EmailIngests" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."EmailIngests";
            CREATE POLICY nexora_tenant_isolation ON public."EmailIngests" TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1 FROM public."Email_Configurations" configuration
                    WHERE configuration."ID" = "EmailIngests"."EmailConfigurationID"
                      AND configuration."BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM public."Email_Configurations" configuration
                    WHERE configuration."ID" = "EmailIngests"."EmailConfigurationID"
                      AND configuration."BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public."Attachments" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."Attachments";
            CREATE POLICY nexora_tenant_isolation ON public."Attachments" TO nexora_tenant_app
                USING ("ParentType" = 'Lead' AND EXISTS (
                    SELECT 1 FROM public."Leads" lead
                    WHERE lead."ID" = "Attachments"."ParentID"
                      AND lead."BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK ("ParentType" = 'Lead' AND EXISTS (
                    SELECT 1 FROM public."Leads" lead
                    WHERE lead."ID" = "Attachments"."ParentID"
                      AND lead."BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public."ShipmentStatusHistory" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."ShipmentStatusHistory";
            CREATE POLICY nexora_tenant_isolation ON public."ShipmentStatusHistory" TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1 FROM public."Shipments" shipment
                    WHERE shipment."ID" = "ShipmentStatusHistory"."ShipmentId"
                      AND shipment."BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM public."Shipments" shipment
                    WHERE shipment."ID" = "ShipmentStatusHistory"."ShipmentId"
                      AND shipment."BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public."ProductAttachments" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."ProductAttachments";
            CREATE POLICY nexora_tenant_isolation ON public."ProductAttachments" TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1 FROM public."Products" product
                    WHERE product."ID" = "ProductAttachments"."InventoryID"
                      AND (product."BUID" IS NULL OR product."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM public."Products" product
                    WHERE product."ID" = "ProductAttachments"."InventoryID"
                      AND product."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public."SupplierPurchaseHistory" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."SupplierPurchaseHistory";
            CREATE POLICY nexora_tenant_isolation ON public."SupplierPurchaseHistory" TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1 FROM public."Products" product, public."Suppliers" supplier
                    WHERE product."ID" = "SupplierPurchaseHistory"."ProductId"
                      AND supplier."ID" = "SupplierPurchaseHistory"."SupplierId"
                      AND (product."BUID" IS NULL OR product."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                      AND (supplier."BUID" IS NULL OR supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM public."Products" product, public."Suppliers" supplier
                    WHERE product."ID" = "SupplierPurchaseHistory"."ProductId"
                      AND supplier."ID" = "SupplierPurchaseHistory"."SupplierId"
                      AND (product."BUID" IS NULL OR product."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                      AND (supplier."BUID" IS NULL OR supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                      AND (product."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint
                           OR supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)));

            ALTER TABLE public."Contacts" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."Contacts";
            CREATE POLICY nexora_tenant_isolation ON public."Contacts" TO nexora_tenant_app
                USING (
                    ("CustomerID" IS NOT NULL OR "SupplierID" IS NOT NULL)
                    AND ("CustomerID" IS NULL OR EXISTS (
                        SELECT 1 FROM public."Customers" customer
                        WHERE customer."ID" = "Contacts"."CustomerID"
                          AND (customer."BUID" IS NULL OR customer."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)))
                    AND ("SupplierID" IS NULL OR EXISTS (
                        SELECT 1 FROM public."Suppliers" supplier
                        WHERE supplier."ID" = "Contacts"."SupplierID"
                          AND (supplier."BUID" IS NULL OR supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))))
                WITH CHECK (
                    ("CustomerID" IS NOT NULL OR "SupplierID" IS NOT NULL)
                    AND ("CustomerID" IS NULL OR EXISTS (
                        SELECT 1 FROM public."Customers" customer
                        WHERE customer."ID" = "Contacts"."CustomerID"
                          AND customer."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                    AND ("SupplierID" IS NULL OR EXISTS (
                        SELECT 1 FROM public."Suppliers" supplier
                        WHERE supplier."ID" = "Contacts"."SupplierID"
                          AND supplier."BUID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)));

            ALTER TABLE public.custom_field_versions ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_versions;
            CREATE POLICY nexora_tenant_isolation ON public.custom_field_versions TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1 FROM public.custom_field_definitions definition
                    WHERE definition."Id" = "DefinitionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM public.custom_field_definitions definition
                    WHERE definition."Id" = "DefinitionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public.custom_field_options ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_options;
            CREATE POLICY nexora_tenant_isolation ON public.custom_field_options TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1
                    FROM public.custom_field_versions version
                    JOIN public.custom_field_definitions definition ON definition."Id" = version."DefinitionId"
                    WHERE version."Id" = "VersionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK (EXISTS (
                    SELECT 1
                    FROM public.custom_field_versions version
                    JOIN public.custom_field_definitions definition ON definition."Id" = version."DefinitionId"
                    WHERE version."Id" = "VersionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public.custom_field_rules ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_rules;
            CREATE POLICY nexora_tenant_isolation ON public.custom_field_rules TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1
                    FROM public.custom_field_versions version
                    JOIN public.custom_field_definitions definition ON definition."Id" = version."DefinitionId"
                    WHERE version."Id" = "VersionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK (EXISTS (
                    SELECT 1
                    FROM public.custom_field_versions version
                    JOIN public.custom_field_definitions definition ON definition."Id" = version."DefinitionId"
                    WHERE version."Id" = "VersionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER TABLE public.custom_field_dependencies ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_dependencies;
            CREATE POLICY nexora_tenant_isolation ON public.custom_field_dependencies TO nexora_tenant_app
                USING (EXISTS (
                    SELECT 1
                    FROM public.custom_field_versions version
                    JOIN public.custom_field_definitions definition ON definition."Id" = version."DefinitionId"
                    WHERE version."Id" = "VersionId"
                      AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint))
                WITH CHECK (
                    EXISTS (
                        SELECT 1
                        FROM public.custom_field_versions version
                        JOIN public.custom_field_definitions definition ON definition."Id" = version."DefinitionId"
                        WHERE version."Id" = "VersionId"
                          AND definition."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    AND EXISTS (
                        SELECT 1 FROM public.custom_field_definitions dependency
                        WHERE dependency."Id" = "DependsOnDefinitionId"
                          AND dependency."BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint));

            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM nexora_tenant_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                REVOKE USAGE, SELECT, UPDATE ON SEQUENCES FROM nexora_tenant_app;
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM nexora_tenant_app;
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM nexora_tenant_app;
            DO $block$
            DECLARE protected_table record;
            BEGIN
                FOR protected_table IN
                    SELECT table_definition.relname
                    FROM pg_class table_definition
                    JOIN pg_namespace schema_definition ON schema_definition.oid = table_definition.relnamespace
                    WHERE schema_definition.nspname = 'public'
                      AND table_definition.relkind IN ('r', 'p')
                      AND table_definition.relrowsecurity
                LOOP
                    EXECUTE format(
                        'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO nexora_tenant_app',
                        protected_table.relname);
                END LOOP;
            END
            $block$;

            DO $block$
            DECLARE protected_sequence record;
            BEGIN
                FOR protected_sequence IN
                    SELECT DISTINCT sequence_definition.relname
                    FROM pg_class sequence_definition
                    JOIN pg_namespace schema_definition ON schema_definition.oid = sequence_definition.relnamespace
                    JOIN pg_depend dependency ON dependency.objid = sequence_definition.oid
                    JOIN pg_class table_definition ON table_definition.oid = dependency.refobjid
                    WHERE schema_definition.nspname = 'public'
                      AND sequence_definition.relkind = 'S'
                      AND dependency.deptype IN ('a', 'i')
                      AND table_definition.relrowsecurity
                LOOP
                    EXECUTE format(
                        'GRANT USAGE ON SEQUENCE public.%I TO nexora_tenant_app',
                        protected_sequence.relname);
                END LOOP;
            END
            $block$;
            GRANT USAGE ON SEQUENCE public."CommercialCaseReferenceSequence" TO nexora_tenant_app;

            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_versions;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_options;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_rules;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public.custom_field_dependencies;
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."BusinessUnits";
            ALTER TABLE public.custom_field_versions DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public.custom_field_options DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public.custom_field_rules DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public.custom_field_dependencies DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."BusinessUnits" DISABLE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."EmailIngests";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."Attachments";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."ShipmentStatusHistory";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."ProductAttachments";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."SupplierPurchaseHistory";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."Contacts";
            ALTER TABLE public."EmailIngests" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."Attachments" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."ShipmentStatusHistory" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."ProductAttachments" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."SupplierPurchaseHistory" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."Contacts" DISABLE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."LeadItems";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."RFQItems";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."QuoteItems";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."OrderItems";
            DROP POLICY IF EXISTS nexora_tenant_isolation ON public."ShipmentItems";
            ALTER TABLE public."LeadItems" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."RFQItems" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."QuoteItems" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."OrderItems" DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public."ShipmentItems" DISABLE ROW LEVEL SECURITY;

            DO $block$
            DECLARE tenant_table record;
            BEGIN
                FOR tenant_table IN
                    SELECT c.table_name
                    FROM information_schema.columns c
                    WHERE c.table_schema = 'public'
                      AND c.column_name IN (
                          'BusinessUnitID', 'BusinessUnitId', 'business_unit_id',
                          'BUID', 'Buid', 'buid')
                      AND c.table_name NOT IN (
                          'Leads', 'RFQ', 'Quotes', 'Orders', 'Shipments', 'CommercialCases',
                          'LeadStatusHistories', 'commercial_lifecycle_events',
                          'lifecycle_outbox_messages', 'document_corpora', 'source_documents',
                          'document_pages', 'document_regions', 'canonical_inquiries',
                          'canonical_line_items', 'field_evidence')
                LOOP
                    EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', tenant_table.table_name);
                    EXECUTE format('ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY', tenant_table.table_name);
                END LOOP;
            END
            $block$;

            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO nexora_tenant_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexora_tenant_app;
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO nexora_tenant_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO nexora_tenant_app;
            REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory" FROM nexora_tenant_app;
            """);
    }
}
