using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What <c>modelBuilder.ApplyPlatformSupportModel()</c> does to the rest of the model.
///
/// <para>Two standing guards decide whether a new table is safe: <c>TenantIsolationTests</c> fails
/// any entity carrying a tenant-data column (<c>BusinessUnitId</c> / <c>Buid</c>) without a global
/// query filter, and <c>PostgreSqlProductionDialectTests</c> fails any PUBLIC-schema entity that
/// carries a query filter without a matching <c>nexora_tenant_isolation</c> RLS policy. The support
/// tables satisfy both by being what they actually are — control-plane records ABOUT a tenant,
/// living in the platform schema and keyed by <c>TenantId</c>, exactly as
/// <c>platform.ImpersonationSessions</c> is.</para>
/// </summary>
public sealed class PlatformSupportModelContractTests
{
    private static readonly Type[] SupportEntities =
        [typeof(SupportTicket), typeof(SupportTicketNote), typeof(SupportTicketLink)];

    [Fact]
    public void Every_support_table_lives_in_the_platform_schema()
    {
        var model = ProductionModelWithSupportDesk();

        foreach (var clrType in SupportEntities)
            Assert.Equal("platform", model.FindEntityType(clrType)!.GetSchema());
    }

    [Fact]
    public void No_support_entity_carries_a_tenant_data_column_name()
    {
        // TenantIsolationTests keys its sweep on these two names. The support tables reference a
        // tenant without BELONGING to one — the same distinction that keeps ImpersonationSessions
        // and TenantAdminInvitations out of the sweep — so the column is TenantId.
        var model = ProductionModelWithSupportDesk();

        foreach (var clrType in SupportEntities)
        {
            var names = model.FindEntityType(clrType)!.GetProperties().Select(p => p.Name).ToArray();
            Assert.DoesNotContain("BusinessUnitId", names);
            Assert.DoesNotContain("Buid", names);
        }
    }

    [Fact]
    public void No_support_entity_carries_a_global_query_filter()
    {
        // A filter here would enrol the table in the RLS-policy expectation asserted by
        // PostgreSqlProductionDialectTests, which no operator-plane table can satisfy: the request
        // that reads it holds no nexora.business_unit_id, so there is nothing to key a policy on.
        var model = ProductionModelWithSupportDesk();

        foreach (var clrType in SupportEntities)
            Assert.Null(model.FindEntityType(clrType)!.GetQueryFilter());
    }

    [Fact]
    public void The_tenant_relationship_is_restrict_so_a_purge_cannot_silently_take_the_history()
    {
        var model = ProductionModelWithSupportDesk();
        var ticket = model.FindEntityType(typeof(SupportTicket))!;

        var tenantFk = Assert.Single(ticket.GetForeignKeys(),
            fk => fk.Properties.Any(p => p.Name == nameof(SupportTicket.TenantId)));
        Assert.Equal(DeleteBehavior.Restrict, tenantFk.DeleteBehavior);

        // Notes and links have no meaning apart from their ticket, and the ticket row itself is
        // never deleted, so their cascade can only ever fire from the erasure path.
        foreach (var clrType in new[] { typeof(SupportTicketNote), typeof(SupportTicketLink) })
            Assert.Equal(DeleteBehavior.Cascade,
                Assert.Single(model.FindEntityType(clrType)!.GetForeignKeys()).DeleteBehavior);
    }

    [Fact]
    public void Enum_columns_are_stored_as_names_rather_than_ordinals()
    {
        // A ticket closed two years ago has to stay explainable, and reordering an enum must never
        // silently reclassify history that has already been quoted to a customer.
        var model = ProductionModelWithSupportDesk();

        foreach (var (clrType, property) in new[]
                 {
                     (typeof(SupportTicket), nameof(SupportTicket.Status)),
                     (typeof(SupportTicket), nameof(SupportTicket.Severity)),
                     (typeof(SupportTicket), nameof(SupportTicket.Origin)),
                     (typeof(SupportTicketNote), nameof(SupportTicketNote.AuthorKind)),
                     (typeof(SupportTicketLink), nameof(SupportTicketLink.Kind))
                 })
        {
            var column = model.FindEntityType(clrType)!.FindProperty(property)!;
            Assert.Equal(typeof(string), column.GetProviderClrType());
            Assert.NotNull(column.GetMaxLength());
        }
    }

    [Fact]
    public void The_ticket_version_is_a_real_concurrency_token()
    {
        var model = ProductionModelWithSupportDesk();

        Assert.True(model.FindEntityType(typeof(SupportTicket))!
            .FindProperty(nameof(SupportTicket.Version))!.IsConcurrencyToken);
    }

    [Fact]
    public void The_query_shapes_the_module_depends_on_translate_to_the_production_dialect()
    {
        // The defect class this pins is the expensive one on this codebase: a query that is green
        // on the SQLite lane and fails on PostgreSQL, where nobody sees it until a request 500s.
        // Both shapes below filter an enum stored through a VALUE CONVERTER — the case where
        // PostgreSQL's `= ANY(@p)` has to be fed converted strings rather than ordinals — and the
        // third groups by one. ToQueryString() renders real Npgsql SQL without a connection, so an
        // untranslatable predicate throws here rather than in production.
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        using var context = new ErpRfqAutomationContext(options);

        var requested = new[] { SupportTicketStatus.New, SupportTicketStatus.Open };
        var byRequestedStatus = context.Set<SupportTicket>()
            .Where(t => requested.Contains(t.Status)).ToQueryString();
        Assert.Contains("""s."Status" = ANY (@""", byRequestedStatus);

        var live = context.Set<SupportTicket>()
            .Where(t => SupportTicketLifecycle.Live.Contains(t.Status)).ToQueryString();
        Assert.Contains("""IN ('New', 'Open', 'Pending')""", live);

        var grouped = context.Set<SupportTicket>()
            .GroupBy(t => t.Severity)
            .Select(g => new { g.Key, Count = g.Count() }).ToQueryString();
        Assert.Contains("""GROUP BY s."Severity" """.TrimEnd(), grouped);
    }

    /// <summary>
    /// The PRODUCTION (Npgsql) model, exactly as it ships. Npgsql rather than SQLite for
    /// <c>TenantIsolationTests.ProductionModel</c>'s reason: several entities are configured inside
    /// <c>if (Database.IsNpgsql())</c>, so the SQLite model is not the model that ships.
    /// </summary>
    private static IModel ProductionModelWithSupportDesk()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        using var context = new ErpRfqAutomationContext(options);
        return context.Model;
    }
}
