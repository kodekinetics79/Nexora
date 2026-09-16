using System.Text.Json;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Templating;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A supplier RFQ email has to tell the supplier what to quote. The queued payload used to
/// carry <c>"{rfqItemId}: {quantity}"</c> — an internal line id and a bare number, which
/// reached a real supplier as "Items: 1: 12" — with no description, maker, part number, unit
/// or needed-by date. These tests pin the per-line contract from the queue to the rendered
/// email, and pin that a message queued under the old contract still sends.
/// </summary>
public sealed class SupplierRfqEmailContentTests
{
    private static readonly DateTime RequiredOn = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_prepared_supplier_rfq_describes_the_line_instead_of_quoting_its_internal_id()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture, line =>
        {
            line.LineItemNo = "3";
            line.ProductShortDescription = "Ball valve 2 in, class 300, flanged";
            line.ItemMaterialCode = "100234";
            line.RequiredDesiredDate = RequiredOn;
        });
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false,
                "content-case", "qa", "corr-content-case")));
        var candidate = Assert.Single(created.Candidates);

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId,
                null, created.Version, "content-prepare", "qa", "corr-content-prepare")));

        await using var verify = fixture.Context();
        var queued = await verify.ProcurementEvents.SingleAsync(x =>
            x.AggregateType == "SupplierSolicitation" && x.AggregateId == prepared.SupplierSolicitationId
            && x.EventType == "SUPPLIER_RFQ_CREATED");
        using var payload = JsonDocument.Parse(queued.PayloadJson);
        var root = payload.RootElement;

        var line = Assert.Single(root.GetProperty("Lines").EnumerateArray());
        Assert.Equal("3", line.GetProperty("LineNumber").GetString());
        Assert.Equal("Ball valve 2 in, class 300, flanged", line.GetProperty("Description").GetString());
        Assert.Equal("QA Maker", line.GetProperty("Maker").GetString());
        Assert.Equal("QA-PART-0", line.GetProperty("MakerPartNumber").GetString());
        Assert.Equal("100234", line.GetProperty("MaterialCode").GetString());
        // The shortfall, not the full demand: 10 asked, 2 on hand.
        Assert.Equal(8m, line.GetProperty("Quantity").GetDecimal());
        Assert.Equal("EA", line.GetProperty("UnitOfMeasure").GetString());
        // SQLite drops DateTimeKind on the way back, so compare the calendar date the supplier reads.
        Assert.Equal(new DateOnly(2026, 10, 1), DateOnly.FromDateTime(line.GetProperty("RequiredOn").GetDateTime()));

        var summary = root.GetProperty("ItemSummary").GetString()!;
        Assert.DoesNotContain(fixture.RfqItemId.ToString(), summary);
        Assert.Contains("Ball valve 2 in", summary);
        Assert.Contains("8 EA", summary);
        // The customer's price never travels to a supplier.
        Assert.False(root.TryGetProperty("UnitPrice", out _));
        Assert.DoesNotContain("UnitPrice", queued.PayloadJson);
    }

    [Fact]
    public async Task A_multi_line_supplier_rfq_lists_every_line_it_covers()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture, line => line.ProductShortDescription = "First line");
        var secondLineId = await fixture.AddRfqLineAsync(5);
        await using (var edit = fixture.Context())
        {
            var second = await edit.Rfqitems.SingleAsync(x => x.Id == secondLineId);
            second.LineItemNo = "2";
            second.ProductShortDescription = "Second line";
            second.ManufacturerName = "Second Maker";
            second.ManufacturerPartNumber = "SM-2";
            await edit.SaveChangesAsync();
        }

        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(
            new CreateSolicitationCommand(fixture.BusinessUnitId, fixture.RfqId, ProcurementTestData.Supplier,
                [fixture.RfqItemId, secondLineId], DateTime.UtcNow.AddDays(2), "multi-line", "qa", "corr-multi-line")));

        await using var verify = fixture.Context();
        var outbox = await verify.ProcurementOutboxMessages.SingleAsync(x => x.SupplierSolicitationId == solicitation.Id);
        using var payload = JsonDocument.Parse(outbox.PayloadJson);
        var lines = payload.RootElement.GetProperty("Lines").EnumerateArray().ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("First line", lines[0].GetProperty("Description").GetString());
        Assert.Equal("Second line", lines[1].GetProperty("Description").GetString());
        Assert.Equal("Second Maker", lines[1].GetProperty("Maker").GetString());
        Assert.Equal("SM-2", lines[1].GetProperty("MakerPartNumber").GetString());
        var summary = payload.RootElement.GetProperty("ItemSummary").GetString()!;
        Assert.Contains("Line 2: Second line", summary);
        Assert.DoesNotContain($"{secondLineId}:", summary);
    }

    [Fact]
    public async Task A_line_with_no_single_maker_carries_the_customers_acceptable_makers()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture, line =>
        {
            line.ManufacturerName = null;
            line.ExtraFields = """{"Approved manufacturers": "Siemens; ABB; Schneider Electric"}""";
        });

        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(fixture.Solicitation("makers")));

        await using var verify = fixture.Context();
        var outbox = await verify.ProcurementOutboxMessages.SingleAsync(x => x.SupplierSolicitationId == solicitation.Id);
        using var payload = JsonDocument.Parse(outbox.PayloadJson);
        var line = Assert.Single(payload.RootElement.GetProperty("Lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("Maker").ValueKind);
        Assert.Equal("Siemens; ABB; Schneider Electric", line.GetProperty("AcceptableMakers").GetString());
    }

    [Fact]
    public async Task The_rendered_email_shows_each_line_in_words_a_supplier_can_act_on()
    {
        var sender = new CapturingSender();
        var service = new NotificationService(sender, new EmailTemplateRenderer(NullLogger<EmailTemplateRenderer>.Instance),
            Options.Create(new NotificationsOptions()), NullLogger<NotificationService>.Instance);

        var receipt = await service.SendRfqToSupplierWithReceiptAsync(new RfqToSupplierNotification
        {
            ToEmail = "quotes@valves.example",
            SupplierName = "Valves Co",
            RfqNumber = "SRFQ-0007-00000012",
            RfqTitle = "Request for quotation for 2 lines",
            ItemSummary = "96070: 12",
            DueDate = "2026-09-22",
            Lines =
            [
                new RfqToSupplierLine
                {
                    LineNumber = "3", Description = "Ball valve 2 in, class 300 <flanged>", Maker = "Velan",
                    MakerPartNumber = "VB-200", MaterialCode = "100234", Quantity = "12", UnitOfMeasure = "EA",
                    RequiredBy = "2026-10-01"
                },
                new RfqToSupplierLine
                {
                    LineNumber = "4", Description = "Gasket, spiral wound", Quantity = "40", UnitOfMeasure = "PC",
                    AcceptableMakers = "Flexitallic; Klinger"
                }
            ]
        });

        Assert.True(receipt.Accepted);
        var message = Assert.Single(sender.Sent);
        foreach (var body in new[] { message.HtmlBody, message.TextBody! })
        {
            Assert.Contains("Line 3", body);
            Assert.Contains("Maker: Velan, part no. VB-200", body);
            Assert.Contains("Material code: 100234", body);
            Assert.Contains("Quantity: 12 EA", body);
            Assert.Contains("Needed by: 2026-10-01", body);
            Assert.Contains("Line 4", body);
            Assert.Contains("Acceptable makers: Flexitallic; Klinger", body);
            Assert.Contains("Quantity: 40 PC", body);
            Assert.Contains("Request for quotation for 2 lines", body);
            // The old bare "id: quantity" never reaches the supplier once lines exist.
            Assert.DoesNotContain("96070: 12", body);
        }
        // Customer-document text is encoded before it enters the HTML part.
        Assert.Contains("class 300 &lt;flanged&gt;", message.HtmlBody);
        Assert.DoesNotContain("<flanged>", message.HtmlBody);
        Assert.Contains("class 300 <flanged>", message.TextBody);
    }

    [Fact]
    public async Task A_message_queued_before_lines_existed_still_renders_its_summary()
    {
        var sender = new CapturingSender();
        var service = new NotificationService(sender, new EmailTemplateRenderer(NullLogger<EmailTemplateRenderer>.Instance),
            Options.Create(new NotificationsOptions()), NullLogger<NotificationService>.Instance);

        await service.SendRfqToSupplierWithReceiptAsync(new RfqToSupplierNotification
        {
            ToEmail = "quotes@valves.example",
            SupplierName = "Valves Co",
            RfqNumber = "RFQ-OLD",
            RfqTitle = "Request for quotation RFQ-OLD",
            ItemSummary = "10 x NX-100",
            DueDate = "2026-09-22"
        });

        var message = Assert.Single(sender.Sent);
        Assert.Contains("10 x NX-100", message.HtmlBody);
        Assert.Contains("Items:      10 x NX-100", message.TextBody);
        Assert.DoesNotContain("{{", message.HtmlBody);
        Assert.DoesNotContain("{{", message.TextBody);
    }

    private static async Task MakeSourcingReadyAsync(ProcurementScenario fixture, Action<Models.Rfqitem> shapeLine)
    {
        await using var context = fixture.Context();
        var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.PreferredSupplierId = ProcurementTestData.Supplier;
        var line = await context.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
        line.ManufacturerPartNumber = "QA-PART-0";
        line.ManufacturerName = "QA Maker";
        shapeLine(line);
        await context.SaveChangesAsync();
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(new("test", "accepted", DateTimeOffset.UtcNow));
        }
    }
}
