using System.Security.Claims;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.CommercialCases;

/// <summary>
/// A case that ends BEFORE a quotation exists must say why. These cover the same ground the quote
/// path already covers: the server refuses a loss with no reason, the reason comes from ONE governed
/// picklist shared with the quote outcome, the reason/note/date persist, and the loss is visible to
/// the surface that reports win/loss.
/// </summary>
public sealed class LeadOutcomeCaptureTests
{
    private const long Tenant = 6_100;

    [Fact]
    public async Task LeadLostWithoutAReasonIsRefusedAndNothingIsRecorded()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedLead(context, leadId: 6_101);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context)
            .TransitionLeadAsync(Tenant, 6_101, Actor(),
                Command("DISQUALIFIED", 1, "lead-6101-lost", reasonCode: null), false, default));

        Assert.Contains("reason", error.Message, StringComparison.OrdinalIgnoreCase);
        context.ChangeTracker.Clear();
        var lead = await context.Leads.SingleAsync(x => x.Id == 6_101);
        Assert.Null(lead.OutcomeOn);
        Assert.Null(lead.OutcomeReasonId);
        Assert.Empty(context.CommercialLifecycleEvents);
    }

    [Fact]
    public async Task LeadLostWithAnUngovernedReasonIsRefused()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedLead(context, leadId: 6_102);
        OutcomeReason(context, ReasonId(6_102), Tenant, "PRICE", "Price too high");
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context)
            .TransitionLeadAsync(Tenant, 6_102, Actor(),
                Command("DISQUALIFIED", 1, "lead-6102-lost", "WE_JUST_FELT_LIKE_IT"), false, default));

        Assert.Contains("governed outcome reasons", error.Message);
        context.ChangeTracker.Clear();
        Assert.Null((await context.Leads.SingleAsync(x => x.Id == 6_102)).OutcomeOn);
        Assert.Empty(context.CommercialLifecycleEvents);
    }

    [Fact]
    public async Task LeadLostWithAGovernedReasonPersistsReasonNoteAndDate()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedLead(context, leadId: 6_103);
        var reason = OutcomeReason(context, ReasonId(6_103), Tenant, "NO_STOCK", "Item unavailable");
        await context.SaveChangesAsync();
        var before = DateTime.UtcNow;

        await Service(context).TransitionLeadAsync(Tenant, 6_103, Actor(),
            Command("DISQUALIFIED", 1, "lead-6103-lost", "NO_STOCK", "Nobody stocks this alloy."),
            false, default);

        context.ChangeTracker.Clear();
        var lead = await context.Leads.SingleAsync(x => x.Id == 6_103);
        Assert.Equal(reason.SetupId, lead.OutcomeReasonId);
        Assert.Equal("Nobody stocks this alloy.", lead.OutcomeNote);
        Assert.NotNull(lead.OutcomeOn);
        Assert.InRange(lead.OutcomeOn!.Value, before, DateTime.UtcNow);
        var recorded = await context.CommercialLifecycleEvents.SingleAsync();
        Assert.Equal("DISQUALIFIED", recorded.NewStatusCode);
        Assert.Equal("NO_STOCK", recorded.ReasonCode);
    }

    [Fact]
    public async Task LeadOutcomeReasonsAreTheSameGovernedPicklistAsTheQuotePath()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        // The quote path owns the defaults and seeds them on demand; the lead path must read the
        // SAME rows rather than seeding a vocabulary of its own.
        var quoteOutcomes = new QuoteOutcomeService(context, null!, new NoopLogger<QuoteOutcomeService>());
        var leadReasons = new LeadOutcomeReasons(context, Provider(quoteOutcomes));

        var fromLead = await leadReasons.GetAsync(Tenant);
        var fromQuote = await quoteOutcomes.GetOutcomeReasonsAsync(Tenant);

        Assert.Equal(fromQuote.Select(x => (x.Id, x.Code, x.Label)), fromLead.Select(x => (x.Id, x.Code, x.Label)));
        Assert.Equal(
            new[] { "AUTO_EXPIRED", "CUSTOMER_CANCELLED", "LEAD_TIME", "LOST_COMPETITOR", "NO_RESPONSE", "NO_STOCK", "OTHER", "PRICE" },
            fromLead.Select(x => x.Code).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Single(await context.SetupMasters.IgnoreQueryFilters()
            .Where(x => x.SetupType == "QuoteOutcomeReason" && x.SetupCode == "PRICE").ToListAsync());

        // A reason the tenant added itself is part of the same list, and usable on a lead.
        OutcomeReason(context, 6_104_100, Tenant, "BUDGET_FROZEN", "Customer budget frozen");
        await context.SaveChangesAsync();
        Assert.Contains(await leadReasons.GetAsync(Tenant), x => x.Code == "BUDGET_FROZEN");
        Assert.Equal(6_104_100, await leadReasons.ResolveAsync(Tenant, "BUDGET_FROZEN"));
    }

    [Fact]
    public async Task AGovernedReasonBelongingToAnotherTenantIsNotResolvable()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Tenant);
        OutcomeReason(context, 6_105_100, businessUnitId: 6_900, code: "PRICE", label: "Price too high");
        await context.SaveChangesAsync();

        var reasons = new LeadOutcomeReasons(context);

        Assert.Null(await reasons.ResolveAsync(Tenant, "PRICE"));
        Assert.Equal(6_105_100, await reasons.ResolveAsync(6_900, "PRICE"));
        Assert.Empty(await reasons.GetAsync(Tenant));
    }

    [Fact]
    public async Task ReopeningALostLeadClearsTheOutcomeSoItStopsCountingAsALoss()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedLead(context, leadId: 6_106);
        OutcomeReason(context, ReasonId(6_106), Tenant, "NO_RESPONSE", "No response");
        await context.SaveChangesAsync();
        var service = Service(context);
        await service.TransitionLeadAsync(Tenant, 6_106, Actor(),
            Command("CANCELLED", 1, "lead-6106-cancel", "NO_RESPONSE", "Gone quiet."), false, default);

        // A reopen must say why in words, not only carry the constant code — see
        // LifecycleApplicationServiceTests.ReopenWithoutWordsIsRefusedAndWritesNothing.
        await service.TransitionLeadAsync(Tenant, 6_106, Actor(),
            Command("UNDER_REVIEW", 2, "lead-6106-reopen", "NEW_INFORMATION",
                "Customer came back after the tender was re-issued."), true, default);

        context.ChangeTracker.Clear();
        var lead = await context.Leads.SingleAsync(x => x.Id == 6_106);
        Assert.Null(lead.OutcomeOn);
        Assert.Null(lead.OutcomeReasonId);
        Assert.Null(lead.OutcomeNote);
        Assert.Equal(2, await context.CommercialLifecycleEvents.CountAsync());
    }

    [Fact]
    public async Task ALeadStageLossIsCountedByTheCustomerWinLossSurface()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed.Customer(context, 6_107_000, Tenant, "Northwind Marine");
        var lead = SeedLead(context, leadId: 6_107);
        lead.ResolveCommercialIdentity(6_107_000, null, "VERIFIED");
        var reason = OutcomeReason(context, ReasonId(6_107), Tenant, "LOST_COMPETITOR", "Lost to competitor");
        await context.SaveChangesAsync();

        await Service(context).TransitionLeadAsync(Tenant, 6_107, Actor(),
            Command("DISQUALIFIED", 1, "lead-6107-lost", "LOST_COMPETITOR", "Incumbent held it."),
            false, default);
        context.ChangeTracker.Clear();

        var action = await CustomerContext(context).GetContext(6_107_000, default);
        var payload = Assert.IsType<CustomerContextDTO>(Assert.IsType<OkObjectResult>(action.Result).Value);

        Assert.Equal(1, payload.LeadStageLosses);
        // No quote ever existed, so the quote-only rate stays blank while the honest one is 0%.
        Assert.Null(payload.WinRatePct);
        Assert.Equal(0m, payload.InquiryWinRatePct);
        var loss = Assert.Single(payload.RecentLeadLosses);
        Assert.Equal(6_107, loss.LeadId);
        Assert.Equal("Lost to competitor", loss.OutcomeReasonName);
        Assert.Equal("Incumbent held it.", loss.OutcomeNote);
        Assert.NotNull(loss.LostOn);
        Assert.Equal(reason.SetupId, await context.Leads.Where(x => x.Id == 6_107)
            .Select(x => x.OutcomeReasonId).SingleAsync());
    }

    [Fact]
    public void TheGovernedPicklistIsSharedWithoutClosingADependencyLoop()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(Tenant);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddScoped<ERP_RFQ_Automation.Services.IQuoteService>(_ => null!);
        // Exactly the Program.cs shape: lifecycle -> lead reasons, and quote outcome -> lifecycle.
        services.AddScoped<ILifecycleApplicationService, LifecycleApplicationService>();
        services.AddScoped<ILeadOutcomeReasons, LeadOutcomeReasons>();
        services.AddScoped<IQuoteOutcomeService, QuoteOutcomeService>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILifecycleApplicationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IQuoteOutcomeService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILeadOutcomeReasons>());
    }

    // ---------------- helpers ----------------

    private static LifecycleApplicationService Service(ErpRfqAutomationContext context) => new(context);

    private static LifecycleActor Actor() => new("rep@nexora.test", "AuthenticatedUser");

    private static LifecycleTransitionCommand Command(
        string target, int version, string key, string? reasonCode, string? notes = null)
        => new(target, version, reasonCode, notes, "Api", $"corr-{key}", $"req-{key}", key);

    /// <summary>A lead sitting in UNDER_REVIEW — the state a pre-quotation loss is decided from.</summary>
    private static Lead SeedLead(ErpRfqAutomationContext context, long leadId)
    {
        Status(context, UnderReviewId(leadId), Tenant, "LeadStatus", "UNDER_REVIEW", "Under Review");
        Status(context, leadId + 300_000, Tenant, "LeadStatus", "DISQUALIFIED", "Disqualified");
        Status(context, leadId + 400_000, Tenant, "LeadStatus", "CANCELLED", "Cancelled");
        return Seed.Lead(context, leadId, Tenant, leadStatusId: UnderReviewId(leadId));
    }

    private static long UnderReviewId(long leadId) => leadId + 200_000;

    private static long ReasonId(long leadId) => leadId + 500_000;

    private static SetupMaster OutcomeReason(
        ErpRfqAutomationContext context, long id, long businessUnitId, string code, string label)
        => Status(context, id, businessUnitId, "QuoteOutcomeReason", code, label);

    private static SetupMaster Status(
        ErpRfqAutomationContext context, long id, long businessUnitId, string type, string code, string label)
    {
        Seed.EnsureBusinessUnit(context, businessUnitId);
        var row = new SetupMaster
        {
            SetupId = id,
            BusinessUnitId = businessUnitId,
            SetupType = type,
            SetupCode = code,
            SetupValue = label,
            Description = label,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(row);
        return row;
    }

    private static CustomerContextController CustomerContext(ErpRfqAutomationContext context) => new(context)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("businessUnitId", Tenant.ToString())], "lead-outcome-test"))
            }
        }
    };

    private static IServiceProvider Provider(IQuoteOutcomeService outcomes)
        => new ServiceCollection().AddSingleton(outcomes).BuildServiceProvider();
}
