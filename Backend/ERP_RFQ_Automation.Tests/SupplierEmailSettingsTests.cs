using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Templating;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Procurement.SupplierEmail;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner decision 2026-09-16: a company words its supplier emails its own way (subject, greeting,
/// opening, default message, sign-off), each sales person may keep their own default message and
/// signature, and the part lines stay exactly as Nexora writes them.
/// </summary>
public sealed class SupplierEmailSettingsTests
{
    [Fact]
    public async Task The_company_standard_saves_only_what_differs_from_nexora_and_the_subject_keeps_the_rfq_number()
    {
        using var fixture = new ProcurementScenario();
        await using var db = fixture.Context();
        var service = new SupplierEmailSettingsService(db);
        var d = SupplierEmailDefaults.Texts;

        var saved = await service.SaveCompanyAsync(fixture.BusinessUnitId, new SaveCompanySupplierEmailCommand(
            Subject: "Enquiry from [Company name]",
            Greeting: d.Greeting,
            Opening: "  ",
            DefaultMessage: "Please quote DDP Jubail and include HS codes.",
            SignOff: "Best regards,\nProcurement Team\n[Company name]"), "admin@qa");

        Assert.Equal("Enquiry from [Company name] ([RFQ number])", saved.Subject);
        Assert.Null(saved.Greeting);
        Assert.Null(saved.Opening);
        Assert.Equal("Please quote DDP Jubail and include HS codes.", saved.DefaultMessage);
        Assert.Equal(d, saved.Defaults);

        await Assert.ThrowsAsync<SupplierEmailValidationException>(() => service.SaveCompanyAsync(fixture.BusinessUnitId,
            new SaveCompanySupplierEmailCommand(null, null, null, new string('x', 2001), null), "admin@qa"));
    }

    [Fact]
    public async Task A_sales_persons_own_message_and_signature_replace_the_companys_for_their_emails_only()
    {
        using var fixture = new ProcurementScenario();
        await using var db = fixture.Context();
        var service = new SupplierEmailSettingsService(db);
        await service.SaveCompanyAsync(fixture.BusinessUnitId, new SaveCompanySupplierEmailCommand(
            null, null, null, "Company message.", "Company signature"), "admin@qa");
        var mine = await service.SaveMineAsync(fixture.BusinessUnitId, 501, new SaveMySupplierEmailCommand(
            "My message.", "Ahmed Saleh\nSales Engineer\n+966 50 000 0000"), "ahmed@qa");

        Assert.Equal("Company message.", mine.Company.DefaultMessage);
        var ahmed = await service.GetEffectiveAsync(fixture.BusinessUnitId, 501);
        var colleague = await service.GetEffectiveAsync(fixture.BusinessUnitId, 502);
        Assert.Equal("My message.", ahmed.DefaultMessage);
        Assert.StartsWith("Ahmed Saleh", ahmed.SignOff);
        Assert.Equal("Company message.", colleague.DefaultMessage);
        Assert.Equal("Company signature", colleague.SignOff);
        Assert.Equal(SupplierEmailDefaults.Texts.Subject, ahmed.Subject);

        // Saving the company's own wording as yours is the same as having none of your own.
        var cleared = await service.SaveMineAsync(fixture.BusinessUnitId, 501, new SaveMySupplierEmailCommand("Company message.", ""), "ahmed@qa");
        Assert.Null(cleared.DefaultMessage);
        Assert.Null(cleared.SignOff);
    }

    [Fact]
    public async Task The_email_uses_the_saved_wording_and_the_part_lines_are_unchanged()
    {
        var sender = new CapturingEmailSender();
        var service = new NotificationService(sender, new EmailTemplateRenderer(NullLogger<EmailTemplateRenderer>.Instance),
            Options.Create(new NotificationsOptions()), NullLogger<NotificationService>.Instance);

        await service.SendRfqToSupplierWithReceiptAsync(new RfqToSupplierNotification
        {
            ToEmail = "quotes@valves.example", SupplierName = "Valves Co", BuyerCompany = "Noor And Sons",
            RfqNumber = "SRFQ-0007-00000012", RfqTitle = "Request for quotation for 1 line", DueDate = "2026-09-22",
            SubjectLine = "Enquiry SRFQ-0007-00000012 from Noor And Sons",
            Greeting = "Hello Valves Co team,",
            Opening = "Noor And Sons needs your best offer for:",
            SignOff = "Best regards,\nAhmed Saleh\nNoor And Sons",
            Message = "Quote DDP Jubail.",
            SendFromMailboxId = 42, ReplyToAddress = "rfq@noorandsons.example", FromDisplayName = "Noor And Sons",
            Lines = [new RfqToSupplierLine { LineNumber = "3", Description = "Relay", MakerPartNumber = "SEL-751", Quantity = "3", UnitOfMeasure = "EA" }]
        });

        var message = Assert.Single(sender.Sent);
        Assert.Equal("Enquiry SRFQ-0007-00000012 from Noor And Sons", message.Subject);
        Assert.Equal(42, message.OwningMailboxId);
        Assert.Equal("rfq@noorandsons.example", message.ReplyTo?.Address);
        Assert.Equal("Noor And Sons", message.FromDisplayName);
        Assert.Contains("Hello Valves Co team,", message.TextBody);
        Assert.Contains("Noor And Sons needs your best offer for:", message.TextBody);
        Assert.Contains("Line 3: Relay", message.TextBody);
        Assert.Contains("Part no. SEL-751", message.TextBody);
        Assert.Contains("Quantity: 3 EA", message.TextBody);
        Assert.Contains("Quote DDP Jubail.", message.TextBody);
        Assert.Contains("Please reply to this email with your price", message.TextBody);
        Assert.Contains("Ahmed Saleh", message.TextBody);
        Assert.DoesNotContain("Kind regards", message.TextBody);
        Assert.Contains("Ahmed Saleh<br", message.HtmlBody);
    }

    [Fact]
    public async Task A_request_carries_the_wording_as_it_was_when_send_was_pressed_and_refuses_a_mailbox_that_is_not_the_companys()
    {
        using var fixture = new ProcurementScenario();
        await SupplierRfqEmailContentTests_MakeSourcingReady(fixture);
        await using (var db = fixture.Context())
            await new SupplierEmailSettingsService(db).SaveMineAsync(fixture.BusinessUnitId, 501,
                new SaveMySupplierEmailCommand("My default.", "Ahmed Saleh"), "ahmed@qa");
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false, "wording", "qa", "corr-wording")));
        var candidate = Assert.Single(created.Candidates);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "wording-bad-mailbox", "qa", "corr-bad-mailbox", UserId: 501, SendFromMailboxId: 999_999))));

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "wording-prepare", "qa", "corr-wording-prepare", UserId: 501)));

        await using var verify = fixture.Context();
        var createdEvent = await verify.ProcurementEvents.SingleAsync(x => x.AggregateType == "SupplierSolicitation"
            && x.AggregateId == prepared.SupplierSolicitationId && x.EventType == "SUPPLIER_RFQ_CREATED");
        Assert.Contains("\"SignOff\":\"Ahmed Saleh\"", createdEvent.PayloadJson);
        Assert.Contains("\"DefaultMessage\":\"My default.\"", createdEvent.PayloadJson);
        Assert.Contains("SRFQ-", createdEvent.PayloadJson);
        Assert.DoesNotContain("[RFQ number]", createdEvent.PayloadJson);
        Assert.DoesNotContain("[Supplier name]", createdEvent.PayloadJson);
    }

    [Fact]
    public async Task Cc_and_bcc_reach_every_suppliers_email_and_the_company_can_set_them_as_always()
    {
        using var fixture = new ProcurementScenario();
        await SupplierRfqEmailContentTests_MakeSourcingReady(fixture);
        await using (var db = fixture.Context())
        {
            var saved = await new SupplierEmailSettingsService(db).SaveCompanyAsync(fixture.BusinessUnitId,
                new SaveCompanySupplierEmailCommand(null, null, null, null, null, ["Purchasing@QA.example"], ["archive@qa.example"]), "admin@qa");
            Assert.Equal(["purchasing@qa.example"], saved.DefaultCc);
            var effective = await new SupplierEmailSettingsService(db).GetEffectiveAsync(fixture.BusinessUnitId, 501);
            Assert.Equal(["archive@qa.example"], effective.DefaultBcc);
            await Assert.ThrowsAsync<SupplierEmailValidationException>(() => new SupplierEmailSettingsService(db).SaveCompanyAsync(
                fixture.BusinessUnitId, new SaveCompanySupplierEmailCommand(null, null, null, null, null, ["not an email"], null), "admin@qa"));
        }
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false, "copies", "qa", "corr-copies")));
        var candidate = Assert.Single(created.Candidates);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "copies-bad", "qa", "corr-copies-bad", Cc: ["manager at company"]))));

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "copies-prepare", "qa", "corr-copies-prepare", Cc: ["Manager@QA.example, purchasing@qa.example"], Bcc: ["archive@qa.example"])));
        await using var verify = fixture.Context();
        var createdEvent = await verify.ProcurementEvents.SingleAsync(x => x.AggregateType == "SupplierSolicitation"
            && x.AggregateId == prepared.SupplierSolicitationId && x.EventType == "SUPPLIER_RFQ_CREATED");
        Assert.Contains("\"Cc\":[\"manager@qa.example\",\"purchasing@qa.example\"]", createdEvent.PayloadJson);
        Assert.Contains("\"Bcc\":[\"archive@qa.example\"]", createdEvent.PayloadJson);

        var sender = new CapturingEmailSender();
        var notifications = new NotificationService(sender, new EmailTemplateRenderer(NullLogger<EmailTemplateRenderer>.Instance),
            Options.Create(new NotificationsOptions()), NullLogger<NotificationService>.Instance);
        await notifications.SendRfqToSupplierWithReceiptAsync(new RfqToSupplierNotification
        {
            ToEmail = "quotes@valves.example", SupplierName = "Valves Co", RfqNumber = "SRFQ-1",
            CcAddresses = ["manager@qa.example"], BccAddresses = ["archive@qa.example"],
        });
        var message = Assert.Single(sender.Sent);
        Assert.Equal("manager@qa.example", Assert.Single(message.Cc).Address);
        Assert.Equal("archive@qa.example", Assert.Single(message.Bcc).Address);
    }

    private static async Task SupplierRfqEmailContentTests_MakeSourcingReady(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.PreferredSupplierId = ProcurementTestData.Supplier;
        await context.SaveChangesAsync();
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(new EmailDeliveryReceipt("test", "id", DateTimeOffset.UtcNow));
        }
    }
}
