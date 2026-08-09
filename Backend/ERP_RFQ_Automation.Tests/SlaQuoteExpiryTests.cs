using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-QTM-07: "Track validity and automatically mark a quotation Expired after its
/// validity date, or after 90 days from submission when no customer response is
/// recorded."
///
/// Both triggers were wrong or missing. The sweep expired a SENT quote at
/// <c>coalesce(ValidUntil, SentOn) + QuoteAutoExpireDays</c> with the knob defaulting to
/// 14, so a quote outlived the validity date it had published to the customer by a
/// fortnight; and the 90-day-no-response rule did not exist at all — StaleQuoteDays only
/// ever sent a nudge email. Neither boundary was reachable through the single 1..365
/// setting the SLA controller exposed.
///
/// These tests pin the two triggers independently, the precondition that separates them
/// (a recorded customer response suppresses the 90-day rule but NOT the validity rule),
/// and the claim-based idempotency that keeps expiry once-only under scale-out.
/// </summary>
public sealed class SlaQuoteExpiryTests
{
    private const long Bu = 96_400;
    private const long SentStatusId = 96_402;
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------ trigger 1: the validity date

    [Fact]
    public async Task A_quote_expires_as_soon_as_its_validity_date_has_passed()
    {
        using var db = new TestDb();
        await SeedAsync(db,
            Quote(1, validUntil: Now.AddMinutes(-1), sentOn: Now.AddDays(-10)),
            Quote(2, validUntil: Now.AddMinutes(1), sentOn: Now.AddDays(-10)));

        // Zero grace by default: one minute past validity is past validity.
        Assert.Equal(new long[] { 1 }, await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    [Fact]
    public void The_default_policy_carries_zero_grace_and_a_ninety_day_no_response_window()
    {
        var policy = SlaPolicy.Default(Bu);

        Assert.Equal(0, policy.QuoteExpiryGraceDays);
        Assert.Equal(90, policy.QuoteNoResponseExpiryDays);
    }

    [Fact]
    public async Task A_tenant_that_buys_a_grace_window_gets_exactly_that_window()
    {
        using var db = new TestDb();
        await SeedAsync(db,
            Quote(1, validUntil: Now.AddDays(-2), sentOn: Now.AddDays(-10)),
            Quote(2, validUntil: Now.AddDays(-4), sentOn: Now.AddDays(-10)));

        var policy = SlaPolicy.Default(Bu);
        policy.QuoteExpiryGraceDays = 3;

        // Grace is additive to the validity date, not a replacement for it.
        Assert.Equal(new long[] { 2 }, await CandidateIdsAsync(db, policy));
    }

    [Fact]
    public async Task A_sentinel_validity_date_does_not_expire_a_freshly_sent_quote()
    {
        // Unparsed dates arrive as year-0001/1900 sentinels from the extraction pipeline
        // (the same convention OpenLeadDeadlineCandidates guards against). With zero grace
        // such a quote would otherwise be expired the instant it was sent.
        using var db = new TestDb();
        await SeedAsync(db, Quote(1, validUntil: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), sentOn: Now.AddDays(-1)));

        Assert.Empty(await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    // ------------------------------- trigger 2: 90 days from submission, no response

    [Fact]
    public async Task A_quote_with_no_customer_response_expires_ninety_days_after_submission()
    {
        using var db = new TestDb();
        await SeedAsync(db,
            Quote(1, validUntil: null, sentOn: Now.AddDays(-91)),
            Quote(2, validUntil: null, sentOn: Now.AddDays(-89)));

        // No ValidUntil at all: trigger 1 can never see these, which is exactly why the
        // requirement carries a second clause.
        Assert.Equal(new long[] { 1 }, await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    [Fact]
    public async Task A_quote_still_inside_its_validity_expires_at_ninety_days_of_silence()
    {
        // Validity deliberately far in the future: the ONLY thing that can expire this
        // quote is the submission-plus-90-days rule.
        using var db = new TestDb();
        await SeedAsync(db, Quote(1, validUntil: Now.AddYears(1), sentOn: Now.AddDays(-91)));

        Assert.Equal(new long[] { 1 }, await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    [Fact]
    public async Task A_quote_the_customer_responded_to_is_never_expired_by_the_ninety_day_rule()
    {
        using var db = new TestDb();
        await SeedAsync(db,
            Quote(1, validUntil: null, sentOn: Now.AddDays(-200), respondedOn: Now.AddDays(-150)),
            Quote(2, validUntil: Now.AddYears(1), sentOn: Now.AddDays(-200), respondedOn: Now.AddDays(-150)));

        // A live negotiation is not "no customer response is recorded". Both quotes are
        // months past the 90-day mark and neither is a candidate.
        Assert.Empty(await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    [Fact]
    public async Task A_recorded_response_does_not_rescue_a_quote_whose_validity_has_lapsed()
    {
        // The response precondition belongs to trigger 2 only. An expired validity date is
        // expired whatever the customer said — otherwise a single "thanks, reviewing it"
        // would keep a stale price live indefinitely.
        using var db = new TestDb();
        await SeedAsync(db, Quote(1, validUntil: Now.AddDays(-1), sentOn: Now.AddDays(-30), respondedOn: Now.AddDays(-20)));

        Assert.Equal(new long[] { 1 }, await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    [Fact]
    public async Task The_no_response_window_is_configurable()
    {
        using var db = new TestDb();
        await SeedAsync(db, Quote(1, validUntil: null, sentOn: Now.AddDays(-45)));

        var policy = SlaPolicy.Default(Bu);
        Assert.Empty(await CandidateIdsAsync(db, policy));           // 45 < 90

        policy.QuoteNoResponseExpiryDays = 30;
        Assert.Equal(new long[] { 1 }, await CandidateIdsAsync(db, policy));
    }

    [Fact]
    public async Task Only_quotes_still_in_SENT_are_candidates()
    {
        using var db = new TestDb();
        await SeedAsync(db, Quote(1, validUntil: Now.AddDays(-30), sentOn: Now.AddDays(-120), statusId: null));

        Assert.Empty(await CandidateIdsAsync(db, SlaPolicy.Default(Bu)));
    }

    // ------------------------------------------------------------ end-to-end sweep

    [Fact]
    public async Task The_sweep_expires_a_lapsed_quote_through_the_single_expiry_path()
    {
        var outcomes = new RecordingOutcomes();
        using var host = NewHost(outcomes);
        await SeedAsync(host.ContextFor(null), Quote(1, validUntil: DateTime.UtcNow.AddDays(-1), sentOn: DateTime.UtcNow.AddDays(-10)));

        await NewWorker(host).SweepOnceAsync(default);

        Assert.Equal(new[] { (1L, "AUTO_EXPIRED") }, outcomes.Calls.ToArray());
    }

    [Fact]
    public async Task The_sweep_expires_a_silent_quote_ninety_days_after_submission()
    {
        var outcomes = new RecordingOutcomes();
        using var host = NewHost(outcomes);
        await SeedAsync(host.ContextFor(null), Quote(1, validUntil: null, sentOn: DateTime.UtcNow.AddDays(-91)));

        await NewWorker(host).SweepOnceAsync(default);

        Assert.Equal(new[] { (1L, "AUTO_EXPIRED") }, outcomes.Calls.ToArray());
    }

    [Fact]
    public async Task The_sweep_leaves_an_answered_quote_alone()
    {
        var outcomes = new RecordingOutcomes();
        using var host = NewHost(outcomes);
        await SeedAsync(host.ContextFor(null), Quote(1,
            validUntil: DateTime.UtcNow.AddYears(1),
            sentOn: DateTime.UtcNow.AddDays(-200),
            respondedOn: DateTime.UtcNow.AddDays(-150)));

        await NewWorker(host).SweepOnceAsync(default);

        Assert.Empty(outcomes.Calls);
    }

    [Fact]
    public async Task A_quote_that_trips_both_triggers_is_expired_exactly_once()
    {
        // Past its validity date AND 90+ days of silence. One claim, one ExpireAsync —
        // and repeated sweeps (and a second instance) must not stamp it again. The fake
        // deliberately leaves the quote in SENT so every later sweep re-selects it as a
        // candidate: the (BusinessUnitId, DedupKey) claim is what has to stop it.
        var outcomes = new RecordingOutcomes();
        using var host = NewHost(outcomes);
        await SeedAsync(host.ContextFor(null), Quote(1,
            validUntil: DateTime.UtcNow.AddDays(-30), sentOn: DateTime.UtcNow.AddDays(-120)));

        var instanceA = NewWorker(host);
        var instanceB = NewWorker(host);
        await instanceA.SweepOnceAsync(default);
        await instanceA.SweepOnceAsync(default);
        await instanceB.SweepOnceAsync(default);

        Assert.Equal(new[] { (1L, "AUTO_EXPIRED") }, outcomes.Calls.ToArray());

        using var verify = host.ContextFor(null);
        var claims = await verify.Set<SlaEvent>().IgnoreQueryFilters()
            .Where(e => e.EntityType == "quote").ToListAsync();
        Assert.Single(claims);
        Assert.Equal("expired", claims[0].Level);
    }

    // ---------------------------------------------------------------- test harness

    private static IQueryable<Quote> Candidates(ErpRfqAutomationContext ctx, SlaPolicy policy)
        => SlaSweepWorker.ExpiryCandidates(ctx, Bu, new List<long> { SentStatusId }, policy, Now);

    private static async Task<long[]> CandidateIdsAsync(TestDb db, SlaPolicy policy)
    {
        await using var ctx = db.ContextFor(Bu);
        return await Candidates(ctx, policy).Select(q => q.Id).OrderBy(id => id).ToArrayAsync();
    }

    private static Quote Quote(
        long id, DateTime? validUntil, DateTime? sentOn,
        DateTime? respondedOn = null, long? statusId = SentStatusId)
        => new()
        {
            Id = id,
            QuoteNo = $"Q-EXPIRY-{id}",
            BusinessUnitId = Bu,
            StatusId = statusId,
            QuoteDate = sentOn,
            ValidUntil = validUntil,
            SentOn = sentOn,
            RespondedOn = respondedOn,
            TotalAmount = 100,
            CreatedBy = "tests",
            CreatedDate = sentOn
        };

    private static Task SeedAsync(TestDb db, params Quote[] quotes) => SeedAsync(db.ContextFor(null), quotes);

    private static async Task SeedAsync(ErpRfqAutomationContext seed, params Quote[] quotes)
    {
        await using (seed)
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();

            seed.SetupMasters.Add(new SetupMaster
            {
                SetupId = SentStatusId, BusinessUnitId = Bu, SetupType = "QuoteStatus",
                SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            seed.Quotes.AddRange(quotes);
            await seed.SaveChangesAsync();
        }
    }

    private static TenantScopedHost NewHost(RecordingOutcomes outcomes) => new(services =>
    {
        services.AddSingleton<ISlaNotifications>(new SilentNotifications());
        services.AddSingleton<IQuoteOutcomeService>(outcomes);
    });

    private static SlaSweepWorker NewWorker(TenantScopedHost host)
        => new(host.ScopeFactory, host.TenantScope, NullLogger<SlaSweepWorker>.Instance);

    /// <summary>
    /// Stands in for <see cref="QuoteOutcomeService.ExpireAsync"/> — the ONE expiry path,
    /// which the sweep must call rather than transitioning the quote itself. Reports success
    /// without mutating the quote, so a second sweep still sees a live candidate.
    /// </summary>
    private sealed class RecordingOutcomes : IQuoteOutcomeService
    {
        private readonly object _gate = new();
        public List<(long QuoteId, string ReasonCode)> Calls { get; } = new();

        public Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default)
        {
            lock (_gate) Calls.Add((quoteId, reasonCode));
            return Task.FromResult(true);
        }

        public Task<QuoteResponseDTO> SetOutcomeAsync(
            long quoteId, long businessUnitId, string actorEmail,
            string outcome, string? reasonCode = null, string? note = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutcomeReasonDto>>(Array.Empty<OutcomeReasonDto>());
    }

    private sealed class SilentNotifications : ISlaNotifications
    {
        public Task<bool> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
