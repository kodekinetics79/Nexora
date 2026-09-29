using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner request 2026-09-28, "add more client-customised statuses", all three parts: the client's
/// own steps while a quote is with the customer, their own endings each counting as Won / Lost /
/// Expired, and their own reasons with a "for". They are labels on the fixed lifecycle, never new
/// states — every rule, reminder and dashboard keeps reading the fixed status.
/// </summary>
public sealed class QuoteClientStatusTests
{
    private const long Bu = 97_401;
    private const long DraftId = 97_402;
    private const long SentId = 97_403;
    private const long AcceptedId = 97_404;
    private const long RejectedId = 97_405;
    private const long ExpiredId = 97_406;
    private const long OrderedId = 97_407;
    private const long OwnerId = 97_408;
    private const long SentQuoteId = 97_410;

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public async Task A_tenant_with_none_gets_the_gulf_defaults_in_order_once()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var statuses = Statuses(ctx);

        var catalog = await statuses.GetCatalogAsync(Bu, includeInactive: false);

        Assert.Equal(new[] { "Technical evaluation", "Clarification asked", "Commercial evaluation", "Negotiation / BAFO" },
            catalog.Steps.Select(s => s.Name));
        Assert.Equal(
            new[]
            {
                ("Partly won", "WON"), ("Verbal award, awaiting PO", "WON"),
                ("Tender cancelled", "LOST"), ("Awarded to another bidder", "LOST")
            },
            catalog.Endings.Select(e => (e.Name, e.CountsAs)));
        Assert.Equal(new[] { "Only compliant bidder", "Best price", "Stock available" },
            catalog.Reasons.Where(r => r.For == "WON").Select(r => r.Name));
        // The reasons that existed before keep "for" empty (= Lost and Expired), and the sweep's own is locked.
        Assert.Contains(catalog.Reasons, r => r.Code == "PRICE" && r.For is null && !r.IsSystem);
        Assert.True(Assert.Single(catalog.Reasons, r => r.Code == "AUTO_EXPIRED").IsSystem);

        var again = await statuses.GetCatalogAsync(Bu, includeInactive: true);
        Assert.Equal(catalog.Steps.Count, again.Steps.Count);
        Assert.Equal(catalog.Endings.Count, again.Endings.Count);
        Assert.Equal(catalog.Reasons.Count, again.Reasons.Count);
        Assert.Equal(4, await ctx.SetupMasters.CountAsync(s => s.SetupType == QuoteClientStatusTypes.Step));

        // A won reason is never offered where only Lost / Expired reasons belong (the outcome and
        // lead-rejection picklists).
        var legacy = await new QuoteOutcomeService(ctx, null!, new NoopLogger<QuoteOutcomeService>()).GetOutcomeReasonsAsync(Bu);
        Assert.Equal(8, legacy.Count);
        Assert.DoesNotContain(legacy, r => r.Code == "BEST_PRICE");
    }

    [Fact]
    public async Task A_lead_loss_offers_and_accepts_every_reason_it_did_before_and_never_a_won_reason()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var catalog = await Statuses(ctx).GetCatalogAsync(Bu, includeInactive: false);
        var wonReason = catalog.Reasons.First(r => r.For == "WON");
        // A reason the client points at Lost stays available to a lead loss, like the originals.
        await Statuses(ctx).AddAsync(Bu, "admin", new QuoteStatusOptionCreateRequest { Kind = "reason", Name = "Late submission", For = "LOST" });

        foreach (var leadReasons in new[] { new LeadOutcomeReasons(ctx), new LeadOutcomeReasons(ctx, new OutcomesProvider(Outcomes(ctx))) })
        {
            var offered = await leadReasons.GetAsync(Bu);
            Assert.Equal(
                new[] { "AUTO_EXPIRED", "CUSTOMER_CANCELLED", "LATE_SUBMISSION", "LEAD_TIME", "LOST_COMPETITOR", "NO_RESPONSE", "NO_STOCK", "OTHER", "PRICE" },
                offered.Select(r => r.Code).OrderBy(c => c, StringComparer.Ordinal));
            Assert.NotNull(await leadReasons.ResolveAsync(Bu, "PRICE"));
            Assert.Null(await leadReasons.ResolveAsync(Bu, wonReason.Code));
        }
    }

    [Fact]
    public async Task A_tenant_that_has_its_own_steps_is_never_given_the_defaults()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        ctx.SetupMasters.Add(new SetupMaster
        {
            SetupType = QuoteClientStatusTypes.Step, SetupCode = "SITE_VISIT", SetupValue = "Site visit",
            ParentSetupId = SentId, BusinessUnitId = Bu, IsActive = false, CreatedBy = "admin", CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var catalog = await Statuses(ctx).GetCatalogAsync(Bu, includeInactive: true);

        var only = Assert.Single(catalog.Steps);
        Assert.Equal("Site visit", only.Name);
        Assert.False(only.IsActive);
        Assert.Empty((await Statuses(ctx).GetCatalogAsync(Bu, includeInactive: false)).Steps);
    }

    [Fact]
    public async Task A_step_is_added_last_renamed_trimmed_reordered_and_switched_off()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var statuses = Statuses(ctx);

        var added = Assert.IsType<QuoteStepOptionDto>(await statuses.AddAsync(Bu, "admin@tenant.test",
            new QuoteStatusOptionCreateRequest { Kind = "step", Name = "  Awaiting samples " }));
        Assert.Equal("Awaiting samples", added.Name);
        Assert.Equal("Awaiting samples", (await statuses.GetCatalogAsync(Bu, false)).Steps.Last().Name);

        // Names are unique per kind, whatever the case, and 1–100 characters.
        await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "step", Name = "TECHNICAL EVALUATION" }));
        await Assert.ThrowsAsync<ArgumentException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "step", Name = "   " }));
        await Assert.ThrowsAsync<ArgumentException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "step", Name = new string('x', 101) }));
        // The same words are fine in another kind.
        await statuses.AddAsync(Bu, "admin", new QuoteStatusOptionCreateRequest { Kind = "reason", Name = "Awaiting samples", For = "LOST" });

        var renamed = Assert.IsType<QuoteStepOptionDto>(await statuses.UpdateAsync(Bu, "admin", added.Id,
            new QuoteStatusOptionUpdateRequest { Name = " Samples asked ", SortOrder = 0 }));
        Assert.Equal("Samples asked", renamed.Name);
        Assert.Equal("Samples asked", (await statuses.GetCatalogAsync(Bu, false)).Steps.First().Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.UpdateAsync(Bu, "admin", added.Id,
            new QuoteStatusOptionUpdateRequest { Name = "clarification ASKED" }));

        await statuses.UpdateAsync(Bu, "admin", added.Id, new QuoteStatusOptionUpdateRequest { IsActive = false });
        Assert.DoesNotContain((await statuses.GetCatalogAsync(Bu, false)).Steps, s => s.Id == added.Id);
        Assert.False(Assert.Single((await statuses.GetCatalogAsync(Bu, true)).Steps, s => s.Id == added.Id).IsActive);
        // A switched-off name still blocks a duplicate: switch it back on instead.
        var clash = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "step", Name = "samples asked" }));
        Assert.Contains("switched off", clash.Message);
    }

    [Fact]
    public async Task What_an_ending_counts_as_is_chosen_once_and_the_system_reason_is_locked()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var statuses = Statuses(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "ending", Name = "Budget withdrawn" }));
        var ending = Assert.IsType<QuoteEndingOptionDto>(await statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "ending", Name = "Budget withdrawn", CountsAs = "LOST" }));
        Assert.Equal("LOST", ending.CountsAs);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.UpdateAsync(Bu, "admin",
            ending.Id, new QuoteStatusOptionUpdateRequest { CountsAs = "WON" }));
        Assert.Contains("never changes", refusal.Message);
        var renamed = Assert.IsType<QuoteEndingOptionDto>(await statuses.UpdateAsync(Bu, "admin", ending.Id,
            new QuoteStatusOptionUpdateRequest { Name = "Budget pulled", CountsAs = "LOST" }));
        Assert.Equal(("Budget pulled", "LOST"), (renamed.Name, renamed.CountsAs));
        Assert.Equal(RejectedId, (await ctx.SetupMasters.AsNoTracking().SingleAsync(s => s.SetupId == ending.Id)).ParentSetupId);

        // A reason's "for" may move, and null means Lost and Expired again.
        var reason = Assert.IsType<QuoteReasonOptionDto>(await statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "reason", Name = "Framework agreement", For = "WON" }));
        Assert.Equal("WON", reason.For);
        var cleared = Assert.IsType<QuoteReasonOptionDto>(await statuses.UpdateAsync(Bu, "admin", reason.Id,
            System.Text.Json.JsonSerializer.Deserialize<QuoteStatusOptionUpdateRequest>("{\"for\":null}",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!));
        Assert.Null(cleared.For);

        var system = (await statuses.GetCatalogAsync(Bu, true)).Reasons.Single(r => r.Code == "AUTO_EXPIRED");
        await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.UpdateAsync(Bu, "admin", system.Id,
            new QuoteStatusOptionUpdateRequest { Name = "Timed out" }));
    }

    // ------------------------------------------------------------------ the step on a quote

    [Fact]
    public async Task Picking_a_step_records_the_customer_responded_and_clearing_it_keeps_that()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, SentQuoteId, SentId);
        var statuses = Statuses(ctx);
        var step = (await statuses.GetCatalogAsync(Bu, false)).Steps.First(s => s.Name == "Technical evaluation");

        await statuses.SetStepAsync(SentQuoteId, Bu, "rep@tenant.test", step.Id);

        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.AsNoTracking().SingleAsync(q => q.Id == SentQuoteId);
        Assert.Equal(step.Id, quote.SubStatusId);
        Assert.NotNull(quote.SubStatusOn);
        Assert.NotNull(quote.RespondedOn);
        Assert.Equal("rep@tenant.test", quote.ModifiedBy);
        Assert.Equal(SentId, quote.StatusId);
        // The same "customer responded" activity the button writes (OD1).
        Assert.Contains(await ctx.Set<CommercialActivity>().AsNoTracking().ToListAsync(),
            a => a.AggregateId == SentQuoteId && a.ActivityType == CommercialActivityType.CustomerResponded);

        var detail = await new QuoteRepository(ctx).GetByIdAsync(SentQuoteId, Bu);
        Assert.Equal(("Technical evaluation", "STEP", step.Id), (detail.SubStatusName, detail.SubStatusKind, detail.SubStatusId));
        Assert.False(detail.IsStale);

        var respondedOn = quote.RespondedOn;
        await statuses.SetStepAsync(SentQuoteId, Bu, "rep@tenant.test", null);

        ctx.ChangeTracker.Clear();
        quote = await ctx.Quotes.AsNoTracking().SingleAsync(q => q.Id == SentQuoteId);
        Assert.Null(quote.SubStatusId);
        Assert.Null(quote.SubStatusOn);
        Assert.Equal(respondedOn, quote.RespondedOn);
    }

    [Fact]
    public async Task A_step_is_refused_on_a_quote_that_is_not_with_the_customer_or_was_replaced()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, SentQuoteId, SentId);
        await AddQuoteAsync(ctx, 97_411, DraftId);
        await AddQuoteAsync(ctx, 97_412, SentId);
        await AddQuoteAsync(ctx, 97_413, DraftId, revisionOf: 97_412);
        var statuses = Statuses(ctx);
        var catalog = await statuses.GetCatalogAsync(Bu, false);
        var step = catalog.Steps.First();

        await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.SetStepAsync(97_411, Bu, "rep", step.Id));
        var replaced = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.SetStepAsync(97_412, Bu, "rep", step.Id));
        Assert.Contains("QT-97413", replaced.Message);
        // Only a step can be a step, and a switched-off one cannot be picked.
        await Assert.ThrowsAsync<ArgumentException>(() => statuses.SetStepAsync(SentQuoteId, Bu, "rep", catalog.Endings.First().Id));
        await statuses.UpdateAsync(Bu, "admin", step.Id, new QuoteStatusOptionUpdateRequest { IsActive = false });
        await Assert.ThrowsAsync<ArgumentException>(() => statuses.SetStepAsync(SentQuoteId, Bu, "rep", step.Id));

        ctx.ChangeTracker.Clear();
        Assert.All(await ctx.Quotes.AsNoTracking().ToListAsync(), q =>
        {
            Assert.Null(q.SubStatusId);
            Assert.Null(q.RespondedOn);
        });
    }

    // ------------------------------------------------------------------ endings on the outcome

    [Fact]
    public async Task An_ending_is_recorded_with_its_outcome_and_replaces_the_step()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, SentQuoteId, SentId);
        var statuses = Statuses(ctx);
        var catalog = await statuses.GetCatalogAsync(Bu, false);
        await statuses.SetStepAsync(SentQuoteId, Bu, "rep", catalog.Steps.First().Id);
        var partlyWon = catalog.Endings.Single(e => e.Name == "Partly won");

        var dto = await Outcomes(ctx).SetOutcomeAsync(SentQuoteId, Bu, "rep@tenant.test", "won", "BEST_PRICE", null, partlyWon.Id);

        Assert.Equal(("Partly won", "ENDING", partlyWon.Id), (dto.SubStatusName, dto.SubStatusKind, dto.SubStatusId));
        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.AsNoTracking().SingleAsync(q => q.Id == SentQuoteId);
        Assert.Equal(AcceptedId, quote.StatusId);
        Assert.Equal(partlyWon.Id, quote.SubStatusId);
        Assert.NotNull(quote.SubStatusOn);
        var listed = Assert.Single((await new QuoteRepository(ctx).GetAllAsync(Bu, 1, 25, state: "outcomes")).Item1);
        Assert.Equal(("Partly won", "ENDING"), (listed.SubStatusName, listed.SubStatusKind));
    }

    [Fact]
    public async Task An_ending_that_counts_as_something_else_is_refused_and_nothing_moves()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, SentQuoteId, SentId);
        var statuses = Statuses(ctx);
        var catalog = await statuses.GetCatalogAsync(Bu, false);
        var step = catalog.Steps.First();
        await statuses.SetStepAsync(SentQuoteId, Bu, "rep", step.Id);
        var partlyWon = catalog.Endings.Single(e => e.Name == "Partly won");

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            Outcomes(ctx).SetOutcomeAsync(SentQuoteId, Bu, "rep", "lost", "PRICE", null, partlyWon.Id));
        Assert.Contains("Won", refusal.Message);
        // A reason kept for Won does not explain a loss either.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Outcomes(ctx).SetOutcomeAsync(SentQuoteId, Bu, "rep", "lost", "BEST_PRICE", null, null));

        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.AsNoTracking().SingleAsync(q => q.Id == SentQuoteId);
        Assert.Equal(SentId, quote.StatusId);
        Assert.Null(quote.OutcomeOn);
        Assert.Equal(step.Id, quote.SubStatusId);
    }

    [Fact]
    public async Task A_plain_outcome_clears_a_step_left_on_the_quote()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, SentQuoteId, SentId);
        var statuses = Statuses(ctx);
        await statuses.SetStepAsync(SentQuoteId, Bu, "rep", (await statuses.GetCatalogAsync(Bu, false)).Steps.First().Id);

        var dto = await Outcomes(ctx).SetOutcomeAsync(SentQuoteId, Bu, "rep", "lost", "PRICE");

        Assert.Null(dto.SubStatusId);
        Assert.Null(dto.SubStatusKind);
        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.AsNoTracking().SingleAsync(q => q.Id == SentQuoteId);
        Assert.Equal(RejectedId, quote.StatusId);
        Assert.Null(quote.SubStatusId);
        Assert.Null(quote.SubStatusOn);
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task Ordered_quotes_stay_in_the_list_and_the_won_lost_tab_with_the_owner_named()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, SentQuoteId, SentId, ownerUserId: OwnerId);
        await AddQuoteAsync(ctx, 97_420, OrderedId);
        await AddQuoteAsync(ctx, 97_421, AcceptedId);
        await AddQuoteAsync(ctx, 97_422, DraftId);
        var repository = new QuoteRepository(ctx);

        var (all, allCount) = await repository.GetAllAsync(Bu, 1, 25);
        var (outcomes, _) = await repository.GetAllAsync(Bu, 1, 25, state: "outcomes");

        Assert.Equal(4, allCount);
        Assert.Contains(all, q => q.Id == 97_420 && q.StatusCode == "ORDERED");
        Assert.Equal(new long[] { 97_420, 97_421 }, outcomes.Select(q => q.Id).OrderBy(id => id));
        Assert.Equal("Rana Haddad", all.Single(q => q.Id == SentQuoteId).OwnerName);
        Assert.Null(all.Single(q => q.Id == 97_420).OwnerName);
        Assert.Equal("Rana Haddad", (await repository.GetByIdAsync(SentQuoteId, Bu)).OwnerName);
    }

    [Fact]
    public async Task A_step_left_on_a_quote_that_left_sent_is_never_shown()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var step = (await Statuses(ctx).GetCatalogAsync(Bu, false)).Steps.First();
        // Moved on by a path that knows nothing of steps (e.g. a direct lifecycle command).
        await AddQuoteAsync(ctx, 97_430, AcceptedId, subStatusId: step.Id);

        var row = Assert.Single((await new QuoteRepository(ctx).GetAllAsync(Bu, 1, 25)).Item1);

        Assert.Null(row.SubStatusId);
        Assert.Null(row.SubStatusName);
        Assert.Null(row.SubStatusKind);
    }

    [Fact]
    public async Task A_status_already_on_quotes_cannot_be_deleted_from_the_generic_setup_list()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var step = (await Statuses(ctx).GetCatalogAsync(Bu, false)).Steps.First();
        await AddQuoteAsync(ctx, SentQuoteId, SentId);
        await Statuses(ctx).SetStepAsync(SentQuoteId, Bu, "rep", step.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => new SetupMasterRepository(ctx).DeleteAsync(step.Id));

        Assert.Contains("Switch it off", refusal.Message);
        Assert.True(await ctx.SetupMasters.AnyAsync(s => s.SetupId == step.Id));
    }

    // ------------------------------------------------------------------ replaced by a revision (finding 1)

    [Fact]
    public async Task A_draft_revision_replaces_the_quote_in_the_list_the_page_and_the_commands_alike()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, 97_412, SentId);
        await AddQuoteAsync(ctx, 97_413, DraftId, revisionOf: 97_412);
        await AddQuoteAsync(ctx, 97_414, SentId);
        await AddQuoteAsync(ctx, 97_415, SentId, revisionOf: 97_414);
        var statuses = Statuses(ctx);
        var step = (await statuses.GetCatalogAsync(Bu, false)).Steps.First();

        // The list says "replaced" exactly when the page and the server do: sent or not.
        var (rows, _) = await new QuoteRepository(ctx).GetAllAsync(Bu, 1, 25);
        Assert.Equal("QT-97413", rows.Single(q => q.Id == 97_412).SupersededByQuoteNo);
        Assert.Equal("QT-97415", rows.Single(q => q.Id == 97_414).SupersededByQuoteNo);
        var info = await new QuoteService(ctx, null!, null!).GetRevisionInfoAsync(97_412, Bu);
        Assert.Equal(("QT-97413", false), (info.SupersededByQuoteNo, info.CanRevise));

        // An unsent revision: the refusal says what to do, not "set it on the draft".
        var stepRefusal = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.SetStepAsync(97_412, Bu, "rep", step.Id));
        Assert.Equal("Revision 'QT-97413' is waiting to be sent. Send it, then update its status.", stepRefusal.Message);
        var outcomeRefusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Outcomes(ctx).SetOutcomeAsync(97_412, Bu, "rep", "lost", "PRICE"));
        Assert.Equal("Revision 'QT-97413' is waiting to be sent. Send it, then update its status.", outcomeRefusal.Message);

        // A sent revision keeps today's meaning.
        var sentStep = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.SetStepAsync(97_414, Bu, "rep", step.Id));
        Assert.Contains("replaced by revision 'QT-97415'", sentStep.Message);
        var sentOutcome = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Outcomes(ctx).SetOutcomeAsync(97_414, Bu, "rep", "lost", "PRICE"));
        Assert.Contains("Record the outcome on the latest revision instead", sentOutcome.Message);
    }

    [Fact]
    public async Task A_withdrawn_revision_replaces_nothing()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        await AddQuoteAsync(ctx, 97_412, SentId);
        await AddQuoteAsync(ctx, 97_413, DraftId, revisionOf: 97_412, removedOn: DateTime.UtcNow.AddDays(-1));
        var statuses = Statuses(ctx);
        var step = (await statuses.GetCatalogAsync(Bu, false)).Steps.First();

        var row = Assert.Single((await new QuoteRepository(ctx).GetAllAsync(Bu, 1, 25)).Item1);
        Assert.Equal(97_412, row.Id);
        Assert.Null(row.SupersededByQuoteNo);
        var info = await new QuoteService(ctx, null!, null!).GetRevisionInfoAsync(97_412, Bu);
        Assert.Null(info.SupersededByQuoteNo);
        Assert.Null(info.SupersededByQuoteId);

        await statuses.SetStepAsync(97_412, Bu, "rep", step.Id);
        await Outcomes(ctx).SetOutcomeAsync(97_412, Bu, "rep", "won", "BEST_PRICE");

        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.AsNoTracking().SingleAsync(q => q.Id == 97_412);
        Assert.Equal(AcceptedId, quote.StatusId);
        Assert.NotNull(quote.OutcomeOn);
    }

    // ------------------------------------------------------------------ one name space (finding 18)

    [Theory]
    [InlineData("step", "won")]
    [InlineData("step", "Customer Replied")]
    [InlineData("ending", "LOST")]
    [InlineData("ending", " expired ")]
    public async Task The_fixed_names_in_the_status_window_are_reserved(string kind, string name)
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var statuses = Statuses(ctx);
        var catalog = await statuses.GetCatalogAsync(Bu, false);

        var added = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = kind, Name = name, CountsAs = kind == "ending" ? "LOST" : null }));
        Assert.Contains("fixed status", added.Message);
        long existingId = kind == "step" ? catalog.Steps.First().Id : catalog.Endings.First().Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.UpdateAsync(Bu, "admin", existingId,
            new QuoteStatusOptionUpdateRequest { Name = name }));
    }

    [Fact]
    public async Task A_step_and_an_ending_cannot_share_a_name()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        await SeedAsync(ctx);
        var statuses = Statuses(ctx);
        var catalog = await statuses.GetCatalogAsync(Bu, false);

        var asEnding = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "ending", Name = "technical EVALUATION", CountsAs = "LOST" }));
        Assert.Contains("already a step", asEnding.Message);
        var asStep = await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.AddAsync(Bu, "admin",
            new QuoteStatusOptionCreateRequest { Kind = "step", Name = "Partly Won" }));
        Assert.Contains("already an ending", asStep.Message);
        // A rename is checked the same way.
        var partlyWon = catalog.Endings.Single(e => e.Name == "Partly won");
        await Assert.ThrowsAsync<InvalidOperationException>(() => statuses.UpdateAsync(Bu, "admin", partlyWon.Id,
            new QuoteStatusOptionUpdateRequest { Name = "Clarification asked" }));
        // Renaming a row to its own name (another case) is still fine.
        await statuses.UpdateAsync(Bu, "admin", partlyWon.Id, new QuoteStatusOptionUpdateRequest { Name = "Partly WON" });

        Assert.Equal(4, await ctx.SetupMasters.CountAsync(s => s.SetupType == QuoteClientStatusTypes.Ending));
        Assert.Equal(4, await ctx.SetupMasters.CountAsync(s => s.SetupType == QuoteClientStatusTypes.Step));
    }

    // ------------------------------------------------------------------ who may do what

    [Fact]
    public void Reading_needs_view_setting_a_step_needs_edit_and_the_list_is_limited_like_setup_picklists()
    {
        static T? Attr<T>(string action) where T : Attribute
            => typeof(QuoteController).GetMethod(action)!.GetCustomAttribute<T>();

        var read = Attr<RequireModulePermissionAttribute>(nameof(QuoteController.GetStatusCatalog));
        Assert.Equal(("Quotations", PermissionAction.View), (read!.ModuleName, read.Action));
        var step = Attr<RequireModulePermissionAttribute>(nameof(QuoteController.SetStep));
        Assert.Equal(("Quotations", PermissionAction.Edit), (step!.ModuleName, step.Action));
        Assert.NotNull(Attr<RequireManagerRoleAttribute>(nameof(QuoteController.AddStatusOption)));
        Assert.NotNull(Attr<RequireManagerRoleAttribute>(nameof(QuoteController.UpdateStatusOption)));
        // The same gate the generic picklist editor carries.
        Assert.NotNull(typeof(SetupMasterController).GetMethod(nameof(SetupMasterController.Update))!
            .GetCustomAttribute<RequireManagerRoleAttribute>());
    }

    // ------------------------------------------------------------------ plumbing

    private static QuoteOutcomeService Outcomes(ErpRfqAutomationContext ctx)
        => new(ctx, new QuoteService(ctx, null!, null!), new NoopLogger<QuoteOutcomeService>(),
            sales: new SalesApplicationService(new EfSalesPersistence(ctx)));

    private sealed class OutcomesProvider(IQuoteOutcomeService outcomes) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IQuoteOutcomeService) ? outcomes : null;
    }

    private static QuoteClientStatusService Statuses(ErpRfqAutomationContext ctx) => new(ctx, Outcomes(ctx));

    private static async Task SeedAsync(ErpRfqAutomationContext ctx)
    {
        var bu = Seed.EnsureBusinessUnit(ctx, Bu);
        bu.DefaultLeadOwnerUserId = OwnerId;
        ctx.Users.Add(new User
        {
            Id = OwnerId, FirstName = "Rana", LastName = "Haddad", Email = "rana@tenant.test",
            PasswordHash = "x", ImageUrl = "n/a", Buid = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        ctx.SetupMasters.AddRange(
            Status(DraftId, "DRAFT", "Draft"), Status(SentId, "SENT", "Sent"), Status(AcceptedId, "ACCEPTED", "Accepted"),
            Status(RejectedId, "REJECTED", "Rejected"), Status(ExpiredId, "EXPIRED", "Expired"),
            Status(OrderedId, "ORDERED", "Ordered"));
        await ctx.SaveChangesAsync();
    }

    private static async Task AddQuoteAsync(ErpRfqAutomationContext ctx, long id, long statusId,
        long? revisionOf = null, long? ownerUserId = null, long? subStatusId = null, DateTime? removedOn = null)
    {
        ctx.Quotes.Add(new Quote
        {
            Id = id, QuoteNo = $"QT-{id}", BusinessUnitId = Bu, StatusId = statusId,
            QuoteDate = DateTime.UtcNow.AddDays(-12), ValidUntil = DateTime.UtcNow.AddDays(20),
            SentOn = statusId == DraftId ? null : DateTime.UtcNow.AddDays(-12),
            RevisionOfQuoteId = revisionOf, RevisionNo = revisionOf is null ? 1 : 2,
            OwnerUserId = ownerUserId, SubStatusId = subStatusId, SubStatusOn = subStatusId is null ? null : DateTime.UtcNow,
            RemovedOn = removedOn, RemovedBy = removedOn is null ? null : "rep", RemovalReason = removedOn is null ? null : "withdrawn",
            TotalAmount = 11_500m, CreatedBy = "seed", CreatedDate = DateTime.UtcNow.AddDays(-12 + (int)(id % 10))
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
    }

    private static SetupMaster Status(long id, string code, string value) => new()
    {
        SetupId = id, BusinessUnitId = Bu, SetupType = "QuoteStatus", SetupCode = code, SetupValue = value,
        IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };
}
