using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Decision Register R7 — "the user sets the validity/expiry on a quote. If they change it
/// afterwards they give a reason, and that reason is logged and visible."
///
/// Only half of R7 had shipped: validity could be set while the quote was a draft, and after
/// that nothing could move it. FIN-05 blocks every edit once a quote leaves DRAFT, there was
/// no extend operation, and the SLA sweep's 90-day no-response rule would eventually expire
/// the quote regardless. The rep's only remaining move was to revise — which increments the
/// revision number and reads to the buyer as a NEW commercial offer, when all they asked was
/// "can you hold your price for another two weeks". In Gulf tender business that request is
/// the single most common thing a buyer says.
///
/// These tests pin the missing half at the service layer: the operation exists, it demands a
/// reason, it records that reason as evidence rather than as a log line, and it leaves the
/// commercial offer — above all the revision number — completely alone.
/// </summary>
public sealed class QuoteValidityExtensionTests
{
    private const long Tenant = 97_100;
    private const long SentStatusId = 97_101;
    private const long DraftStatusId = 97_102;
    private const long AcceptedStatusId = 97_103;
    private const long QuoteId = 97_110;

    // -------------------------------------------------- the operation that was missing

    [Fact]
    public async Task A_sent_quote_can_be_extended_and_the_revision_number_does_not_move()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = NewService(context);
        var newDate = DateTime.UtcNow.AddDays(45);

        var result = await service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, newDate, "  Buyer asked us to hold pricing while the technical evaluation runs.  ",
            "rep@nexora.invalid", 7, "extend-1");

        Assert.Equal(1, result.RevisionNo);
        Assert.Equal(newDate.Date, result.ValidUntil!.Value.Date);
        Assert.NotNull(result.ValidityExtendedOn);
        Assert.False(result.Replayed);

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId);
        // The offer itself is untouched: same revision, same status, same money, no successor.
        Assert.Equal(1, quote.RevisionNo);
        Assert.Null(quote.RevisionOfQuoteId);
        Assert.Equal(SentStatusId, quote.StatusId);
        Assert.Equal(401m, quote.TotalAmount);
        Assert.Empty(await context.Quotes.AsNoTracking().Where(q => q.RevisionOfQuoteId == QuoteId).ToListAsync());
        // Only the expiry moved.
        Assert.Equal(newDate.Date, quote.ValidUntil!.Value.Date);
        Assert.NotNull(quote.ValidityExtendedOn);
    }

    /// <summary>
    /// R7's engineering note is explicit: "the change must be an auditable event, not a silent
    /// field update. A reason nobody can read later is not a reason." So the reason is a row —
    /// with the previous date, the actor and the timestamp — and it is readable back.
    /// </summary>
    [Fact]
    public async Task The_reason_is_persisted_as_readable_evidence_not_a_log_line()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var originalValidity = Seed(context);
        var service = NewService(context);
        var newDate = DateTime.UtcNow.AddDays(60);

        await service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, newDate, "SEC extended the bid validity to 15 Oct at the pre-award meeting.",
            "rep@nexora.invalid", 7, "extend-1");

        context.ChangeTracker.Clear();
        var row = await context.QuoteValidityExtensions.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(x => x.QuoteId == QuoteId);
        Assert.Equal(Tenant, row.BusinessUnitId);
        Assert.Equal("SEC extended the bid validity to 15 Oct at the pre-award meeting.", row.Reason);
        Assert.Equal("rep@nexora.invalid", row.ExtendedBy);
        Assert.Equal(7, row.ExtendedByUserId);
        Assert.Equal(originalValidity.Date, row.PreviousValidUntil!.Value.Date);
        Assert.Equal(newDate.Date, row.NewValidUntil.Date);
        Assert.NotEqual(default, row.ExtendedOn);

        // And it is readable back through the API the UI uses, newest first.
        var history = await service.GetValidityExtensionsAsync(QuoteId, Tenant);
        var only = Assert.Single(history);
        Assert.Equal(row.Reason, only.Reason);
        Assert.Equal(row.ExtendedBy, only.ExtendedBy);
    }

    // ------------------------------------------------------------------ the reason gate

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public async Task An_extension_without_a_reason_is_refused_and_nothing_changes(string reason)
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var originalValidity = Seed(context);
        var service = NewService(context);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, DateTime.UtcNow.AddDays(45), reason, "rep@nexora.invalid", 7, "extend-1"));
        Assert.Contains("Enter why the validity is being extended", error.Message);

        context.ChangeTracker.Clear();
        Assert.Empty(context.QuoteValidityExtensions.IgnoreQueryFilters());
        var quote = await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId);
        Assert.Equal(originalValidity.Date, quote.ValidUntil!.Value.Date);
        Assert.Null(quote.ValidityExtendedOn);
    }

    [Fact]
    public async Task An_over_long_reason_is_refused_rather_than_silently_truncated()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = NewService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, DateTime.UtcNow.AddDays(45), new string('x', 501),
            "rep@nexora.invalid", 7, "extend-1"));
        Assert.Empty(context.QuoteValidityExtensions.IgnoreQueryFilters());
    }

    // ------------------------------------------------------------------- the date gate

    [Fact]
    public async Task A_date_in_the_past_is_refused_because_it_is_not_an_extension()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = NewService(context);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, DateTime.UtcNow.AddDays(-1), "Backdating", "rep@nexora.invalid", 7, "extend-1"));
        Assert.Contains("must be in the future", error.Message);
    }

    [Fact]
    public async Task A_date_no_later_than_the_current_validity_is_refused_under_a_control_called_extend()
    {
        // The button says "extend". Quietly accepting an earlier date would SHORTEN the offer
        // the rep pressed it to lengthen, and the customer is the one who finds out.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var originalValidity = Seed(context);
        var service = NewService(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, originalValidity.AddDays(-1), "Pull it in", "rep@nexora.invalid", 7, "extend-1"));
        Assert.Contains("Choose a later date", error.Message);
    }

    // ------------------------------------------------------------- the lifecycle gates

    [Fact]
    public async Task A_draft_is_refused_because_its_validity_is_edited_directly()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, statusId: DraftStatusId);
        var service = NewService(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, DateTime.UtcNow.AddDays(45), "Hold it", "rep@nexora.invalid", 7, "extend-1"));
        Assert.Contains("has not been issued to the customer yet", error.Message);
    }

    [Fact]
    public async Task A_quote_that_already_has_an_outcome_is_refused()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var quote = await context.Quotes.SingleAsync(q => q.Id == QuoteId);
        quote.StatusId = AcceptedStatusId;
        quote.OutcomeOn = DateTime.UtcNow.AddDays(-1);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = NewService(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, DateTime.UtcNow.AddDays(45), "Hold it", "rep@nexora.invalid", 7, "extend-1"));
        Assert.Contains("no longer live", error.Message);
        Assert.Empty(context.QuoteValidityExtensions.IgnoreQueryFilters());
    }

    [Fact]
    public async Task A_superseded_quote_points_the_rep_at_the_latest_revision()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        context.Quotes.Add(new Quote
        {
            Id = QuoteId + 1, QuoteNo = "Q-EXTEND-1-R2", BusinessUnitId = Tenant,
            StatusId = DraftStatusId, RevisionOfQuoteId = QuoteId, RevisionNo = 2,
            QuoteDate = DateTime.UtcNow, TotalAmount = 401m,
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = NewService(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, DateTime.UtcNow.AddDays(45), "Hold it", "rep@nexora.invalid", 7, "extend-1"));
        Assert.Contains("Q-EXTEND-1-R2", error.Message);
    }

    // ------------------------------------------------------- replay + tenant isolation

    [Fact]
    public async Task The_same_command_replayed_records_one_extension_not_two()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = NewService(context);
        var newDate = DateTime.UtcNow.AddDays(45);

        var first = await service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, newDate, "Buyer requested a hold.", "rep@nexora.invalid", 7, "extend-1");
        context.ChangeTracker.Clear();
        var replay = await service.ExtendQuoteValidityAsync(
            QuoteId, Tenant, newDate, "Buyer requested a hold.", "rep@nexora.invalid", 7, "extend-1");

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Single(context.QuoteValidityExtensions.IgnoreQueryFilters());
    }

    [Fact]
    public async Task An_extension_never_crosses_a_business_unit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed(context);
        var service = NewService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ExtendQuoteValidityAsync(
            QuoteId, Tenant + 1, DateTime.UtcNow.AddDays(45), "Hold it",
            "intruder@nexora.invalid", 7, "extend-1"));
        Assert.Empty(context.QuoteValidityExtensions.IgnoreQueryFilters());
    }

    // ---------------------------------------------------------------- test harness

    private static QuoteService NewService(ErpRfqAutomationContext context) =>
        new(context, new SilentEmailService(), null!);

    /// <summary>Seeds one SENT quote and returns the validity date it was issued with.</summary>
    private static DateTime Seed(ErpRfqAutomationContext context, long statusId = SentStatusId)
    {
        Support.Seed.EnsureBusinessUnit(context, Tenant);
        context.SaveChanges();

        foreach (var (id, code, value) in new[]
                 {
                     (SentStatusId, "SENT", "Sent"),
                     (DraftStatusId, "DRAFT", "Draft"),
                     (AcceptedStatusId, "ACCEPTED", "Accepted")
                 })
        {
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = id, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
                SetupCode = code, SetupValue = value, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        }

        var validUntil = DateTime.UtcNow.AddDays(15);
        context.Quotes.Add(new Quote
        {
            Id = QuoteId,
            QuoteNo = "Q-EXTEND-1",
            BusinessUnitId = Tenant,
            StatusId = statusId,
            QuoteDate = DateTime.UtcNow.AddDays(-10),
            SentOn = DateTime.UtcNow.AddDays(-10),
            ValidUntil = validUntil,
            TotalAmount = 401m,
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow.AddDays(-10)
        });
        context.SaveChanges();
        context.ChangeTracker.Clear();
        return validUntil;
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
