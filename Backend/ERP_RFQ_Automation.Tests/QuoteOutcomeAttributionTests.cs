using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A won quote must be credited to somebody, or say why it was not.
///
/// <para><b>The defect.</b> <c>QuoteOutcomeService.AppendSalesActivityAsync</c> returned
/// silently whenever the quote's lead had no owner: <c>if (attribution?.OwnerUserId is not > 0)
/// return;</c>. No log, no field, no row. On the live tenant 230 of 238 leads are unowned, so
/// this was the normal path: every Won, Lost and CustomerResponded activity was dropped and
/// <c>commercial_activities</c> is empty on production. It is the outcome-side twin of the
/// silent return in <c>QuoteService.RecordQuoteSentWorkAsync</c>.</para>
///
/// <para>The tenant already names a fallback owner for routing
/// (<c>BusinessUnit.DefaultLeadOwnerUserId</c>). The outcome path now reads the same setting,
/// and when even that is unset it records a warning naming the gap instead of nothing.</para>
/// </summary>
public sealed class QuoteOutcomeAttributionTests
{
    private const long Tenant = 9_831;
    private const long DefaultOwnerId = 98_311;
    private const long LeadId = 98_312;
    private const long RfqId = 98_313;
    private const long QuoteId = 98_314;
    private const long SentStatusId = 98_315;
    private const long AcceptedStatusId = 98_316;

    [Fact]
    public async Task A_won_quote_on_an_unowned_lead_is_credited_to_the_tenants_fallback_owner()
    {
        // PRODUCTION'S SHAPE: lead unowned (AssignTo NULL), quote SENT, no outcome yet.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        await SeedAsync(context, defaultOwner: DefaultOwnerId);
        var logger = new CapturingLogger<QuoteOutcomeService>();
        var service = Service(context, logger);

        await service.SetOutcomeAsync(QuoteId, Tenant, "rep@tenant.test", "won");

        context.ChangeTracker.Clear();
        var activities = await context.Set<CommercialActivity>().AsNoTracking()
            .Where(x => x.AggregateType == "Quote" && x.AggregateId == QuoteId)
            .ToListAsync();
        var won = Assert.Single(activities, x => x.ActivityType == CommercialActivityType.Won);
        Assert.Equal(DefaultOwnerId, won.SalesRepUserId);
        Assert.Contains(activities, x => x.ActivityType == CommercialActivityType.CustomerResponded);
        Assert.DoesNotContain(logger.Entries, x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_won_quote_with_nobody_to_credit_says_so_instead_of_dropping_the_outcome()
    {
        // No lead owner AND no tenant fallback: there is genuinely nobody to credit. The outcome
        // itself is still recorded on the quote; the missing attribution is now a named warning.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        await SeedAsync(context, defaultOwner: null);
        var logger = new CapturingLogger<QuoteOutcomeService>();
        var service = Service(context, logger);

        await service.SetOutcomeAsync(QuoteId, Tenant, "rep@tenant.test", "won");

        context.ChangeTracker.Clear();
        Assert.NotNull((await context.Quotes.AsNoTracking().SingleAsync(x => x.Id == QuoteId)).OutcomeOn);
        Assert.Empty(await context.Set<CommercialActivity>().AsNoTracking().ToListAsync());
        // One warning per activity that could not be credited (CustomerResponded, Won), each
        // naming the setup gap that fixes it.
        var warnings = logger.Entries.Where(x => x.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, x => Assert.Contains("fallback lead owner", x.Message));
    }

    // ------------------------------------------------------------------------ test plumbing

    private static QuoteOutcomeService Service(ErpRfqAutomationContext context, ILogger<QuoteOutcomeService> logger)
        => new(context, new QuoteService(context, null!, null!), logger,
            sales: new SalesApplicationService(new EfSalesPersistence(context)));

    private static async Task SeedAsync(ErpRfqAutomationContext context, long? defaultOwner)
    {
        var bu = Seed.EnsureBusinessUnit(context, Tenant);
        bu.DefaultLeadOwnerUserId = defaultOwner;
        context.Users.Add(new User
        {
            Id = DefaultOwnerId, FirstName = "Fallback", LastName = "Owner", Email = "fallback@tenant.test",
            PasswordHash = "x", ImageUrl = "n/a", Buid = Tenant, IsActive = true,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
                SetupCode = "SENT", SetupValue = "Sent", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            },
            new SetupMaster
            {
                SetupId = AcceptedStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
                SetupCode = "ACCEPTED", SetupValue = "Accepted", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            });
        var lead = Seed.Lead(context, LeadId, Tenant);
        lead.AssignTo = null;
        await context.SaveChangesAsync();

        context.Rfqs.Add(new Rfq
        {
            Id = RfqId, Rfqno = $"RFQ-{RfqId}", RecDate = DateTime.UtcNow.AddDays(-10), LeadId = LeadId,
            BusinessUnitId = Tenant, CreatedBy = "seed", CreatedDate = DateTime.UtcNow.AddDays(-10)
        });
        context.Quotes.Add(new Quote
        {
            Id = QuoteId, QuoteNo = "QT-0826-0002", BusinessUnitId = Tenant, Rfqid = RfqId,
            StatusId = SentStatusId, QuoteDate = DateTime.UtcNow.AddDays(-5),
            ValidUntil = DateTime.UtcNow.AddDays(25), SentOn = DateTime.UtcNow.AddDays(-5),
            TotalAmount = 460_460m, CreatedBy = "seed", CreatedDate = DateTime.UtcNow.AddDays(-5)
        });
        await context.SaveChangesAsync();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
