using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Models = ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 7 Module 7 — FR-DLM-01, 02, 03, 05, 07.
///
/// <para>Every test here asserts a DEPENDENCE, not a round trip. The question each one answers is
/// the wiring contract's: if this wiring were deleted, what would break? So the assertions are on
/// refusals, on the number a second subsystem reads, and on the outcome a third derives — never on
/// "the value I saved came back".</para>
///
/// <para>PostgreSQL CHECK constraints are not exercised here: the portable lane runs SQLite with
/// <c>ignore_check_constraints</c> on. Every invariant those constraints express is also enforced
/// in <see cref="DeliveryConfirmationService"/>, and that is what these tests hit.</para>
/// </summary>
public sealed class Gate7DeliveryConfirmationTests
{
    private const long Tenant = 700;
    private const long OtherTenant = 701;
    private const long OrderId = 7100;
    private const long OrderItemId = 7110;
    private const long ShipmentId = 7200;
    private const long ShipmentItemId = 7210;
    private const long DeliveredStatusId = 3300;
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    // ================================================ FR-DLM-05: the ladder is a lifecycle

    [Fact]
    public async Task An_operator_cannot_stamp_a_shipment_DELIVERED()
    {
        using var db = NewDatabase();
        var service = Confirmations(db);

        // DELIVERED is an OUTCOME of what the customer accepted. If an operator could select it,
        // cumulative delivered would once again be a word somebody typed — which is the whole
        // reason FR-DLM-02 could not be built before this gate.
        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => service.TransitionAsync(Tenant, ShipmentId, DeliveryStatuses.Delivered, "clerk"));
        Assert.Contains("cannot move", failure.Message);

        var exception = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => service.TransitionAsync(Tenant, ShipmentId, DeliveryStatuses.DeliveryException, "clerk"));
        Assert.Contains("cannot move", exception.Message);
    }

    [Fact]
    public async Task A_confirmed_shipment_cannot_be_cancelled()
    {
        using var db = NewDatabase();
        await ConfirmAsync(db, accepted: 4m);

        // Once a customer has signed, "cancellation" is a return or a credit note — a commercial
        // transaction with its own evidence — not a status change that silently un-accrues a
        // quantity an invoice may already reference.
        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => Confirmations(db).TransitionAsync(Tenant, ShipmentId, DeliveryStatuses.Cancelled, "clerk"));
        Assert.Contains("cannot be cancelled", failure.Message);
        Assert.Contains("left the warehouse", failure.Message);
    }

    // ============ D1: cancelling a despatched shipment must not free a quantity nothing gave back

    [Fact]
    public async Task A_despatched_shipment_cannot_be_cancelled_because_nothing_reverses_the_goods_issue()
    {
        using var db = NewDatabase();

        // The stock issue and the lot consumption are written at shipment creation
        // (ShipmentController.IssueOrderStockAsync). DeliveryConfirmationService takes no stock
        // dependency at all, and CANCELLED sits outside DeliveryStatuses.Despatched — so if this
        // transition were allowed, one click would remove the quantity from the over-shipment
        // ceiling AND from the delivered ledger while the material was already on a lorry, and the
        // order line would be fully re-shippable against stock that had gone.
        foreach (var inFlight in new[] { DeliveryStatuses.Dispatched, DeliveryStatuses.InTransit })
        {
            await SetStatusAsync(db, inFlight);

            var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
                () => Confirmations(db).TransitionAsync(Tenant, ShipmentId, DeliveryStatuses.Cancelled, "clerk"));

            // The refusal has to be legible at a loading bay, and it has to name the alternative
            // that IS honest — a full refusal is a confirmation with zero accepted, not a deletion.
            Assert.Contains("cannot be cancelled", failure.Message);
            Assert.Contains("goods-return", failure.Message);
            Assert.Contains("REJECTED", failure.Message);

            // Nothing moved, and — the whole point — the despatched quantity is still on the order
            // line. This assertion is what fails if the guard is removed.
            await using var context = db.ContextFor(Tenant);
            Assert.Equal(inFlight, (await context.Shipments.SingleAsync(s => s.Id == ShipmentId)).DeliveryStatus);
            Assert.Equal(4m, Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId)).DespatchedQuantity);
        }
    }

    [Fact]
    public void The_cancellable_set_is_the_single_authority_and_names_only_the_pre_despatch_state()
    {
        // Wiring-contract failure #9: the guard lives with the constants, once, so the next status
        // added to the ladder is a visible decision in one file. If DISPATCHED or IN_TRANSIT is
        // ever put back into Cancellable, this fails before anything reaches a warehouse.
        Assert.Equal(new[] { DeliveryStatuses.Scheduled }, DeliveryStatuses.Cancellable.ToArray());
        foreach (var despatched in DeliveryStatuses.Despatched)
        {
            Assert.False(DeliveryStatuses.CanTransition(despatched, DeliveryStatuses.Cancelled),
                $"{despatched} must not be cancellable: nothing in this system reverses its goods issue.");
            Assert.DoesNotContain(despatched, DeliveryStatuses.Withdrawable);
        }
    }

    [Fact]
    public async Task A_shipment_that_never_left_can_still_be_cancelled()
    {
        using var db = NewDatabase();
        await SetStatusAsync(db, DeliveryStatuses.Scheduled);

        // The refusal above is narrow, not blanket. A plan that was abandoned before anything was
        // picked accrued nothing, so cancelling it reverses nothing and is the honest record.
        Assert.Equal(DeliveryStatuses.Cancelled,
            await Confirmations(db).TransitionAsync(Tenant, ShipmentId, DeliveryStatuses.Cancelled, "clerk"));
    }

    // ======================================= D2: a signed shipment cannot be soft-deleted, at all

    [Fact]
    public async Task A_despatched_shipment_cannot_be_deleted()
    {
        using var db = NewDatabase();
        await using var context = db.ContextFor(Tenant);

        // The delete path set IsActive = false with no status check whatever. This row is the only
        // account of a goods issue that really happened.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ShipmentRepository(context).DeleteShipmentAsync(
                ShipmentId, Tenant, "Raised against the wrong order.", "clerk@tenant"));
        Assert.Contains("cannot be deleted", failure.Message);

        Assert.True((await context.Shipments.SingleAsync(s => s.Id == ShipmentId)).IsActive);
        Assert.Equal(4m, Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId)).DespatchedQuantity);
    }

    [Fact]
    public async Task A_shipment_with_a_proof_of_delivery_cannot_be_deleted()
    {
        using var db = NewDatabase();
        await ConfirmAsync(db, accepted: 4m);

        // The status is walked back to a deletable one FIRST, so the status guard cannot be what
        // refuses this. The POD check has to stand on its own: DeliveryStatus is one witness that a
        // customer signed, and the signed document is the other. Delete the second check and this
        // test goes green while a signature disappears.
        await SetStatusAsync(db, DeliveryStatuses.Scheduled);

        await using var context = db.ContextFor(Tenant);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ShipmentRepository(context).DeleteShipmentAsync(
                ShipmentId, Tenant, "Duplicate note.", "clerk@tenant"));
        Assert.Contains("proof of delivery", failure.Message);

        Assert.True((await context.Shipments.SingleAsync(s => s.Id == ShipmentId)).IsActive);
    }

    [Fact]
    public async Task A_withdrawal_states_who_and_why_or_it_is_refused()
    {
        using var db = NewDatabase();
        await SetStatusAsync(db, DeliveryStatuses.Scheduled);
        await using var context = db.ContextFor(Tenant);
        var repository = new ShipmentRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.DeleteShipmentAsync(ShipmentId, Tenant, "   ", "clerk@tenant"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.DeleteShipmentAsync(ShipmentId, Tenant, "Raised in error.", " "));
        Assert.True((await context.Shipments.SingleAsync(s => s.Id == ShipmentId)).IsActive);

        await repository.DeleteShipmentAsync(
            ShipmentId, Tenant, "Customer moved the collection to next month.", "clerk@tenant");

        var withdrawn = await context.Shipments.SingleAsync(s => s.Id == ShipmentId);
        Assert.False(withdrawn.IsActive);
        Assert.Equal("clerk@tenant", withdrawn.ModifiedBy);

        // Who, when and why survive the row leaving every list — the point of attributing it.
        var record = await context.ShipmentStatusHistories.AsNoTracking()
            .Where(h => h.ShipmentId == ShipmentId && h.Notes!.Contains("withdrawn"))
            .SingleAsync();
        Assert.Equal("clerk@tenant", record.ChangedBy);
        Assert.Contains("Customer moved the collection to next month.", record.Notes);
    }

    [Fact]
    public async Task The_ledger_excludes_an_inactive_shipment_on_both_sides()
    {
        using var db = NewDatabase();
        await ConfirmAsync(db, accepted: 3m, reason: DeliveryExceptionReasons.Damaged);

        // Written straight to the column, deliberately: the repository now refuses this, and the
        // ledger must not depend on that guard holding to be correct. The despatched side filtered
        // Shipment.IsActive and the accepted side did not — so a withdrawn shipment lost its
        // despatched quantity and KEPT its accepted quantity, which is the number that caps an
        // invoice. The one figure that survived deletion was the one that authorises money.
        await using (var tamper = db.ContextFor(Tenant))
        {
            (await tamper.Shipments.SingleAsync(s => s.Id == ShipmentId)).IsActive = false;
            await tamper.SaveChangesAsync();
        }

        var line = Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId));
        Assert.Equal(0m, line.DespatchedQuantity);
        Assert.Equal(0m, line.AcceptedQuantity);
        Assert.Equal(0m, line.RefusedQuantity);
        Assert.Equal(4m, line.OutstandingQuantity);

        // Both shapes the invoice ceiling reads, not just the order-scoped one.
        Assert.Empty(await Ledger(db).AcceptedByOrderItemAsync(Tenant, [OrderItemId]));
        Assert.Empty(await Ledger(db).CapsByOrderItemAsync(Tenant, [OrderItemId]));
    }

    [Fact]
    public async Task A_scheduled_shipment_cannot_be_confirmed_received()
    {
        using var db = NewDatabase();
        await SetStatusAsync(db, DeliveryStatuses.Scheduled);

        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => ConfirmAsync(db, accepted: 4m));
        Assert.Contains("SCHEDULED", failure.Message);
    }

    // ============================= FR-DLM-02: delivered means accepted, and it is a second number

    [Fact]
    public async Task Despatched_and_accepted_are_two_different_numbers()
    {
        using var db = NewDatabase();

        // Goods have left, nobody has signed. The ledger must NOT report them as delivered.
        var beforeConfirmation = Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId));
        Assert.Equal(4m, beforeConfirmation.AwardedQuantity);
        Assert.Equal(4m, beforeConfirmation.DespatchedQuantity);
        Assert.Equal(0m, beforeConfirmation.AcceptedQuantity);
        Assert.Equal(4m, beforeConfirmation.AwaitingConfirmationQuantity);
        Assert.False(beforeConfirmation.IsFullyDelivered);

        await ConfirmAsync(db, accepted: 3m, reason: DeliveryExceptionReasons.Damaged);

        var afterConfirmation = Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId));
        Assert.Equal(4m, afterConfirmation.DespatchedQuantity);
        Assert.Equal(3m, afterConfirmation.AcceptedQuantity);
        Assert.Equal(1m, afterConfirmation.RefusedQuantity);
        Assert.Equal(0m, afterConfirmation.AwaitingConfirmationQuantity);
        // The refused unit is still owed: the customer ordered it and does not have it.
        Assert.Equal(1m, afterConfirmation.OutstandingQuantity);
    }

    [Fact]
    public async Task A_cancelled_shipment_accrues_no_despatched_quantity()
    {
        using var db = NewDatabase();
        // Cancellation is reachable only from SCHEDULED — see
        // A_despatched_shipment_cannot_be_cancelled_because_nothing_reverses_the_goods_issue.
        await CancelBeforeDespatchAsync(db);

        // If the ledger stopped consulting DeliveryStatuses.Despatched, a cancelled despatch would
        // permanently consume the order line with goods that never left.
        var line = Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId));
        Assert.Equal(0m, line.DespatchedQuantity);
        Assert.Equal(0m, line.AcceptedQuantity);
        Assert.Equal(4m, line.OutstandingQuantity);
    }

    // =========================================== FR-DLM-02 downstream: the invoice ceiling depends

    [Fact]
    public async Task An_invoice_cannot_bill_more_than_the_customer_accepted()
    {
        using var db = NewDatabase();
        await ConfirmAsync(db, accepted: 3m, reason: DeliveryExceptionReasons.Rejected);

        await using var context = db.ContextFor(Tenant);
        var finance = new CommercialFinanceApplicationService(context);

        // Four were ordered and four were despatched, so the ORDERED ceiling would allow this. Only
        // the delivered ledger stops it, which is the point: billing a customer for a carton they
        // refused at the door is how a receivable becomes a dispute.
        var failure = await Assert.ThrowsAsync<FinanceConflictException>(
            () => finance.CreateInvoiceAsync(Tenant, OrderId, "inv-over",
                new CreateInvoiceRequest(null, null, [new CreateInvoiceLineRequest(OrderItemId, 4m)]),
                "billing"));
        Assert.Contains("accepted", failure.Message, StringComparison.OrdinalIgnoreCase);

        // And the accepted quantity invoices cleanly, so the cap is a ceiling and not a blockade.
        var invoice = await finance.CreateInvoiceAsync(Tenant, OrderId, "inv-ok",
            new CreateInvoiceRequest(null, null, [new CreateInvoiceLineRequest(OrderItemId, 3m)]),
            "billing");
        Assert.Equal(3m, Assert.Single(invoice.Lines).Quantity);
    }

    [Fact]
    public async Task An_unconfirmed_despatch_is_not_invoiceable()
    {
        using var db = NewDatabase();

        await using var context = db.ContextFor(Tenant);
        var finance = new CommercialFinanceApplicationService(context);

        // Goods on a lorry are neither in the warehouse nor with the customer. Before this gate the
        // ceiling was the ORDERED quantity, so a despatched order could be invoiced in full with
        // nobody having signed for anything.
        var failure = await Assert.ThrowsAsync<FinanceConflictException>(
            () => finance.CreateInvoiceAsync(Tenant, OrderId, "inv-early",
                new CreateInvoiceRequest(null, null, [new CreateInvoiceLineRequest(OrderItemId, 1m)]),
                "billing"));
        Assert.Contains("0 accepted", failure.Message);
    }

    [Fact]
    public async Task A_line_with_nothing_despatched_stays_on_the_pre_existing_ordered_ceiling()
    {
        using var db = NewDatabase();
        // Cancel before despatch, so the delivery module knows nothing about this line at all.
        await CancelBeforeDespatchAsync(db);

        await using var context = db.ContextFor(Tenant);
        var invoice = await new CommercialFinanceApplicationService(context).CreateInvoiceAsync(
            Tenant, OrderId, "inv-nodelivery",
            new CreateInvoiceRequest(null, null, [new CreateInvoiceLineRequest(OrderItemId, 4m)]),
            "billing");

        // This is the STATED boundary of the delivered-quantity ceiling, not an oversight: advance
        // and progress invoicing keep working exactly as they did, and whether they should is an
        // open commercial-policy question recorded in CreateInvoiceAsync. If that question is
        // answered "no", this test is the one that changes.
        Assert.Equal(4m, Assert.Single(invoice.Lines).Quantity);
    }

    [Fact]
    public async Task A_draft_raised_before_a_refusal_cannot_be_issued_after_it()
    {
        using var db = NewDatabase();
        await using var context = db.ContextFor(Tenant);
        var finance = new CommercialFinanceApplicationService(context);

        // Despatched but unconfirmed lines are already blocked at draft creation, so the draft is
        // raised while the delivery module still knows nothing — then the goods go out, and the
        // customer refuses two.
        await CancelBeforeDespatchAsync(db);
        var draft = await finance.CreateInvoiceAsync(Tenant, OrderId, "inv-race",
            new CreateInvoiceRequest(null, null, [new CreateInvoiceLineRequest(OrderItemId, 4m)]),
            "billing");
        await SetStatusAsync(db, DeliveryStatuses.Dispatched);
        await ConfirmAsync(db, accepted: 2m, reason: DeliveryExceptionReasons.Rejected);

        // A ceiling evaluated only when the draft was written is a control a patient user walks past.
        var failure = await Assert.ThrowsAsync<FinanceConflictException>(
            () => finance.IssueAsync(Tenant, draft.Id, new IssueDocumentRequest(draft.Version), "billing"));
        Assert.Contains("more than the customer accepted", failure.Message);
    }

    // =============================================================== FR-DLM-03: proof of delivery

    [Fact]
    public async Task A_confirmation_writes_the_proof_and_the_accepted_quantities_in_one_write()
    {
        using var db = NewDatabase();
        var view = await ConfirmAsync(db, accepted: 4m);

        Assert.Equal(DeliveryStatuses.Delivered, view.Outcome);
        Assert.Equal("Faisal Al-Harbi", view.ReceivedByName);

        await using var verify = db.ContextFor(Tenant);
        var proof = Assert.Single(await verify.DeliveryProofs.ToListAsync());
        var line = Assert.Single(await verify.DeliveryProofLines.ToListAsync());
        Assert.Equal(proof.Id, line.DeliveryProofId);
        Assert.Equal(4m, line.AcceptedQuantity);
        // A quantity snapshot, not a join: the signed document must keep saying what it said.
        Assert.Equal(4m, line.DespatchedQuantity);
        // The shipment's own status follows from the numbers, in the same transaction.
        Assert.Equal(DeliveryStatuses.Delivered,
            await verify.Shipments.Where(s => s.Id == ShipmentId).Select(s => s.DeliveryStatus).SingleAsync());
    }

    [Fact]
    public async Task A_confirmation_with_no_receiving_contact_is_refused()
    {
        using var db = NewDatabase();

        // Everything else on a POD may legitimately be missing — a driver with no signal, a storeman
        // who refuses a photograph. The name is not optional: an unsigned note proves nothing.
        var failure = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-noname",
                Command(4m) with { ReceivedByName = "   " }, "driver"));
        Assert.Contains("required", failure.Message);

        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.DeliveryProofs.ToListAsync());
    }

    [Fact]
    public async Task Every_despatched_line_must_be_answered()
    {
        using var db = NewDatabase();

        // An omitted line silently treated as fully accepted is the value the operator never gave
        // becoming the value an invoice is raised against.
        var failure = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-partial",
                Command(4m) with { Lines = [] }, "driver"));
        Assert.Contains("every line", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_shipment_carries_one_proof_of_delivery_and_only_one()
    {
        using var db = NewDatabase();
        await ConfirmAsync(db, accepted: 4m);

        // Two guards stand between a consignment and a second signature, and the first one to fire
        // is the ladder: a DELIVERED shipment is terminal, so it is not confirmable. The unique
        // index behind the second guard is what makes this true under a race rather than in
        // sequence.
        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-second", Command(4m), "driver"));
        Assert.Contains("cannot be confirmed received", failure.Message);

        await using var verify = db.ContextFor(Tenant);
        Assert.Single(await verify.DeliveryProofs.ToListAsync());
        Assert.Single(await verify.DeliveryProofLines.ToListAsync());
    }

    [Fact]
    public async Task A_retried_confirmation_replays_rather_than_doubling_the_accepted_quantity()
    {
        using var db = NewDatabase();
        await Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-retry", Command(4m), "driver");
        var replay = await Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-retry", Command(4m), "driver");

        Assert.Equal(DeliveryStatuses.Delivered, replay.Outcome);
        var line = Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId));
        Assert.Equal(4m, line.AcceptedQuantity);
    }

    [Fact]
    public async Task A_retried_confirmation_cannot_change_the_command_behind_the_key()
    {
        using var db = NewDatabase();
        await Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-hash", Command(4m), "driver");

        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(() =>
            Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-hash",
                Command(4m) with { ReceivedByName = "A different receiver" }, "driver"));

        Assert.Contains("different delivery confirmation command", failure.Message);
        await using var verify = db.ContextFor(Tenant);
        Assert.Single(await verify.DeliveryProofs.ToListAsync());
        Assert.Single(await verify.DeliveryProofLines.ToListAsync());
    }

    [Fact]
    public async Task Full_cumulative_acceptance_moves_the_customer_order_to_delivered()
    {
        using var db = NewDatabase();
        await ConfirmAsync(db, accepted: 4m);

        await using var verify = db.ContextFor(Tenant);
        Assert.Equal(DeliveredStatusId, await verify.Orders.Where(x => x.Id == OrderId)
            .Select(x => x.StatusId).SingleAsync());
    }

    [Fact]
    public async Task Order_moves_to_delivered_only_after_acceptance_across_active_shipments_covers_every_line()
    {
        const long secondShipmentId = ShipmentId + 100;
        const long secondShipmentItemId = ShipmentItemId + 100;
        using var db = NewDatabase();
        await using (var arrange = db.ContextFor(Tenant))
        {
            (await arrange.ShipmentItems.SingleAsync(x => x.Id == ShipmentItemId)).Quantity = 2m;
            arrange.Shipments.Add(new Shipment
            {
                Id = secondShipmentId, ShipmentNo = $"DN-{secondShipmentId}", OrderId = OrderId,
                BusinessUnitId = Tenant, StatusId = Tenant + 500, ShipmentDate = Now,
                DeliveryStatus = DeliveryStatuses.Dispatched, DeliveryStatusChangedBy = "qa",
                DeliveryStatusChangedOn = Now, CreatedBy = "qa", CreatedOn = Now, IsActive = true
            });
            arrange.ShipmentItems.Add(new ShipmentItem
            {
                Id = secondShipmentItemId, ShipmentId = secondShipmentId, OrderItemId = OrderItemId,
                Quantity = 2m, CreatedBy = "qa", CreatedOn = Now, IsActive = true
            });
            await arrange.SaveChangesAsync();
        }

        await Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-part-one",
            Command(2m), "driver");
        await using (var afterFirst = db.ContextFor(Tenant))
            Assert.Equal(Tenant, await afterFirst.Orders.Where(x => x.Id == OrderId)
                .Select(x => x.StatusId).SingleAsync());

        var secondCommand = Command(2m) with
        {
            Lines = [new ConfirmDeliveryLineCommand(secondShipmentItemId, 2m, null, null, null)]
        };
        await Confirmations(db).ConfirmAsync(Tenant, secondShipmentId, "pod-part-two",
            secondCommand, "driver");

        await using var verify = db.ContextFor(Tenant);
        Assert.Equal(DeliveredStatusId, await verify.Orders.Where(x => x.Id == OrderId)
            .Select(x => x.StatusId).SingleAsync());
        Assert.Equal(4m, (await Ledger(db).ForOrderAsync(Tenant, OrderId)).Single().AcceptedQuantity);
    }

    [Fact]
    public async Task Missing_delivered_status_rolls_back_the_entire_confirmation()
    {
        using var db = NewDatabase();
        await using (var setup = db.ContextFor(Tenant))
        {
            setup.SetupMasters.Remove(await setup.SetupMasters.SingleAsync(x => x.SetupId == DeliveredStatusId));
            await setup.SaveChangesAsync();
        }

        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => ConfirmAsync(db, accepted: 4m));
        Assert.Contains("OrderStatus/DELIVERED", failure.Message);

        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.DeliveryProofs.ToListAsync());
        Assert.Empty(await verify.DeliveryProofLines.ToListAsync());
        Assert.Equal(DeliveryStatuses.Dispatched, await verify.Shipments.Where(x => x.Id == ShipmentId)
            .Select(x => x.DeliveryStatus).SingleAsync());
        Assert.Equal(Tenant, await verify.Orders.Where(x => x.Id == OrderId)
            .Select(x => x.StatusId).SingleAsync());
    }

    [Fact]
    public async Task Evidence_filed_against_another_shipment_cannot_be_stapled_to_this_proof()
    {
        using var db = NewDatabase();
        long foreignAttachmentId;
        await using (var seed = db.ContextFor(null))
        {
            var attachment = new Attachment
            {
                ParentType = DeliveryProofEvidenceService.EvidenceParentType,
                ParentId = 999999, // some other shipment
                FileName = "signature.png",
                FilePath = "evidence/other",
                ContentSha256 = new string('a', 64),
                CreatedOn = Now,
            };
            seed.Attachments.Add(attachment);
            await seed.SaveChangesAsync();
            foreignAttachmentId = attachment.Id;
        }

        var failure = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-foreign",
                Command(4m) with { SignatureEvidenceId = foreignAttachmentId }, "driver"));
        Assert.Contains("not captured against this shipment", failure.Message);
    }

    [Fact]
    public async Task Half_a_coordinate_is_refused_and_a_missing_fix_is_not()
    {
        using var db = NewDatabase();

        var failure = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-halfgps",
                Command(4m) with { GpsLatitude = 24.7136m }, "driver"));
        Assert.Contains("Half a coordinate", failure.Message);

        // No fix at all is fine and is recorded AS no fix, not as 0,0 — which the read model would
        // otherwise be unable to tell from a fix at the equator.
        var view = await Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-nogps",
            Command(4m), "driver");
        Assert.False(view.HasGpsFix);
        Assert.Null(view.GpsLatitude);
    }

    // ============================================ FR-DLM-07: the commercial fact and its consequence

    [Fact]
    public async Task The_outcome_is_derived_from_the_quantities_and_not_asserted()
    {
        using var db = NewDatabase();
        var view = await ConfirmAsync(db, accepted: 1m, reason: DeliveryExceptionReasons.ShortShipment);

        // Nobody selected DELIVERY_EXCEPTION. The numbers chose it.
        Assert.Equal(DeliveryStatuses.DeliveryException, view.Outcome);
        Assert.Equal(3m, Assert.Single(view.Lines).RefusedQuantity);

        // And the accepted unit still counts. Treating the whole consignment as undelivered because
        // three of four cartons were short would leave one invoiceable unit invisible.
        var ledgerLine = Assert.Single(await Ledger(db).ForOrderAsync(Tenant, OrderId));
        Assert.Equal(1m, ledgerLine.AcceptedQuantity);
    }

    [Fact]
    public async Task A_short_line_must_say_why_and_a_full_line_may_not()
    {
        using var db = NewDatabase();

        var noReason = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-noreason",
                Command(2m), "driver"));
        Assert.Contains("must state why", noReason.Message);

        var inventedReason = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-badreason",
                Command(4m, DeliveryExceptionReasons.Damaged), "driver"));
        Assert.Contains("cannot carry an exception reason", inventedReason.Message);

        var unknownReason = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-unknown",
                Command(2m, "PALLET_FELL_OFF"), "driver"));
        Assert.Contains("not a recognised delivery exception reason", unknownReason.Message);
    }

    [Fact]
    public async Task A_customer_cannot_accept_more_than_arrived()
    {
        using var db = NewDatabase();

        var failure = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, "pod-over",
                Command(9m), "driver"));
        Assert.Contains("cannot accept more than arrived", failure.Message);
    }

    [Fact]
    public async Task A_shortfall_is_decided_once_and_the_decision_cannot_be_revised()
    {
        using var db = NewDatabase();
        var view = await ConfirmAsync(db, accepted: 2m, reason: DeliveryExceptionReasons.Rejected);
        var shortfall = Assert.Single(view.Lines);

        var decided = await Confirmations(db).RecordShortfallDecisionAsync(
            Tenant, shortfall.Id,
            new RecordShortfallDecisionCommand("credit", "Customer will not take a replacement."),
            "sales-manager");
        Assert.Equal(DeliveryShortfallDecisions.Credit, decided.CommercialDecision);
        Assert.Equal("sales-manager", decided.CommercialDecisionBy);

        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => Confirmations(db).RecordShortfallDecisionAsync(
                Tenant, shortfall.Id,
                new RecordShortfallDecisionCommand("RESUPPLY", "Changed our mind."), "sales-manager"));
        Assert.Contains("append-only", failure.Message);
    }

    [Fact]
    public async Task A_shortfall_decision_needs_a_reason_and_a_recognised_answer()
    {
        using var db = NewDatabase();
        var view = await ConfirmAsync(db, accepted: 2m, reason: DeliveryExceptionReasons.Damaged);
        var shortfall = Assert.Single(view.Lines);

        var noReason = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).RecordShortfallDecisionAsync(
                Tenant, shortfall.Id, new RecordShortfallDecisionCommand("CREDIT", "  "), "manager"));
        Assert.Contains("reason", noReason.Message, StringComparison.OrdinalIgnoreCase);

        // Deliberately NOT a workflow state. There are two answers, and "investigating" is not one:
        // any claim against the carrier is run outside this system.
        var invented = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db).RecordShortfallDecisionAsync(
                Tenant, shortfall.Id,
                new RecordShortfallDecisionCommand("INVESTIGATING", "With the carrier."), "manager"));
        Assert.Contains("RESUPPLY or CREDIT", invented.Message);
    }

    [Fact]
    public async Task A_line_accepted_in_full_has_no_shortfall_to_decide()
    {
        using var db = NewDatabase();
        var view = await ConfirmAsync(db, accepted: 4m);

        var failure = await Assert.ThrowsAsync<DeliveryConflictException>(
            () => Confirmations(db).RecordShortfallDecisionAsync(
                Tenant, Assert.Single(view.Lines).Id,
                new RecordShortfallDecisionCommand("CREDIT", "Nothing wrong."), "manager"));
        Assert.Contains("no shortfall", failure.Message);
    }

    // ================================================================ FR-DLM-01: the delivery note

    [Fact]
    public async Task The_delivery_note_states_the_governed_region_rather_than_parsing_the_address()
    {
        using var db = NewDatabase();
        var note = await Notes(db).GetAsync(Tenant, ShipmentId);
        Assert.NotNull(note);

        Assert.Equal("Riyadh", note!.DeliveryCityName);
        Assert.Equal("Riyadh Region", note.DeliveryRegionName);
        Assert.Equal("Saudi Arabia", note.DeliveryCountryName);
        // The address free text says something else entirely; the note reports the MAPPING.
        Assert.Contains("Industrial", note.ShippingAddress!);
        Assert.DoesNotContain(note.Gaps, g => g.Contains("not mapped to a region"));
    }

    [Fact]
    public async Task An_unmapped_delivery_address_is_reported_as_a_gap_rather_than_guessed()
    {
        using var db = NewDatabase();
        await using (var edit = db.ContextFor(Tenant))
        {
            var shipment = await edit.Shipments.SingleAsync(s => s.Id == ShipmentId);
            shipment.DeliveryCityId = null;
            await edit.SaveChangesAsync();
        }

        var note = await Notes(db).GetAsync(Tenant, ShipmentId);
        Assert.Null(note!.DeliveryRegionName);
        Assert.Contains(note.Gaps, g => g.Contains("not mapped to a region"));
    }

    [Fact]
    public async Task The_delivery_note_names_Arabic_as_a_deferred_gap_rather_than_half_rendering_it()
    {
        using var db = NewDatabase();
        var note = await Notes(db).GetAsync(Tenant, ShipmentId);

        // Decision R6 defers Arabic, RTL and Hijri. The BRD asks FR-DLM-01 for a bilingual note, so
        // the deviation is stated ON THE DOCUMENT rather than only in a register nobody prints.
        Assert.Contains(DeliveryNoteReadService.ArabicGap, note!.Gaps);
    }

    [Fact]
    public async Task The_delivery_note_remaining_column_comes_from_the_delivered_ledger()
    {
        using var db = NewDatabase();

        // Nothing accepted yet: four awarded, four on this note, nothing left after it.
        var beforeConfirmation = Assert.Single((await Notes(db).GetAsync(Tenant, ShipmentId))!.Lines);
        Assert.Equal(4m, beforeConfirmation.OrderedQuantity);
        Assert.Equal(4m, beforeConfirmation.DespatchedQuantity);
        Assert.Equal(0m, beforeConfirmation.PreviouslyAcceptedQuantity);
        Assert.Equal(0m, beforeConfirmation.RemainingQuantity);

        // The remaining figure is server-computed against the same ledger the invoice ceiling reads.
        // If this page went back to subtracting in the browser, the signed paper and the quantity
        // finance may bill could disagree.
        await ConfirmAsync(db, accepted: 1m, reason: DeliveryExceptionReasons.Damaged);
        var afterConfirmation = Assert.Single((await Notes(db).GetAsync(Tenant, ShipmentId))!.Lines);
        Assert.Equal(1m, afterConfirmation.PreviouslyAcceptedQuantity);
    }

    [Fact]
    public async Task The_delivery_note_never_invents_an_issuer_and_says_so_instead()
    {
        using var db = NewDatabase();
        var note = await Notes(db).GetAsync(Tenant, ShipmentId);

        Assert.Equal("Business Unit 700", note!.Issuer.LegalName);
        Assert.Null(note.Issuer.TaxRegistrationNumber);
        Assert.Null(note.Issuer.AddressLine);
        Assert.Contains(note.Gaps, g => g.Contains("VAT registration number"));
        Assert.Contains(note.Gaps, g => g.Contains("does not identify its sender"));
    }

    [Fact]
    public async Task The_delivery_note_shows_the_proof_once_one_exists()
    {
        using var db = NewDatabase();
        Assert.Null((await Notes(db).GetAsync(Tenant, ShipmentId))!.Proof);

        await ConfirmAsync(db, accepted: 4m);
        var proof = (await Notes(db).GetAsync(Tenant, ShipmentId))!.Proof;
        Assert.NotNull(proof);
        Assert.Equal("Faisal Al-Harbi", proof!.ReceivedByName);
    }

    // ============================================================================ tenant isolation

    [Fact]
    public async Task Another_tenant_cannot_confirm_or_read_this_delivery()
    {
        using var db = NewDatabase();

        var failure = await Assert.ThrowsAsync<DeliveryValidationException>(
            () => Confirmations(db, OtherTenant).ConfirmAsync(OtherTenant, ShipmentId, "pod-cross",
                Command(4m), "intruder"));
        Assert.Contains("not found", failure.Message);

        await ConfirmAsync(db, accepted: 4m);
        Assert.Null(await Confirmations(db, OtherTenant).GetAsync(OtherTenant, ShipmentId));
        Assert.Null(await Notes(db, OtherTenant).GetAsync(OtherTenant, ShipmentId));
    }

    // ==================================================================================== helpers

    private static IDeliveryConfirmationService Confirmations(TestDb db, long tenant = Tenant)
        => new DeliveryConfirmationService(db.ContextFor(tenant),
            NullLogger<DeliveryConfirmationService>.Instance);

    private static IDeliveredQuantityLedger Ledger(TestDb db, long tenant = Tenant)
        => new DeliveredQuantityLedger(db.ContextFor(tenant));

    private static IDeliveryNoteReadService Notes(TestDb db, long tenant = Tenant)
    {
        var context = db.ContextFor(tenant);
        return new DeliveryNoteReadService(context, new DeliveredQuantityLedger(context),
            new DeliveryConfirmationService(context, NullLogger<DeliveryConfirmationService>.Instance));
    }

    private static ConfirmDeliveryCommand Command(decimal accepted, string? reason = null)
        => new(
            "Faisal Al-Harbi", "+966 55 000 0000", "Store Keeper", Now,
            null, null, null, null, null, null, null, null,
            [new ConfirmDeliveryLineCommand(ShipmentItemId, accepted, reason, reason is null ? null : "Two cartons crushed.", null)]);

    private static Task<DeliveryProofView> ConfirmAsync(
        TestDb db, decimal accepted, string? reason = null)
        => Confirmations(db).ConfirmAsync(Tenant, ShipmentId, $"pod-{accepted}-{reason}",
            Command(accepted, reason), "driver");

    /// <summary>
    /// The only legal route to CANCELLED: back the shipment out to SCHEDULED — the state in which
    /// nothing has left — and cancel from there.
    /// </summary>
    private static async Task CancelBeforeDespatchAsync(TestDb db)
    {
        await SetStatusAsync(db, DeliveryStatuses.Scheduled);
        await Confirmations(db).TransitionAsync(Tenant, ShipmentId, DeliveryStatuses.Cancelled, "clerk");
    }

    private static async Task SetStatusAsync(TestDb db, string status)
    {
        await using var context = db.ContextFor(Tenant);
        var shipment = await context.Shipments.SingleAsync(s => s.Id == ShipmentId);
        shipment.DeliveryStatus = status;
        await context.SaveChangesAsync();
    }

    private static TestDb NewDatabase()
    {
        var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, Tenant);
        SeedTenant(seed, OtherTenant);
        seed.SaveChanges();
        return db;
    }

    private static void SeedTenant(ErpRfqAutomationContext context, long tenant)
    {
        var isPrimary = tenant == Tenant;
        Seed.EnsureBusinessUnit(context, tenant);
        Seed.Customer(context, tenant, tenant, $"Customer {tenant}");
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = tenant, SetupType = "OrderStatus", SetupCode = "CONFIRMED", SetupValue = "CONFIRMED",
            BusinessUnitId = tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = tenant + 2600, SetupType = "OrderStatus", SetupCode = "DELIVERED", SetupValue = "DELIVERED",
            BusinessUnitId = tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = tenant + 500, SetupType = "ShipmentStatus", SetupCode = "OPEN", SetupValue = "Open",
            BusinessUnitId = tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.Warehouses.Add(new Warehouse
        {
            Id = tenant, BusinessUnitId = tenant, WarehouseCode = $"WH{tenant}",
            WarehouseName = $"Warehouse {tenant}", IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.Products.Add(new Product
        {
            Id = tenant, Buid = tenant, PartNo = $"PART{tenant}", ProductName = "Gate valve DN50",
            WarehouseId = tenant, QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true,
            CreatedBy = "qa", CreatedOn = Now
        });

        // The governed region master — the same SetCountry/SetState/SetCity shell CommercialRouting
        // already reads for sales territory. Tenant-populated: nothing here is seeded in production.
        context.SetCountries.Add(new SetCountry
        {
            CountryId = (int)tenant, CountryName = "Saudi Arabia", CountryCode = "SA", Buid = tenant,
            IsActive = true, CreatedBy = "qa", CreatedDate = Now
        });
        context.SetStates.Add(new SetState
        {
            StateId = (int)tenant, StateCode = "01", StateName = "Riyadh Region",
            CountryId = (int)tenant, Buid = tenant, IsActive = true, CreatedBy = "qa", CreatedDate = Now
        });
        context.SetCities.Add(new SetCity
        {
            CityId = (int)tenant, CityName = "Riyadh", StateId = (int)tenant, CountryId = (int)tenant,
            Buid = tenant, IsActive = true, CreatedBy = "qa", CreatedDate = Now
        });

        var orderId = isPrimary ? OrderId : OrderId + 1;
        var orderItemId = isPrimary ? OrderItemId : OrderItemId + 1;
        var shipmentId = isPrimary ? ShipmentId : ShipmentId + 1;
        var shipmentItemId = isPrimary ? ShipmentItemId : ShipmentItemId + 1;

        // An invoice must state the currency it is payable in (the gate that stopped
        // INV-2026-000001 being un-payable), so the order it is raised from carries one. A
        // per-order id keeps the primary and secondary orders from colliding on it.
        var currencyId = orderId + 900_000;
        context.Currencies.Add(new Currency
        {
            Id = currencyId, BusinessUnitId = tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            CreatedBy = "qa", CreatedOn = Now
        });
        context.Set<Order>().Add(new Order
        {
            Id = orderId, OrderNo = $"SO-{orderId}", CustomerId = tenant, BusinessUnitId = tenant,
            CurrencyId = currencyId,
            StatusId = tenant, TotalAmount = 400m, OrderDate = Now, CreatedBy = "qa", CreatedOn = Now,
            IsActive = true
        });
        context.Set<OrderItem>().Add(new OrderItem
        {
            Id = orderItemId, OrderId = orderId, ProductId = tenant, WarehouseId = tenant,
            Quantity = 4m, UnitPrice = 100m, Discount = 0m, TaxAmount = 0m, TotalAmount = 400m,
            CreatedBy = "qa", CreatedDate = Now, IsActive = true
        });
        context.Set<Shipment>().Add(new Shipment
        {
            Id = shipmentId, ShipmentNo = $"DN-{shipmentId}", OrderId = orderId, BusinessUnitId = tenant,
            StatusId = tenant + 500, ShipmentDate = Now,
            ShippingAddress = "Plot 44, Second Industrial City",
            DeliveryCityId = (int)tenant,
            DeliveryStatus = DeliveryStatuses.Dispatched,
            DeliveryStatusChangedBy = "qa", DeliveryStatusChangedOn = Now,
            CreatedBy = "qa", CreatedOn = Now, IsActive = true
        });
        context.Set<ShipmentItem>().Add(new ShipmentItem
        {
            Id = shipmentItemId, ShipmentId = shipmentId, OrderItemId = orderItemId, Quantity = 4m,
            CreatedBy = "qa", CreatedOn = Now, IsActive = true
        });
    }
}
