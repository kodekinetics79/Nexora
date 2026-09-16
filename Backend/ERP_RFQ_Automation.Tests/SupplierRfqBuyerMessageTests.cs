using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The rep reviews the supplier email before it goes and can write their own message in it
/// (owner: "a email window will open with default content but with the requirement in it and
/// the user will review before sending"). Until now the email's message was a constant nobody
/// could change. These tests pin the message from the prepare call to the solicitation row and
/// on into the queued dispatch payload the worker reads, and pin the two edges: blank keeps the
/// standard sentence, and an over-long message is refused in a sentence the rep can act on.
/// </summary>
public sealed class SupplierRfqBuyerMessageTests
{
    private const string RepMessage = "We need delivery to Jubail by 1 October.\nPlease quote DDP and include HS codes.";

    [Fact]
    public async Task The_reps_message_is_stored_on_the_supplier_rfq_and_travels_in_the_dispatch_payload()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(CreateCase(fixture, "message-case")));
        var candidate = Assert.Single(created.Candidates);

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
            fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
            "message-prepare", "qa", "corr-message-prepare", "  " + RepMessage + "  ")));
        var queued = await fixture.Execute(service => service.QueuePreparedSupplierRfqAsync(new QueuePreparedSupplierRfqCommand(
            fixture.BusinessUnitId, created.Id, prepared.SupplierSolicitationId, prepared.SourcingCaseVersion,
            prepared.SolicitationVersion, "message-queue", "qa-manager", "corr-message-queue")));

        await using var verify = fixture.Context();
        var solicitation = await verify.Set<SupplierSolicitation>().SingleAsync(x => x.Id == prepared.SupplierSolicitationId);
        Assert.Equal(RepMessage, solicitation.BuyerMessage);

        var preparation = await verify.ProcurementEvents.SingleAsync(x =>
            x.AggregateType == "SupplierSolicitation" && x.AggregateId == prepared.SupplierSolicitationId
            && x.EventType == "SUPPLIER_RFQ_CREATED");
        using var preparationPayload = JsonDocument.Parse(preparation.PayloadJson);
        Assert.Equal(RepMessage, preparationPayload.RootElement.GetProperty("Message").GetString());

        var outbox = await verify.ProcurementOutboxMessages.SingleAsync(x => x.SupplierSolicitationId == queued.SupplierSolicitationId);
        using var dispatchPayload = JsonDocument.Parse(outbox.PayloadJson);
        Assert.Equal(RepMessage, dispatchPayload.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task A_blank_message_is_stored_as_none_so_the_email_keeps_its_standard_sentence()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(CreateCase(fixture, "blank-message-case")));
        var candidate = Assert.Single(created.Candidates);

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
            fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
            "blank-message-prepare", "qa", "corr-blank-message", "   ")));

        await using var verify = fixture.Context();
        Assert.Null(await verify.Set<SupplierSolicitation>()
            .Where(x => x.Id == prepared.SupplierSolicitationId).Select(x => x.BuyerMessage).SingleAsync());
        var preparation = await verify.ProcurementEvents.SingleAsync(x =>
            x.AggregateType == "SupplierSolicitation" && x.AggregateId == prepared.SupplierSolicitationId
            && x.EventType == "SUPPLIER_RFQ_CREATED");
        using var payload = JsonDocument.Parse(preparation.PayloadJson);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("Message").ValueKind);
    }

    [Fact]
    public async Task A_message_longer_than_two_thousand_characters_is_refused_before_anything_is_written()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(CreateCase(fixture, "long-message-case")));
        var candidate = Assert.Single(created.Candidates);

        var refused = await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "long-message-prepare", "qa", "corr-long-message", new string('x', 2001)))));

        Assert.Contains("2,000 characters", refused.Message);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.Set<SupplierSolicitation>().ToListAsync());
    }

    private static CreateSourcingCaseCommand CreateCase(ProcurementScenario fixture, string key) => new(
        fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false, key, "qa", $"corr-{key}");

    private static async Task MakeSourcingReadyAsync(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.PreferredSupplierId = ProcurementTestData.Supplier;
        var line = await context.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
        line.ManufacturerPartNumber = "QA-PART-0";
        line.ManufacturerName = "QA Maker";
        await context.SaveChangesAsync();
    }
}
