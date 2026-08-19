using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A buyer who takes a price over the phone could not record it. Response capture requires a
/// solicitation that actually reached the supplier — a correct requirement — but the only thing
/// that could ever satisfy it was <c>ProcurementDispatchWorker</c> delivering an email. On a
/// deployment with no outbound mail, and for every supplier reached by phone, in person or through
/// their own portal, the workbench refused a response the buyer was holding in their hand, while
/// the Supplier Quote Inbox wrote the same canonical revision with no email at all.
///
/// <para>These tests hold both halves: the guard still refuses a solicitation that never reached
/// the supplier, and a recorded out-of-band delivery is a legitimate way to satisfy it.</para>
/// </summary>
public sealed class Gate2SolicitationDeliveryRecordTests
{
    [Fact]
    public async Task Response_capture_is_still_refused_when_nothing_reached_the_supplier()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "guard");

        var refusal = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.CaptureSupplierQuoteAsync(fixture.Quote(prepared.SupplierSolicitationId, "guard-capture"))));

        Assert.Contains("delivery evidence", refusal.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.SupplierQuotedItems.ToListAsync());
        Assert.Equal(SolicitationStatus.PendingDispatch, await verify.Set<SupplierSolicitation>()
            .Where(x => x.Id == prepared.SupplierSolicitationId).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Recording_how_the_supplier_was_reached_lets_the_buyer_capture_the_price()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "phone");

        var recorded = await fixture.Execute(service => service.RecordSolicitationDeliveryAsync(
            Record(fixture, prepared.SupplierSolicitationId, prepared.SolicitationVersion, "phone-delivery")));

        Assert.False(recorded.Replayed);
        Assert.Equal(nameof(SolicitationStatus.Sent), recorded.Status);
        Assert.Equal(SolicitationDeliveryChannels.Phone, recorded.Channel);
        Assert.Equal("buyer@nexora.test", recorded.RecordedBy);

        var captured = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(prepared.SupplierSolicitationId, "phone-capture")));

        Assert.Single(captured.LineIds);
        await using var verify = fixture.Context();
        Assert.Equal(SolicitationStatus.Responded, await verify.Set<SupplierSolicitation>()
            .Where(x => x.Id == prepared.SupplierSolicitationId).Select(x => x.Status).SingleAsync());
        var record = Assert.Single(await verify.SupplierSolicitationDeliveryRecords.ToListAsync());
        Assert.Equal(prepared.SupplierSolicitationId, record.SupplierSolicitationId);
        Assert.Equal(fixture.BusinessUnitId, record.BusinessUnitId);
        Assert.Equal("Spoke to Ahmed on the phone; he quoted 12 per unit, 5 day lead time.", record.Note);
        // The actor is the authenticated caller, never a name the request body chose.
        Assert.Equal("buyer@nexora.test", record.RecordedBy);
        Assert.NotEqual(default, record.RecordedOn);
        Assert.Contains(await verify.ProcurementEvents.ToListAsync(), x =>
            x.EventType == "SUPPLIER_SOLICITATION_DELIVERY_RECORDED"
            && x.AggregateType == "SupplierSolicitation"
            && x.AggregateId == prepared.SupplierSolicitationId
            && x.Actor == "buyer@nexora.test");
    }

    /// <summary>
    /// The two kinds of delivery must stay tellable apart everywhere they are read. An email
    /// delivery names a provider and an acceptance reference; this names a person and what they
    /// said happened, and no screen may dress one as the other.
    /// </summary>
    [Fact]
    public async Task Recorded_delivery_is_never_presented_as_an_email_delivery()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "distinct");

        await fixture.Execute(service => service.RecordSolicitationDeliveryAsync(
            Record(fixture, prepared.SupplierSolicitationId, prepared.SolicitationVersion, "distinct-delivery")
            with { DeliveryChannel = SolicitationDeliveryChannels.InPerson }));

        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        var view = Assert.Single(workbench.Solicitations,
            x => x.Id == prepared.SupplierSolicitationId);

        Assert.Equal("SENT", view.Status);
        Assert.NotNull(view.RecordedDelivery);
        Assert.Equal(SolicitationDeliveryChannels.InPerson, view.RecordedDelivery!.Channel);
        Assert.Equal("buyer@nexora.test", view.RecordedDelivery.RecordedBy);
        // Nothing that says "Nexora emailed this" may be populated: no provider accepted anything.
        Assert.Null(view.ProviderReference);
        Assert.Null(view.LastErrorCode);
        Assert.Equal(0, view.AttemptCount);
        Assert.NotEqual(SolicitationDeliveryChannels.Email, view.Channel);
        Assert.Equal(SolicitationDeliveryChannels.InPerson, view.Channel);

        await using var verify = fixture.Context();
        Assert.Empty(await verify.ProcurementOutboxMessages.ToListAsync());
        Assert.Single(await verify.SupplierSolicitationDeliveryRecords.ToListAsync());
    }

    [Fact]
    public async Task Recorded_delivery_without_an_account_of_what_happened_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "no-note");
        var command = Record(fixture, prepared.SupplierSolicitationId, prepared.SolicitationVersion, "no-note-delivery");

        var blank = await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.RecordSolicitationDeliveryAsync(command with { Note = "   " })));
        Assert.Contains("what happened", blank.Message, StringComparison.OrdinalIgnoreCase);

        // A channel nobody offers, and the one channel a human may never assert, are both refused.
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.RecordSolicitationDeliveryAsync(command with
            {
                DeliveryChannel = "Carrier pigeon", IdempotencyKey = "no-note-channel"
            })));
        var email = await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.RecordSolicitationDeliveryAsync(command with
            {
                DeliveryChannel = SolicitationDeliveryChannels.Email, IdempotencyKey = "no-note-email"
            })));
        Assert.Contains("cannot be entered by hand", email.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.Context();
        Assert.Empty(await verify.SupplierSolicitationDeliveryRecords.ToListAsync());
        Assert.Equal(SolicitationStatus.PendingDispatch, await verify.Set<SupplierSolicitation>()
            .Where(x => x.Id == prepared.SupplierSolicitationId).Select(x => x.Status).SingleAsync());
        // The refusal is total: capture is still barred, so a rejected note cannot leave a door ajar.
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.CaptureSupplierQuoteAsync(fixture.Quote(prepared.SupplierSolicitationId, "no-note-capture"))));
    }

    [Fact]
    public async Task Recorded_delivery_cannot_reach_another_tenants_solicitation()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "tenant");

        await using var intruderContext = fixture.Context(fixture.OtherBusinessUnitId);
        var intruder = new ProcurementApplicationService(intruderContext);
        await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            intruder.RecordSolicitationDeliveryAsync(new RecordSolicitationDeliveryCommand(
                fixture.OtherBusinessUnitId, prepared.SupplierSolicitationId,
                SolicitationDeliveryChannels.Phone, "Called them and agreed the price.",
                prepared.SolicitationVersion, "cross-tenant-delivery", "intruder@other.test",
                "corr-cross-tenant-delivery")));

        await using var verify = fixture.Context();
        Assert.Empty(await verify.SupplierSolicitationDeliveryRecords.ToListAsync());
        Assert.Equal(SolicitationStatus.PendingDispatch, await verify.Set<SupplierSolicitation>()
            .Where(x => x.Id == prepared.SupplierSolicitationId).Select(x => x.Status).SingleAsync());
    }

    /// <summary>
    /// A retried command must not record a second, contradictory account of the same delivery, and
    /// the same key carrying a different story must be refused outright.
    /// </summary>
    [Fact]
    public async Task Recorded_delivery_replays_and_refuses_a_reused_key_with_a_different_story()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "replay");
        var command = Record(fixture, prepared.SupplierSolicitationId, prepared.SolicitationVersion, "replay-delivery");

        var first = await fixture.Execute(service => service.RecordSolicitationDeliveryAsync(command));
        var replay = await fixture.Execute(service => service.RecordSolicitationDeliveryAsync(command));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(first.Note, replay.Note);
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.RecordSolicitationDeliveryAsync(command with { Note = "Actually I emailed them." })));

        await using var verify = fixture.Context();
        Assert.Single(await verify.SupplierSolicitationDeliveryRecords.ToListAsync());
    }

    /// <summary>
    /// A Supplier RFQ the dispatch worker is still trying to email must not also be handed over by
    /// hand: the supplier would get the same request twice, from two routes, with two stories about
    /// how it arrived.
    /// </summary>
    [Fact]
    public async Task Delivery_cannot_be_recorded_while_nexora_is_still_trying_to_email_it()
    {
        using var fixture = new ProcurementScenario();
        var prepared = await PrepareSupplierRfqAsync(fixture, "queued");
        var queued = await fixture.Execute(service => service.QueuePreparedSupplierRfqAsync(
            new QueuePreparedSupplierRfqCommand(fixture.BusinessUnitId, prepared.SourcingCaseId,
                prepared.SupplierSolicitationId, prepared.SourcingCaseVersion, prepared.SolicitationVersion,
                "queued-dispatch", "qa", "corr-queued-dispatch")));

        var refusal = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.RecordSolicitationDeliveryAsync(
                Record(fixture, queued.SupplierSolicitationId, queued.SolicitationVersion, "queued-delivery"))));

        Assert.Contains("still trying to email", refusal.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.SupplierSolicitationDeliveryRecords.ToListAsync());
    }

    private static RecordSolicitationDeliveryCommand Record(ProcurementScenario fixture,
        long solicitationId, long expectedVersion, string key) => new(
        fixture.BusinessUnitId, solicitationId, SolicitationDeliveryChannels.Phone,
        "Spoke to Ahmed on the phone; he quoted 12 per unit, 5 day lead time.",
        expectedVersion, key, "buyer@nexora.test", $"corr-{key}");

    private static async Task<PreparedSupplierRfqResult> PrepareSupplierRfqAsync(
        ProcurementScenario fixture, string key)
    {
        await using (var context = fixture.Context())
        {
            var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
            context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = $"NXR-QA-{key}";
            var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
            product.PreferredSupplierId = ProcurementTestData.Supplier;
            await context.SaveChangesAsync();
        }

        var sourcingCase = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId,
                10, false, $"{key}-case", "qa", $"corr-{key}-case")));
        var candidate = Assert.Single(sourcingCase.Candidates);
        return await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, sourcingCase.Id, candidate.SupplierId,
                DateTime.UtcNow.AddDays(2), sourcingCase.Version, $"{key}-prepare", "qa", $"corr-{key}-prepare")));
    }
}
