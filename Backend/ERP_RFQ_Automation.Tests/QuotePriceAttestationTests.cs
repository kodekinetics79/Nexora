using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Decision Register R5 — price-provenance attestation before send. This is the
/// pre-release control that REPLACES the BRD's FR-QTM-04 LOA approver workflow.
///
/// The defect it closes is a fail-open one: BelowFloorGuard only blocks a line whose floor
/// can be established from prior accepted quotes, so any first-time item had no floor and
/// was emailed with no approver, no date and no recorded margin. These tests pin the gate
/// at the SERVICE layer, which every send path funnels through — the controller, the RFQ
/// auto-send on approval, and the below-floor approval tool.
/// </summary>
public sealed class QuotePriceAttestationTests
{
    private const long Tenant = 96_001;
    private const long QuoteId = 96_011;
    private const long SentStatusId = 96_002;

    /// <summary>
    /// THE TEST THAT MATTERS: the API refuses the send. Nothing is queued, nothing is
    /// emailed, and the quote is not stamped as sent.
    /// </summary>
    [Fact]
    public async Task Send_is_refused_when_the_price_source_was_never_confirmed()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var email = new RecordingEmailService();
        var service = new QuoteService(context, email, null!);

        var result = await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");

        Assert.True(result.BlockedPendingPriceAttestation);
        Assert.False(result.QueuedForDelivery);
        Assert.Contains("price source has not been confirmed", result.PriceAttestationReason);
        Assert.Empty(context.QuoteDeliveryRequests.IgnoreQueryFilters());
        Assert.Equal(0, email.SendCount);
        Assert.Null((await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId)).SentOn);
    }

    /// <summary>
    /// With a confirmation covering the current prices the send proceeds exactly as before,
    /// and the confirmation is on the record: who, when, which source, which reference, and
    /// the price of every line at that moment.
    /// </summary>
    [Fact]
    public async Task Send_proceeds_once_the_price_source_is_confirmed_and_the_confirmation_is_persisted()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471 / Alfa Trading",
            7, "rep@nexora.invalid", default);

        var result = await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");

        Assert.False(result.BlockedPendingPriceAttestation);
        Assert.True(result.QueuedForDelivery);
        Assert.Single(context.QuoteDeliveryRequests.IgnoreQueryFilters());

        var record = await context.QuotePriceAttestations.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(a => a.QuoteId == QuoteId);
        Assert.Equal(Tenant, record.BusinessUnitId);
        Assert.Equal(PriceAttestationSources.SupplierQuote, record.Source);
        Assert.Equal("SQ-4471 / Alfa Trading", record.SourceReference);
        Assert.Equal("rep@nexora.invalid", record.ConfirmedBy);
        Assert.Equal(7, record.ConfirmedByUserId);
        Assert.NotEqual(default, record.ConfirmedOn);
        Assert.Equal(2, record.LineCount);

        // The exact per-line prices at the moment of confirmation, not just a timestamp.
        var lines = await context.QuotePriceAttestationLines.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.AttestationId == record.Id).OrderBy(l => l.QuoteItemId).ToListAsync();
        Assert.Equal(new[] { 120.500000m, 80.000000m }, lines.Select(l => l.UnitPrice).ToArray());
    }

    [Fact]
    public async Task A_priced_quote_with_an_open_customer_revision_impact_cannot_be_sent()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);

        var identity = await new LeadIdentityApplicationService(context).EstablishBaselineRevisionAsync(
            Tenant, 96_301,
            new LeadIdentityBaselineRequest("Test", "Establish immutable test revision.",
                "Service", "tests", "quote-stale-send"));
        context.Set<LeadRevisionImpact>().Add(new LeadRevisionImpact
        {
            BusinessUnitId = Tenant,
            LeadId = 96_301,
            LeadRevisionId = identity.RevisionId!.Value,
            AggregateType = "QUOTE",
            AggregateId = QuoteId,
            ImpactType = "QUOTE_REVISION_REQUIRED",
            Status = "OPEN",
            DetailsJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await new PriceAttestationService(context).AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471 / Alfa Trading",
            7, "rep@nexora.invalid", default);

        var service = new QuoteService(context, new RecordingEmailService(), null!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid"));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.QuoteDeliveryRequests.IgnoreQueryFilters());
    }

    /// <summary>
    /// The one that keeps the gate honest: confirm, then edit a price, then send. Validity
    /// is decided by comparing the RECORDED prices against the current ones, so a stale
    /// confirmation cannot cover a repriced quote no matter how recent it is.
    /// </summary>
    [Fact]
    public async Task A_price_edit_after_confirmation_invalidates_it_and_the_send_is_refused_again()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SalesManager, "Imran Qureshi",
            7, "rep@nexora.invalid", default);
        Assert.True((await attestations.EvaluateAsync(QuoteId, Tenant, default)).Satisfied);

        // The rep drops one line's price after confirming it.
        var line = await context.QuoteItems.SingleAsync(i => i.Id == 96_101);
        line.UnitPrice = 99.000000m;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");

        Assert.True(result.BlockedPendingPriceAttestation);
        Assert.Contains("changed after", result.PriceAttestationReason);
        Assert.Empty(context.QuoteDeliveryRequests.IgnoreQueryFilters());

        // Confirming again over the NEW prices restores the send.
        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SalesManager, "Imran Qureshi",
            7, "rep@nexora.invalid", default);
        Assert.True((await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid")).QueuedForDelivery);
    }

    /// <summary>
    /// Adding a line is adding a price nobody confirmed, so it voids the confirmation too —
    /// the gate covers the whole set of prices, not only the ones that already existed.
    /// </summary>
    [Fact]
    public async Task Adding_a_line_after_confirmation_invalidates_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471",
            7, "rep@nexora.invalid", default);

        context.QuoteItems.Add(new QuoteItem
        {
            Id = 96_103, QuoteId = QuoteId, ItemDescription = "Late addition",
            Quantity = 1m, UnitPrice = 250.000000m, TotalAmount = 250m,
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");
        Assert.True(result.BlockedPendingPriceAttestation);
    }

    /// <summary>
    /// Applies to EVERY send, including revisions. A revision is a new Quote row, so the
    /// predecessor's confirmation does not travel with it — the rep confirms the revised
    /// prices before the customer sees them.
    /// </summary>
    [Fact]
    public async Task A_revision_requires_its_own_confirmation_even_though_the_original_had_one()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471",
            7, "rep@nexora.invalid", default);
        await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");
        context.ChangeTracker.Clear();

        var revision = await service.ReviseQuoteAsync(QuoteId, Tenant, "rep@nexora.invalid");
        context.ChangeTracker.Clear();

        Assert.NotEqual(QuoteId, revision.Id);
        Assert.Empty(await context.QuotePriceAttestations.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.QuoteId == revision.Id).ToListAsync());

        var result = await service.SendQuoteEmailAsync(revision.Id, Tenant, "buyer@nexora.invalid");
        Assert.True(result.BlockedPendingPriceAttestation);
        Assert.Contains("price source has not been confirmed", result.PriceAttestationReason);

        // And the revision becomes sendable on its own confirmation, not the original's.
        await attestations.AttestAsync(
            revision.Id, Tenant, PriceAttestationSources.SalesManager, "Imran Qureshi",
            7, "rep@nexora.invalid", default);
        Assert.True((await service.SendQuoteEmailAsync(revision.Id, Tenant, "buyer@nexora.invalid")).QueuedForDelivery);
    }

    /// <summary>
    /// Only the two ratified provenances are storable, and each one needs the reference that
    /// makes it checkable afterwards — a supplier quote id, or the manager's name.
    /// </summary>
    [Theory]
    [InlineData("MY_OWN_JUDGEMENT", "whatever")]
    [InlineData(PriceAttestationSources.SupplierQuote, "   ")]
    [InlineData(PriceAttestationSources.SalesManager, "")]
    public async Task An_unusable_declaration_is_rejected(string source, string reference)
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => attestations.AttestAsync(
            QuoteId, Tenant, source, reference, 7, "rep@nexora.invalid", default));
        Assert.Empty(context.QuotePriceAttestations.IgnoreQueryFilters());
    }

    /// <summary>
    /// Tenant isolation: a confirmation recorded in one business unit must never satisfy
    /// another's send gate, and must never be attachable to another tenant's quote.
    /// </summary>
    [Fact]
    public async Task A_confirmation_never_crosses_a_business_unit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed(context);
        var attestations = new PriceAttestationService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => attestations.AttestAsync(
            QuoteId, Tenant + 1, PriceAttestationSources.SalesManager, "Imran Qureshi",
            7, "intruder@nexora.invalid", default));

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SalesManager, "Imran Qureshi",
            7, "rep@nexora.invalid", default);
        var foreign = await attestations.EvaluateAsync(QuoteId, Tenant, default);
        Assert.True(foreign.Satisfied);
    }

    // ============================================================================
    // Time-of-check / time-of-use: "send" only ENQUEUES
    // ============================================================================

    /// <summary>
    /// The hole this closes. <c>SendQuoteEmailAsync</c> checks the attestation and then writes a
    /// delivery row; the quote stays DRAFT — and therefore editable — until the worker actually
    /// emails it. Anything that changes a price inside that window would have been delivered
    /// under a confirmation made against different numbers, so the customer could receive a price
    /// nobody attested to. The fingerprint the send was AUTHORISED for is now recorded on the
    /// delivery row and re-verified immediately before the PDF is rendered.
    /// </summary>
    [Fact]
    public async Task The_send_records_the_priced_content_it_was_authorised_for()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471", 7, "rep@nexora.invalid", default);
        await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");

        context.ChangeTracker.Clear();
        var delivery = await context.QuoteDeliveryRequests.AsNoTracking().IgnoreQueryFilters().SingleAsync();
        var attestation = await context.QuotePriceAttestations.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(a => a.QuoteId == QuoteId);
        Assert.Equal(attestation.LineFingerprint, delivery.AttestedPriceFingerprint);
    }

    /// <summary>
    /// A price edited between queueing and dispatching fails the send CLOSED: the renderer
    /// refuses, so no PDF exists and no email is possible. Nothing degrades into a successful
    /// send carrying unattested numbers.
    /// </summary>
    [Fact]
    public async Task A_price_edited_after_the_send_was_authorised_blocks_the_dispatch()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var email = new RecordingEmailService();
        var service = new QuoteService(context, email, new StubQuoteConfigurationRepository());

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471", 7, "rep@nexora.invalid", default);
        await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");
        context.ChangeTracker.Clear();
        var delivery = await context.QuoteDeliveryRequests.AsNoTracking().IgnoreQueryFilters().SingleAsync();

        // Out-of-band price change inside the queue window, then the rep re-confirms — which by
        // itself would satisfy the gate. The BINDING is what refuses.
        var line = await context.QuoteItems.SingleAsync(i => i.Id == 96_101);
        line.UnitPrice = 99.000000m;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471", 7, "rep@nexora.invalid", default);
        Assert.True((await attestations.EvaluateAsync(QuoteId, Tenant, default)).Satisfied);

        var sender = new QuoteDeliverySender(service, new AcceptingEmailSender());
        var envelope = new QuoteDeliveryEnvelope(
            delivery.Id, Tenant, QuoteId, "buyer@nexora.invalid", "Quote", "Body", null,
            "quote.pdf", 1, Guid.NewGuid(), delivery.AttestedPriceFingerprint);

        var failure = await Assert.ThrowsAsync<QuoteDeliveryPreSendException>(
            () => sender.SendAsync(envelope, default));

        Assert.True(failure.Permanent);   // retrying can never make a changed price match
        Assert.IsType<PriceAttestationRequiredException>(failure.InnerException);
        Assert.Contains("changed after this send was authorised", failure.InnerException!.Message);
        Assert.Contains("Nothing was", failure.InnerException.Message);
        Assert.Equal(0, email.SendCount);
        Assert.Null((await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId)).SentOn);
    }

    /// <summary>
    /// The prevention half. The dispatcher failing closed is the backstop; refusing the edit is
    /// what stops a rep silently destroying their own send. The message says what to do next.
    /// </summary>
    [Fact]
    public async Task A_quote_queued_for_delivery_refuses_edits_while_the_email_is_still_in_flight()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var draft = await context.Quotes.SingleAsync(q => q.Id == QuoteId);
        draft.StatusId = SentStatusId + 1; // DRAFT — this is exactly the state "send" leaves it in
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);
        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471", 7, "rep@nexora.invalid", default);
        await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");
        context.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateQuoteAsync(
            QuoteId, new ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteUpdateRequestDTO
            {
                Id = QuoteId, QuoteNo = "Q-ATTEST-1", ModifiedBy = "rep@nexora.invalid",
                QuoteItems =
                {
                    new ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteItemUpdateRequestDTO
                    {
                        Id = 96_101, Quantity = 2m, UnitPrice = 1m, TotalAmount = 2m
                    }
                }
            }));

        Assert.Contains("queued for delivery", error.Message);
        Assert.Equal(120.500000m, (await context.QuoteItems.AsNoTracking().SingleAsync(i => i.Id == 96_101)).UnitPrice);
    }

    // ============================================================================
    // The PDF endpoint: the commercial document itself
    // ============================================================================

    /// <summary>
    /// <c>GET /api/Quote/{id}/pdf</c> had no attestation check at all. The PDF IS the commercial
    /// offer — once rendered it can be downloaded and forwarded — so it must not exist for a
    /// quote whose prices nobody has attested to. Fails closed, with a message the rep can act on.
    /// </summary>
    [Fact]
    public async Task The_pdf_refuses_an_unattested_quote()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = new QuoteService(context, new RecordingEmailService(), new StubQuoteConfigurationRepository());

        var error = await Assert.ThrowsAsync<PriceAttestationRequiredException>(
            () => service.GenerateQuotePdfAsync(QuoteId, Tenant));

        Assert.Contains("Q-ATTEST-1", error.Message);
        Assert.Contains("price source has not been confirmed", error.Message);
        Assert.False(error.BindingBroken);
    }

    [Fact]
    public async Task The_pdf_refuses_a_quote_whose_prices_moved_after_it_was_attested()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var attestations = new PriceAttestationService(context);
        var service = new QuoteService(context, new RecordingEmailService(), new StubQuoteConfigurationRepository());

        await attestations.AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SalesManager, "Imran Qureshi", 7, "rep@nexora.invalid", default);
        Assert.NotEmpty(await service.GenerateQuotePdfAsync(QuoteId, Tenant));

        var line = await context.QuoteItems.SingleAsync(i => i.Id == 96_101);
        line.UnitPrice = 99.000000m;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<PriceAttestationRequiredException>(
            () => service.GenerateQuotePdfAsync(QuoteId, Tenant));
        Assert.Contains("changed after", error.Message);
    }

    [Fact]
    public async Task The_pdf_renders_once_the_price_source_is_confirmed()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = new QuoteService(context, new RecordingEmailService(), new StubQuoteConfigurationRepository());

        await new PriceAttestationService(context).AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SupplierQuote, "SQ-4471", 7, "rep@nexora.invalid", default);

        Assert.NotEmpty(await service.GenerateQuotePdfAsync(QuoteId, Tenant));
    }

    // ------------------------------------------------------------------ seed

    private static void Seed(ErpRfqAutomationContext context)
    {
        // A revisable quote needs a canonical commercial identity, so the chain is seeded
        // for real: lead -> resolved customer/contact -> RFQ -> quote.
        var lead = Support.Seed.Lead(context, 96_301, Tenant);
        Support.Seed.Customer(context, 96_401, Tenant, "Gulf Industrial");
        Support.Seed.Contact(context, 96_501, Tenant, 96_401);
        context.SaveChanges();
        lead.ResolveCommercialIdentity(96_401, 96_501, "CONFIRMED");

        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = SentStatusId + 1, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "DRAFT", SetupValue = "Draft", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });

        var rfq = new Rfq
        {
            Id = 96_601, Rfqno = "RFQ-96601", RecDate = DateTime.UtcNow, BusinessUnitId = Tenant,
            LeadId = lead.Id, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        context.SaveChanges();

        var quote = new Quote
        {
            Id = QuoteId, QuoteNo = "Q-ATTEST-1", BusinessUnitId = Tenant, Rfqid = rfq.Id,
            // Non-draft so the revision test can revise it; the gate itself is status-agnostic.
            StatusId = SentStatusId,
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
            // R17: the lines below now carry the derived output tax the send gate requires, so this
            // fixture represents a quote that is sendable for every reason EXCEPT the price
            // provenance these tests are about. Without it every case here would fail on the tax
            // gate instead, and would stop testing the attestation at all.
            //   241.00 + 36.15 VAT = 277.15;  160.00 + 24.00 VAT = 184.00;  header 461.15.
            TotalAmount = 461.15m, CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            QuoteItems =
            {
                new QuoteItem
                {
                    Id = 96_101, ItemDescription = "Ball valve, 2in",
                    Quantity = 2m, UnitPrice = 120.500000m, TotalAmount = 277.15m,
                    TaxAmount = 36.15m, TaxCategory = QuoteLineTaxCategories.Standard,
                    TaxRatePercentApplied = 15m,
                    CreatedBy = "tests", CreatedDate = DateTime.UtcNow
                },
                new QuoteItem
                {
                    Id = 96_102, ItemDescription = "Gasket set",
                    Quantity = 2m, UnitPrice = 80.000000m, TotalAmount = 184.00m,
                    TaxAmount = 24.00m, TaxCategory = QuoteLineTaxCategories.Standard,
                    TaxRatePercentApplied = 15m,
                    CreatedBy = "tests", CreatedDate = DateTime.UtcNow
                }
            }
        };
        quote.InheritCommercialIdentity(rfq);
        context.Quotes.Add(quote);
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    /// <summary>No configuration row: the PDF path exercises every built-in default.</summary>
    private sealed class StubQuoteConfigurationRepository : ERP_RFQ_Automation.Interfaces.IQuoteConfigurationRepository
    {
        // Configured, because the PDF now refuses a business unit that cannot say who is sending
        // the quotation. These tests are about the price-attestation gate; the identity gate is
        // pinned by QuoteIssuerIdentityTests. Note the ORDER this relies on: attestation is
        // checked before identity, so `The_pdf_refuses_an_unattested_quote` still gets the
        // attestation exception rather than this one.
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId) =>
            Task.FromResult<QuoteConfiguration?>(new QuoteConfiguration
            {
                BusinessUnitId = businessUnitId,
                CompanyAddress = "King Fahd Road, Al Khobar 34423",
                CompanyPhone = "+966 13 800 0000",
                CompanyEmail = "sales@noorandsons.example"
            });
        public Task AddAsync(QuoteConfiguration config) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration config) => Task.CompletedTask;
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public int SendCount { get; private set; }
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptingEmailSender : IEmailSender
    {
        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default) =>
            Task.FromResult<EmailDeliveryReceipt?>(new("test", "accepted", DateTimeOffset.UtcNow));
    }
}
