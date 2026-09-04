using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A quote that cannot be sent must say so BEFORE the send dialog, not after.
///
/// <para><b>The defect.</b> Half the send chain runs asynchronously inside
/// <c>QuoteDeliveryDispatcher</c>, and its refusals reach nobody. A draft with no currency, a
/// business unit with no legal name, and a tenant with no transmitting mailbox all pass every
/// synchronous check in <c>SendQuoteEmailAsync</c>, answer the rep "Quote delivery queued", and
/// then die in the outbox. The delivery idempotency key is fixed per quote
/// (<c>quote:{id}:delivery:v1</c>), so a dead-lettered row then makes that quote PERMANENTLY
/// unsendable — every later send returns the dead-letter code and the only way out is a new
/// revision, which nothing tells the rep.</para>
///
/// <para><b>Why now.</b> Measured on production 2026-09-02: <c>quote_delivery_requests</c> has
/// zero rows across every tenant, and the only two customer quotes that exist are DRAFT with
/// NULL currency — one with a total of 0.00. This half of the spine has never run, and the
/// first person to walk it will be a client.</para>
/// </summary>
public class QuoteSendReadinessTests
{
    private const long Tenant = 9801;

    [Fact]
    public async Task A_draft_with_no_currency_is_refused_before_the_dialog_instead_of_dying_in_the_outbox()
    {
        // PRODUCTION'S EXACT SHAPE: DRAFT, CurrencyId NULL, lines priced, output tax derived so
        // the tax gate cannot mask the currency gap.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, currencyId: null);

        // What actually happens today. The renderer runs in a background worker, so this refusal
        // is invisible to the rep — it is the outbox death, reproduced.
        var service = new QuoteService(context, null!, Configured());
        var rendered = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateQuotePdfAsync(quoteId, Tenant));
        Assert.Contains("Commercial Review Required", rendered.Message);

        // What the rep now sees first, on the same rule, naming the specific missing field.
        var readiness = await service.EvaluateSendReadinessAsync(quoteId, Tenant);
        Assert.False(readiness.CanSend);
        var blocker = Assert.Single(readiness.Blockers, x => x.Code == "QUOTE_INCOMPLETE");
        Assert.Contains("no currency", blocker.Message);
        Assert.Contains("Set the currency", blocker.Message);
    }

    [Fact]
    public async Task A_complete_draft_reports_no_blockers()
    {
        // THE CONTROL. Without it a readiness check that always refuses would pass every test
        // above and stop the product working.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, currencyId: 98011);

        var readiness = await new QuoteService(context, null!, Configured(),
            outboundSenders: Sender(transmits: true)).EvaluateSendReadinessAsync(quoteId, Tenant);

        Assert.True(readiness.CanSend);
        Assert.Empty(readiness.Blockers);
    }

    [Fact]
    public async Task A_tenant_that_cannot_transmit_is_told_before_the_send_burns_the_quote_number()
    {
        // The worst of the asynchronous refusals: a non-transmitting sender dead-letters on
        // attempt ONE, and the fixed idempotency key then bars every future send of this quote.
        // The rep's evidence today is a green "Quote delivery queued" toast.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, currencyId: 98011);

        var readiness = await new QuoteService(context, null!, Configured(),
            outboundSenders: Sender(transmits: false)).EvaluateSendReadinessAsync(quoteId, Tenant);

        Assert.False(readiness.CanSend);
        var blocker = Assert.Single(readiness.Blockers, x => x.Code == "OUTBOUND_MAIL_NOT_CONFIGURED");
        Assert.Equal("/setup/mailboxes", blocker.SetupPath);
        Assert.Contains("no active SMTP mailbox", blocker.Message);
    }

    [Fact]
    public async Task An_uncertain_delivery_says_the_customer_may_already_have_it_and_never_offers_a_resend()
    {
        // The restart case, on the customer side. A deploy landing mid-send expires the lease;
        // QuoteDeliveryStore dead-letters it as DeliveryOutcomeUncertain. Nobody knows whether
        // the customer received the quote, so at-most-once means nothing is resent — and the rep
        // must be told that, not shown a raw error code from a background worker.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, currencyId: 98011);
        SeedDeadLetteredDelivery(context, quoteId, "DeliveryOutcomeUncertain:InvalidOperationException");

        var readiness = await new QuoteService(context, null!, Configured(),
            outboundSenders: Sender(transmits: true)).EvaluateSendReadinessAsync(quoteId, Tenant);

        Assert.False(readiness.CanSend);
        Assert.Equal("UNCERTAIN", readiness.DeliveryOutcome);
        var blocker = Assert.Single(readiness.Blockers, x => x.Code == "DELIVERY_OUTCOME_UNCERTAIN");
        Assert.Contains("may or may not have received it", blocker.Message);
        Assert.Contains("new revision", blocker.Message);
        // The internal code is not the message.
        Assert.DoesNotContain("InvalidOperationException", blocker.Message);
    }

    [Fact]
    public async Task A_delivery_that_definitely_failed_is_reported_as_a_different_fact()
    {
        // The control for the test above. Told apart, because "we do not know" and "it did not
        // arrive" call for different actions from the rep.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, currencyId: 98011);
        SeedDeadLetteredDelivery(context, quoteId, "QuoteRenderFailed");

        var readiness = await new QuoteService(context, null!, Configured(),
            outboundSenders: Sender(transmits: true)).EvaluateSendReadinessAsync(quoteId, Tenant);

        Assert.Equal("NOT_DELIVERED", readiness.DeliveryOutcome);
        var blocker = Assert.Single(readiness.Blockers, x => x.Code == "DELIVERY_FAILED");
        Assert.Contains("failed permanently", blocker.Message);
        Assert.DoesNotContain("may or may not", blocker.Message);
    }

    // ------------------------------------------------------------------------ test plumbing

    /// <summary>
    /// PRODUCTION QUOTE 66 (BU 7), read on 2026-09-02: DRAFT, three lines priced at 18500 /
    /// 3250 / 47900, output tax derived at 15% on every line, ValidUntil set — and CurrencyID
    /// NULL. Every gate the rep can see passes; the currency is the only thing missing, and it
    /// is checked nowhere except inside the PDF renderer that runs in a background worker.
    ///
    /// <para>The tax is derived on purpose. A fixture that also omitted the tax would trip the
    /// R17 gate first and prove nothing about the currency.</para>
    /// </summary>
    private const long DraftStatusId = 98010;

    private static long SeedQuote(ErpRfqAutomationContext context, long? currencyId)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "DRAFT", SetupValue = "Draft", CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        if (currencyId.HasValue)
            context.Currencies.Add(new Currency
            {
                Id = currencyId.Value, BusinessUnitId = Tenant, Code = "SAR",
                CurrencyName = "Saudi Riyal", CreatedBy = "seed"
            });
        var quote = new Quote
        {
            Id = 98013,
            QuoteNo = "QT-READY-9801",
            BusinessUnitId = Tenant,
            StatusId = DraftStatusId,
            CurrencyId = currencyId,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 57.50m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = 98014,
            ItemDescription = "Gasket spiral wound",
            Quantity = 5m,
            UnitOfMeasure = "EA",
            UnitPrice = 10m,
            TaxAmount = 7.50m,
            TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
            TaxRatePercentApplied = 15m,
            TotalAmount = 57.50m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        context.SaveChanges();
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quote.Id, Tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-TEST", null, "tests", default).GetAwaiter().GetResult();
        return quote.Id;
    }

    private static void SeedDeadLetteredDelivery(
        ErpRfqAutomationContext context, long quoteId, string lastErrorCode)
    {
        context.QuoteDeliveryRequests.Add(new QuoteDeliveryRequest
        {
            BusinessUnitId = Tenant,
            QuoteId = quoteId,
            IdempotencyKey = $"quote:{quoteId}:delivery:v1",
            RecipientEmail = "buyer@customer.test",
            Subject = "Quote",
            Body = "Quote",
            AttachmentFileName = "Quote.pdf",
            RequestedOn = DateTime.UtcNow.AddMinutes(-30),
            AvailableOn = DateTime.UtcNow.AddMinutes(-30),
            AttemptCount = 1,
            DeadLetteredOn = DateTime.UtcNow.AddMinutes(-20),
            LastErrorCode = lastErrorCode,
            Version = 2
        });
        context.SaveChanges();
    }

    private static IOutboundSenderResolver Sender(bool transmits) => new StubSenderResolver(transmits);

    private sealed class StubSenderResolver(bool transmits) : IOutboundSenderResolver
    {
        private static readonly OutboundEmailSettingsSnapshot Platform =
            OutboundEmailSettingsSnapshot.FromOptions(new NotificationsOptions { Provider = "console" });

        public Task<ResolvedOutboundSender> ResolveAsync(long? businessUnitId, CancellationToken ct = default)
            => Task.FromResult(new ResolvedOutboundSender(OutboundSenderOrigin.Platform,
                new GuardedEmailSender(new ConsoleEmailSender(NullLogger<ConsoleEmailSender>.Instance),
                    Options.Create(new NotificationsOptions()), NullLogger<GuardedEmailSender>.Instance),
                transmits ? "smtp" : "console", transmits, OutboundEmailMode.Live,
                "x@y.test", "X", null, null, null, Platform));

        public ResolvedOutboundSender ForMailbox(TenantOutboundSender mailbox, OutboundEmailSettingsSnapshot platformSettings)
            => throw new NotSupportedException();
    }

    private static IQuoteConfigurationRepository Configured() => new StubConfig(new QuoteConfiguration
    {
        BusinessUnitId = Tenant,
        CompanyAddress = "King Fahd Road, Al Khobar 34423",
        CompanyPhone = "+966 13 800 0000",
        CompanyEmail = "sales@noorandsons.example"
    });

    private sealed class StubConfig(QuoteConfiguration? configuration) : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId)
            => Task.FromResult(configuration);

        public Task<QuoteConfiguration> UpsertAsync(QuoteConfiguration configurationToSave)
            => Task.FromResult(configurationToSave);

        public Task AddAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;

        public Task UpdateAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;
    }
}
