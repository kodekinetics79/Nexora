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
    public async Task A_quote_that_predates_Nexora_is_accepted_by_the_commercial_identity_trigger()
    {
        await using var db = database.ContextFor(BusinessUnitId);
        await db.Database.MigrateAsync();
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Legacy Customer");
        await db.SaveChangesAsync();

        // The quote was issued in MARCH; it is being carried in now. Everything downstream reads
        // the issue date, so the record must say March, not today.
        var issuedOn = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);

        var rfq = await new QuoteBackfillSpine(db).OriginateAsync(
            BusinessUnitId, CustomerId, null, issuedOn, "importer@tenant.test", "CUST-Q-2026-0042");

        // The spine's whole purpose: an RFQ carrying a real commercial identity, so a Quote may
        // inherit one the trigger will accept.
        Assert.NotNull(rfq.CommercialCaseId);
        Assert.False(string.IsNullOrWhiteSpace(rfq.NexoraSerial));
        Assert.Equal(CustomerId, rfq.CustomerId);

        var lead = await db.Leads.AsNoTracking().SingleAsync(x => x.Id == rfq.LeadId);
        Assert.Equal(QuoteBackfillSpine.LeadSourceBackfill, lead.LeadSource);

        // The serial is minted from the lead's source and ORIGINAL date, so a March quote
        // imported in August is issued a March-dated reference rather than today's.
        Assert.Equal(rfq.NexoraSerial, lead.CommercialCaseReference);
        Assert.Contains("2026", lead.CommercialCaseReference);
        Assert.Equal(issuedOn.Date, lead.CreatedDate.Date);

        // The row the trigger judges. If any part of the identity is wrong this SaveChanges throws.
        var quote = new Quote
        {
            QuoteNo = "QT-BACKFILL-0001",
            Rfqid = rfq.Id,
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            QuoteDate = issuedOn,
            StatusId = null,
            TotalAmount = 12_345.67m,
            ExternalQuoteReference = "CUST-Q-2026-0042",
            Origin = QuoteOrigin.Backfill,
            CreatedBy = "importer@tenant.test",
            CreatedDate = issuedOn,
            FinancialCalculationVersion = 2,
        };
        quote.InheritCommercialIdentity(rfq);
        db.Quotes.Add(quote);

        await db.SaveChangesAsync();   // <- the assertion that matters

        var stored = await db.Quotes.AsNoTracking().SingleAsync(x => x.Id == quote.Id);
        Assert.Equal(QuoteOrigin.Backfill, stored.Origin);
        Assert.Equal("CUST-Q-2026-0042", stored.ExternalQuoteReference);
        Assert.Equal(rfq.NexoraSerial, stored.NexoraSerial);
    }

    [Fact]
    public async Task The_customers_own_reference_is_unique_per_tenant_so_a_reimport_cannot_duplicate()
    {
        await using var db = database.ContextFor(BusinessUnitId);
        await db.Database.MigrateAsync();
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId + 1, BusinessUnitId, "Reimport Customer");
        await db.SaveChangesAsync();

        var spine = new QuoteBackfillSpine(db);
        var issuedOn = new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc);

        async Task<Quote> CarryInAsync(string quoteNo)
        {
            var rfq = await spine.OriginateAsync(
                BusinessUnitId, CustomerId + 1, null, issuedOn, "importer@tenant.test", "DUP-REF-0001");
            var q = new Quote
            {
                QuoteNo = quoteNo, Rfqid = rfq.Id, CustomerId = CustomerId + 1,
                BusinessUnitId = BusinessUnitId, QuoteDate = issuedOn, TotalAmount = 10m,
                ExternalQuoteReference = "DUP-REF-0001", Origin = QuoteOrigin.Backfill,
                CreatedBy = "importer@tenant.test", CreatedDate = issuedOn, FinancialCalculationVersion = 2,
            };
            q.InheritCommercialIdentity(rfq);
            db.Quotes.Add(q);
            await db.SaveChangesAsync();
            return q;
        }

        await CarryInAsync("QT-DUP-1");

        // Re-uploading a corrected file is the NORMAL way an import gets fixed. The customer's own
        // number is the identity, so the second write must be refused by the database rather than
        // quietly producing a second quote for one real-world document.
        var again = await Assert.ThrowsAnyAsync<DbUpdateException>(() => CarryInAsync("QT-DUP-2"));
        Assert.Contains("ExternalQuoteReference", again.InnerException?.Message ?? again.Message,
            StringComparison.OrdinalIgnoreCase);
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
    public async Task A_quote_with_no_inherited_identity_is_REFUSED_which_proves_the_guard_is_live()
    {
        await using var db = database.ContextFor(BusinessUnitId);
        await db.Database.MigrateAsync();
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId + 2, BusinessUnitId, "Ungoverned Customer");
        await db.SaveChangesAsync();

        var rfq = await new QuoteBackfillSpine(db).OriginateAsync(
            BusinessUnitId, CustomerId + 2, null, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            "importer@tenant.test", "UNGOVERNED-0001");

        var quote = new Quote
        {
            QuoteNo = "QT-UNGOVERNED-1",
            Rfqid = rfq.Id,
            CustomerId = CustomerId + 2,
            BusinessUnitId = BusinessUnitId,
            QuoteDate = DateTime.UtcNow,
            TotalAmount = 1m,
            Origin = QuoteOrigin.Backfill,
            CreatedBy = "importer@tenant.test",
            CreatedDate = DateTime.UtcNow,
            FinancialCalculationVersion = 2,
            // NOTE: InheritCommercialIdentity is deliberately NOT called. A serial is set by hand
            // that belongs to no commercial case, which is precisely what the trigger exists to stop.
        };
        typeof(Quote).GetProperty(nameof(Quote.NexoraSerial))!
            .SetValue(quote, "NOT-A-REAL-SERIAL");
        typeof(Quote).GetProperty(nameof(Quote.CommercialCaseId))!
            .SetValue(quote, rfq.CommercialCaseId);
        db.Quotes.Add(quote);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
