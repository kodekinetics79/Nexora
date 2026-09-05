using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The production dialect half of the revision defect (docs/audit/SCENARIOS-QUOTE-TO-CASH-2026-09-05.md).
///
/// <para>Two things stopped every POST /api/Quote/{id}/revise on PostgreSQL, and only this dialect
/// can prove either. First, Program.cs configures EnableRetryOnFailure, so
/// NpgsqlRetryingExecutionStrategy refused the user-initiated transaction ReviseQuoteAsync opened
/// outside its delegate: 409 "The configured execution strategy 'NpgsqlRetryingExecutionStrategy'
/// does not support user-initiated transactions", before the quote was even read. Second, the
/// partial unique index UX_Quotes_BusinessUnitID_RFQID (one quote per RFQ per tenant, raw SQL in
/// the squashed baseline, absent from SQLite) refused the revision row itself, because a revision
/// is a second quote on the same RFQ. Migration 20260905093000_ScopeOneQuotePerRfqToOriginalQuotes
/// scopes that index to original quotes.</para>
///
/// <para>Verified by reverting: with the migration file removed this test fails with the
/// "one quote per RFQ" sentence QuoteService maps the index violation to; with it, the revision
/// is written. The SQLite tests in QuoteToCashScenarioRegressionTests pass either way, which is
/// the control that shows the suite is not simply always-red.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class QuoteRevisionPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 96_401;
    private const long DraftStatusId = 96_402;
    private const long SentStatusId = 96_403;
    private const long CustomerId = 96_404;
    private const long LeadId = 96_405;
    private const long RfqId = 96_406;
    private const long QuoteId = 96_407;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_sent_quote_is_revised_on_PostgreSQL_into_a_second_row_on_the_same_RFQ()
    {
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            foreach (var (id, code, value) in new[] { (DraftStatusId, "DRAFT", "Draft"), (SentStatusId, "SENT", "Sent") })
                seed.SetupMasters.Add(new SetupMaster
                {
                    SetupId = id, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = code,
                    SetupValue = value, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
                });
            var lead = Seed.Lead(seed, LeadId, Tenant);
            Seed.Customer(seed, CustomerId, Tenant, "Revision Customer");
            lead.ResolveCommercialIdentity(CustomerId, null, "MATCHED");
            await seed.SaveChangesAsync();
            // CK_RFQ_LeadPromotionLineage: an RFQ that names its lead must carry the whole promotion
            // lineage. The revision defect is about the quote row, so the RFQ inherits the lead's
            // commercial case without naming the lead.
            var rfq = new Rfq
            {
                Id = RfqId, Rfqno = "RFQ-REVISE-PG", RecDate = DateTime.UtcNow,
                CustomerId = CustomerId, BusinessUnitId = Tenant, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            };
            rfq.InheritCommercialIdentity(lead);
            seed.Rfqs.Add(rfq);
            await seed.SaveChangesAsync();
            var quote = new Quote
            {
                Id = QuoteId, QuoteNo = "QT-REVISE-PG", Rfqid = RfqId, CustomerId = CustomerId, BusinessUnitId = Tenant,
                StatusId = SentStatusId, QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
                SentOn = DateTime.UtcNow.AddDays(-1), TotalAmount = 115m, LifecycleVersion = 1, RevisionNo = 1,
                CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            };
            quote.InheritCommercialIdentity(rfq);
            quote.QuoteItems.Add(new QuoteItem
            {
                ItemDescription = "Gasket spiral wound", Quantity = 10m, UnitOfMeasure = "EA", UnitPrice = 10m,
                TaxAmount = 15m, TaxRatePercentApplied = 15m, TotalAmount = 115m,
                TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
                CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            });
            seed.Quotes.Add(quote);
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var context = database.ContextFor(Tenant);
            var service = new QuoteService(context, new SilentEmailService(), null!);

            var revision = await service.ReviseQuoteAsync(QuoteId, Tenant, "rep@nexora.invalid");

            // Neither trap fired: not the retrying strategy, not the one-quote-per-RFQ index.
            Assert.Equal(2, revision.Version);
            Assert.Equal("QT-REVISE-PG-R2", revision.QuoteNo);
            Assert.Equal("DRAFT", revision.StatusCode);
            context.ChangeTracker.Clear();
            // Two rows on the one RFQ: the seeded original and the revision that points back at it.
            var rows = await context.Quotes.AsNoTracking().Where(q => q.Rfqid == RfqId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Null(Assert.Single(rows, r => r.Id == QuoteId).RevisionOfQuoteId);
            var written = Assert.Single(rows, r => r.Id == revision.Id);
            Assert.Equal(QuoteId, written.RevisionOfQuoteId);
            Assert.Equal(2, written.RevisionNo);
            // The old quote reports its successor, so the screen can say "superseded by".
            var info = await service.GetRevisionInfoAsync(QuoteId, Tenant);
            Assert.Equal(revision.Id, info.SupersededByQuoteId);
            Assert.False(info.CanRevise);
        }
        finally
        {
            await using var cleanup = database.ContextFor(null);
            await cleanup.Database.ExecuteSqlRawAsync("""DELETE FROM "QuoteItems" WHERE "QuoteID" IN (SELECT "ID" FROM "Quotes" WHERE "BusinessUnitID" = {0})""", Tenant);
            await cleanup.Database.ExecuteSqlRawAsync("""DELETE FROM "Quotes" WHERE "BusinessUnitID" = {0}""", Tenant);
        }
    }

    private sealed class SilentEmailService : IEmailService
    {
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }
}
