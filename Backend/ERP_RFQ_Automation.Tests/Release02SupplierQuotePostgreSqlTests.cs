using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Release02SupplierQuotePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Supplier_quote_schema_enforces_rls_least_privilege_and_append_only_history()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" = '20260726112455_Release02SupplierQuoteInbox') = 1,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                   AND tablename = ANY(ARRAY[
                       'supplier_quotes', 'supplier_quote_revisions', 'supplier_quote_lines',
                       'supplier_quote_field_evidence', 'supplier_quote_review_decisions'])) = 5,
                has_table_privilege('nexora_tenant_app', 'public.supplier_quotes', 'SELECT,INSERT,UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.supplier_quotes', 'DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.supplier_quote_revisions', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.supplier_quote_revisions', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.supplier_quote_review_decisions', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.supplier_quote_review_decisions', 'UPDATE,DELETE'),
                (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname = ANY(ARRAY[
                    'supplier_quote_revisions_append_only', 'supplier_quote_lines_append_only',
                    'supplier_quote_field_evidence_append_only', 'supplier_quote_review_decisions_append_only',
                    'supplier_quotes_lineage_immutable',
                    'supplier_solicitations_commercial_lineage_write_once'])) = 6;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 6; index++) Assert.True(reader.GetBoolean(index));
    }


    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Pricing_bridge_schema_is_tenant_scoped_append_only_and_least_privilege()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" = '20260726114748_Release02SupplierOfferPricingBridge') = 1,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                   AND tablename = 'customer_quote_sourcing_decisions') = 1,
                has_table_privilege('nexora_tenant_app', 'public.customer_quote_sourcing_decisions', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.customer_quote_sourcing_decisions', 'UPDATE,DELETE'),
                (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname = ANY(ARRAY[
                    'customer_quote_sourcing_decisions_append_only',
                    'supplier_quoted_items_projected_lineage_immutable'])) = 2,
                EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public'
                    AND indexname = 'UX_SupplierQuotedItems_BU_SourceQuoteLine');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 5; index++) Assert.True(reader.GetBoolean(index));
    }
}
