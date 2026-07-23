using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        CREATE ROLE nexora_tenant_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                    END IF;
                    EXECUTE format('GRANT nexora_tenant_app TO %I', current_user);
                END
                $block$;

                GRANT USAGE ON SCHEMA public TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO nexora_tenant_app;
                GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO nexora_tenant_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexora_tenant_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO nexora_tenant_app;

                ALTER TABLE "Leads" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "Leads"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "RFQ" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "RFQ"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "Quotes" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "Quotes"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "Orders" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "Orders"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "Shipments" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "Shipments"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "CommercialCases" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "CommercialCases"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE "LeadStatusHistories" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON "LeadStatusHistories"
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE commercial_lifecycle_events ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON commercial_lifecycle_events
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE lifecycle_outbox_messages ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON lifecycle_outbox_messages
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE document_corpora ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON document_corpora
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE source_documents ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON source_documents
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE document_pages ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON document_pages
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE document_regions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON document_regions
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE canonical_inquiries ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON canonical_inquiries
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE canonical_line_items ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON canonical_line_items
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE field_evidence ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON field_evidence
                    USING (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK (business_unit_id = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Leads";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "RFQ";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Quotes";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Orders";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "Shipments";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "CommercialCases";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON "LeadStatusHistories";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON commercial_lifecycle_events;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON lifecycle_outbox_messages;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON document_corpora;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON source_documents;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON document_pages;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON document_regions;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON canonical_inquiries;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON canonical_line_items;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON field_evidence;

                ALTER TABLE "Leads" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE "RFQ" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE "Quotes" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE "Orders" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE "Shipments" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE "CommercialCases" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE "LeadStatusHistories" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE commercial_lifecycle_events DISABLE ROW LEVEL SECURITY;
                ALTER TABLE lifecycle_outbox_messages DISABLE ROW LEVEL SECURITY;
                ALTER TABLE document_corpora DISABLE ROW LEVEL SECURITY;
                ALTER TABLE source_documents DISABLE ROW LEVEL SECURITY;
                ALTER TABLE document_pages DISABLE ROW LEVEL SECURITY;
                ALTER TABLE document_regions DISABLE ROW LEVEL SECURITY;
                ALTER TABLE canonical_inquiries DISABLE ROW LEVEL SECURITY;
                ALTER TABLE canonical_line_items DISABLE ROW LEVEL SECURITY;
                ALTER TABLE field_evidence DISABLE ROW LEVEL SECURITY;

                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM nexora_tenant_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    REVOKE USAGE, SELECT, UPDATE ON SEQUENCES FROM nexora_tenant_app;
                REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM nexora_tenant_app;
                REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM nexora_tenant_app;
                REVOKE USAGE ON SCHEMA public FROM nexora_tenant_app;
                DROP ROLE IF EXISTS nexora_tenant_app;
                """);
        }
    }
}
