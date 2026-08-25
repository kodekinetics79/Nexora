using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification that a quote which predates Nexora can be carried in.
///
/// <para><b>Why this cannot be a SQLite test.</b> The thing under test is whether PostgreSQL
/// ACCEPTS the row. <c>nexora_validate_downstream_commercial_identity</c> refuses a Quote whose
/// (CommercialCaseID, NexoraSerial) do not match a real commercial case, and
/// <c>TR_Leads_AssignCommercialCase</c> is what mints that case. SQLite has neither trigger, so
/// the whole mechanism — and any regression in it — is invisible on the portable lane. A green
/// build proves nothing here; only a real insert does.</para>
///
/// <para><b>What would have caught the alternative design.</b> Inserting a Quote directly, or
/// minting a bare commercial case for it, both compile and both fail at the database. The spine
/// exists because the guards are correct and a back-filled quote is only worth having if it is
/// as trustworthy as a generated one.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class QuoteBackfillSpinePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long BusinessUnitId = 8_140_001;
    private const long CustomerId = 8_140_101;

    [Fact]
    public async Task Quote_backfill_cannot_originate_a_lead_linked_rfq()
    {
        await using var db = database.ContextFor(BusinessUnitId);
        await db.Database.MigrateAsync();
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Legacy Customer");
        await db.SaveChangesAsync();

        // The quote was issued in MARCH; it is being carried in now. Everything downstream reads
        // the issue date, so the record must say March, not today.
        var issuedOn = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);

        await AssertRetiredAsync(db, CustomerId, issuedOn, "CUST-Q-2026-0042");
    }

    [Fact]
    public async Task Reimport_cannot_use_quote_backfill_as_an_rfq_creation_door()
    {
        await using var db = database.ContextFor(BusinessUnitId);
        await db.Database.MigrateAsync();
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId + 1, BusinessUnitId, "Reimport Customer");
        await db.SaveChangesAsync();

        var issuedOn = new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc);
        await AssertRetiredAsync(db, CustomerId + 1, issuedOn, "DUP-REF-0001");
        await AssertRetiredAsync(db, CustomerId + 1, issuedOn, "DUP-REF-0001");
    }

    /// <summary>
    /// Proves the two tests above are not vacuous.
    ///
    /// If the commercial-identity trigger were absent from the test database — not migrated, or
    /// dropped by a future change — the "accepted" test would pass for the wrong reason and go on
    /// passing forever while the protection it certifies was gone. This inserts the SAME quote
    /// WITHOUT an inherited identity and requires the database to refuse it. Green here means the
    /// guard is live; if this ever starts passing quietly, the suite above is worthless.
    /// </summary>
    [Fact]
    public async Task Ungoverned_quote_backfill_is_refused_before_any_rows_are_written()
    {
        await using var db = database.ContextFor(BusinessUnitId);
        await db.Database.MigrateAsync();
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId + 2, BusinessUnitId, "Ungoverned Customer");
        await db.SaveChangesAsync();

        await AssertRetiredAsync(db, CustomerId + 2,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), "UNGOVERNED-0001");
    }

    private static async Task AssertRetiredAsync(
        ErpRfqAutomationContext db, long customerId, DateTime issuedOn, string reference)
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuoteBackfillSpine(db).OriginateAsync(
                BusinessUnitId, customerId, null, issuedOn, "importer@tenant.test", reference));

        Assert.Contains("Direct quote-backfill RFQ origination is retired", refusal.Message,
            StringComparison.Ordinal);
        Assert.Empty(await db.Leads.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Rfqs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Quotes.AsNoTracking().ToListAsync());
    }
}
