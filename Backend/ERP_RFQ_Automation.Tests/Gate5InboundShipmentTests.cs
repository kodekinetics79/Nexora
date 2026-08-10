using ERP_RFQ_Automation.InboundLogistics;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 5 / Module 6 — FR-MAS-01..05. Inbound supplier shipments keyed to the supplier purchase
/// order (decision R3).
///
/// <para>Entering state: no inbound shipment concept existed. <c>Models.Shipment</c> is an OUTBOUND
/// customer despatch keyed to the sales order, shipment status was ungoverned free text from an
/// unseeded picklist (E20), no named Saudi port master existed (E21) and the customs/putaway lead
/// times behind a material available date had no source of truth at all (E22).</para>
///
/// <para>These tests pin the four things that make the difference between a schema and a feature:
/// the milestone ladder refuses an illegal transition, the quantity invariant holds where it is
/// written, the derived date is never invented, and the derivation propagates to the inbound supply
/// row that availability is actually read from.</para>
/// </summary>
public sealed class Gate5InboundShipmentTests
{
    // ==========================================================================================
    // The milestone ladder (FR-MAS-01)
    // ==========================================================================================

    [Fact]
    public void The_ladder_only_moves_forward_and_never_leaves_a_terminal_state()
    {
        Assert.True(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.ReadyAtFactory, InboundShipmentMilestones.DepartedOrigin));
        // A skip is legitimate: a Riyadh-to-Riyadh shipment never touches a port or customs, and
        // demanding every rung would force operators to record milestones that did not happen.
        Assert.True(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.ReadyAtFactory, InboundShipmentMilestones.ReceivedAtWarehouse));

        Assert.False(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.ArrivedSaudiPort, InboundShipmentMilestones.DepartedOrigin));
        Assert.False(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.InTransit, InboundShipmentMilestones.InTransit));
        Assert.False(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.ReceivedAtWarehouse, InboundShipmentMilestones.Cancelled));
        Assert.False(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.Cancelled, InboundShipmentMilestones.InTransit));

        // Cancellation is reachable from anywhere still in flight, because a shipment can be
        // abandoned at any point before it lands.
        Assert.True(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.CustomsClearance, InboundShipmentMilestones.Cancelled));
        Assert.False(InboundShipmentMilestones.CanTransition(
            InboundShipmentMilestones.InTransit, "TELEPORTED"));
    }

    [Fact]
    public async Task A_backwards_milestone_is_refused_and_changes_nothing()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("back", quantity: 4m);
        shipment = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "back-depart");
        shipment = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.InTransit, "back-transit");

        var exception = await Assert.ThrowsAsync<InboundLogisticsConflictException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "back-again"));
        Assert.Contains("forward", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.Context();
        var row = await verify.SupplierShipments.SingleAsync(x => x.Id == shipment.Id);
        Assert.Equal(InboundShipmentMilestones.InTransit, row.Milestone);
        // The refused transition must leave no trace in the append-only log either.
        Assert.Equal(2, await verify.ProcurementEvents.CountAsync(x =>
            x.AggregateType == InboundShipmentApplicationService.AggregateType
            && x.EventType == InboundShipmentApplicationService.MilestoneRecordedEvent));
    }

    [Fact]
    public async Task Every_transition_is_attributable_and_append_only()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("audit", quantity: 3m);
        await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "audit-depart");

        await using var verify = fixture.Context();
        var events = await verify.ProcurementEvents
            .Where(x => x.AggregateType == InboundShipmentApplicationService.AggregateType)
            .OrderBy(x => x.Id).ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(InboundShipmentApplicationService.ShipmentCreatedEvent, events[0].EventType);
        Assert.Equal(InboundShipmentApplicationService.MilestoneRecordedEvent, events[1].EventType);
        foreach (var recorded in events)
        {
            Assert.Equal("qa", recorded.Actor);
            Assert.False(string.IsNullOrWhiteSpace(recorded.CorrelationId));
            Assert.False(string.IsNullOrWhiteSpace(recorded.IdempotencyKey));
            Assert.Equal(shipment.Id, recorded.AggregateId);
        }
    }

    [Fact]
    public async Task An_arrival_must_name_the_entry_location()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("port", quantity: 2m);
        shipment = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "port-depart");

        var exception = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.ArrivedSaudiPort, "port-arrive"));
        Assert.Contains("Saudi port", exception.Message, StringComparison.OrdinalIgnoreCase);

        var port = await fixture.EnsurePortAsync();
        var arrived = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.ArrivedSaudiPort,
            "port-arrive-2", portOfEntryId: port.Id);
        Assert.Equal(port.Id, arrived.PortOfEntryId);
        Assert.Equal("Jeddah Islamic Port", arrived.PortOfEntryName);
    }

    [Fact]
    public async Task A_milestone_cannot_predate_the_previous_one_or_be_dated_in_the_future()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("dates", quantity: 2m);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var future = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "dates-future",
                occurredOn: today.AddDays(1)));
        Assert.Contains("future", future.Message, StringComparison.OrdinalIgnoreCase);

        var backdated = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "dates-back",
                occurredOn: today.AddDays(-10)));
        Assert.Contains("before the last recorded milestone", backdated.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================================
    // Partial shipments and the reconciliation invariant (FR-MAS-05)
    // ==========================================================================================

    [Fact]
    public async Task One_purchase_order_line_splits_across_several_shipments()
    {
        using var fixture = new InboundScenario();
        var first = await fixture.CreateShipmentAsync("split-1", quantity: 3m);
        var second = await fixture.CreateShipmentAsync("split-2", quantity: 5m);

        Assert.NotEqual(first.ShipmentNumber, second.ShipmentNumber);
        Assert.EndsWith("-S1", first.ShipmentNumber, StringComparison.Ordinal);
        Assert.EndsWith("-S2", second.ShipmentNumber, StringComparison.Ordinal);

        await using var verify = fixture.Context();
        var line = await verify.SupplierPurchaseOrderLines.SingleAsync();
        Assert.Equal(8m, line.ShippedQuantity);
        Assert.Equal(8m, line.OrderedQuantity);
    }

    [Fact]
    public async Task The_sum_shipped_can_never_exceed_the_quantity_ordered()
    {
        using var fixture = new InboundScenario();
        await fixture.CreateShipmentAsync("over-1", quantity: 6m);

        var exception = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.CreateShipmentAsync("over-2", quantity: 3m));
        Assert.Contains("ordered", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.Context();
        // The refused shipment left no header, no line and no running-total drift.
        Assert.Equal(1, await verify.SupplierShipments.CountAsync());
        Assert.Equal(6m, await verify.SupplierPurchaseOrderLines.Select(x => x.ShippedQuantity).SingleAsync());
    }

    [Fact]
    public async Task Cancelling_a_shipment_gives_its_quantity_back_to_the_line()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("cancel", quantity: 8m);

        await using (var before = fixture.Context())
            Assert.Equal(8m, await before.SupplierPurchaseOrderLines.Select(x => x.ShippedQuantity).SingleAsync());

        var cancelled = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.Cancelled,
            "cancel-it", note: "Vessel booking released by the forwarder.");

        Assert.Equal(InboundShipmentMilestones.Cancelled, cancelled.Milestone);
        Assert.Equal("Vessel booking released by the forwarder.", cancelled.CancellationReason);
        Assert.Null(cancelled.MaterialAvailability.Date);

        await using var verify = fixture.Context();
        Assert.Equal(0m, await verify.SupplierPurchaseOrderLines.Select(x => x.ShippedQuantity).SingleAsync());

        // And the line is shippable again, which is the whole point of releasing it.
        var replacement = await fixture.CreateShipmentAsync("cancel-replacement", quantity: 8m);
        Assert.Equal(InboundShipmentMilestones.ReadyAtFactory, replacement.Milestone);
    }

    [Fact]
    public async Task A_cancellation_without_a_reason_is_refused()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("cancel-noreason", quantity: 2m);

        var exception = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.Cancelled, "cancel-noreason-go"));
        Assert.Contains("reason", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================================
    // Material Available Date (FR-MAS-03)
    // ==========================================================================================

    [Fact]
    public void An_unconfigured_policy_derives_no_date_and_says_why()
    {
        var shipment = new SupplierShipment
        {
            Milestone = InboundShipmentMilestones.ReadyAtFactory,
            EtaDate = new DateOnly(2026, 8, 5)
        };

        var result = MaterialAvailabilityCalculator.Derive(shipment, InboundLogisticsPolicy.DefaultFor(1));

        // The Gate 4 SLA backfill lesson, in a different column: a lead time silently read as zero
        // produces a plausible date equal to the ETA on EVERY shipment, and somebody promises a
        // customer against it.
        Assert.Null(result.Date);
        Assert.Contains("lead times", result.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_shipment_with_no_eta_derives_no_date()
    {
        var policy = Policy(customs: 3, putaway: 1);
        var result = MaterialAvailabilityCalculator.Derive(
            new SupplierShipment { Milestone = InboundShipmentMilestones.ReadyAtFactory }, policy);

        Assert.Null(result.Date);
        Assert.Contains("ETA", result.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_date_is_eta_plus_clearance_plus_putaway_counted_in_working_days()
    {
        var policy = Policy(customs: 3, putaway: 1);
        // Wednesday 5 August 2026.
        var shipment = new SupplierShipment
        {
            Milestone = InboundShipmentMilestones.InTransit,
            EtaDate = new DateOnly(2026, 8, 5)
        };

        var result = MaterialAvailabilityCalculator.Derive(shipment, policy);

        // Thu, (skip Fri+Sat), Sun, Mon = 3 working days -> Mon 10 Aug; +1 -> Tue 11 Aug.
        Assert.Equal(new DateOnly(2026, 8, 11), result.Date);
        // A calendar-day implementation would have said Sunday 9 August, promising the customer two
        // days that customs and the warehouse were shut.
        Assert.NotEqual(new DateOnly(2026, 8, 9), result.Date);
        Assert.Equal(MaterialAvailableBasisKinds.Eta, result.BasisKind);
        Assert.Equal(new DateOnly(2026, 8, 5), result.BasisDate);
        Assert.Equal(3, result.CustomsClearanceDays);
        Assert.Equal(1, result.PutawayDays);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public void An_actual_arrival_replaces_the_estimate_and_clearance_stops_being_added_once_it_is_done()
    {
        var policy = Policy(customs: 5, putaway: 2);

        var arrived = MaterialAvailabilityCalculator.Derive(new SupplierShipment
        {
            Milestone = InboundShipmentMilestones.ArrivedSaudiPort,
            EtaDate = new DateOnly(2026, 8, 20),
            ArrivedSaudiPortOn = new DateOnly(2026, 8, 5)
        }, policy);

        // The ETA has been overtaken by events and is no longer evidence of anything.
        Assert.Equal(MaterialAvailableBasisKinds.ArrivedSaudiPort, arrived.BasisKind);
        Assert.Equal(new DateOnly(2026, 8, 5), arrived.BasisDate);
        Assert.Equal(5, arrived.CustomsClearanceDays);

        var cleared = MaterialAvailabilityCalculator.Derive(new SupplierShipment
        {
            Milestone = InboundShipmentMilestones.CustomsClearance,
            EtaDate = new DateOnly(2026, 8, 20),
            ArrivedSaudiPortOn = new DateOnly(2026, 8, 5),
            CustomsClearanceOn = new DateOnly(2026, 8, 9)
        }, policy);

        // Clearance has HAPPENED. Continuing to add a five-day allowance would push the availability
        // date a week past the truth and lose a promise that could have been made.
        Assert.Equal(MaterialAvailableBasisKinds.CustomsCleared, cleared.BasisKind);
        Assert.Equal(0, cleared.CustomsClearanceDays);
        Assert.Equal(2, cleared.PutawayDays);
        Assert.Equal(BusinessCalendar.AddBusinessDays(new DateOnly(2026, 8, 9), 2), cleared.Date);
    }

    [Fact]
    public void A_cancelled_shipment_has_no_material_available_date()
    {
        var result = MaterialAvailabilityCalculator.Derive(new SupplierShipment
        {
            Milestone = InboundShipmentMilestones.Cancelled,
            EtaDate = new DateOnly(2026, 8, 5),
        }, Policy(customs: 1, putaway: 1));

        Assert.Null(result.Date);
        Assert.Contains("cancelled", result.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_derived_date_is_persisted_with_the_inputs_that_produced_it()
    {
        using var fixture = new InboundScenario();
        await fixture.SetPolicyAsync(customs: 4, putaway: 1);
        var eta = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        var shipment = await fixture.CreateShipmentAsync("inputs", quantity: 5m, eta: eta);

        Assert.Equal(MaterialAvailableBasisKinds.Eta, shipment.MaterialAvailability.BasisKind);
        Assert.Equal(eta, shipment.MaterialAvailability.BasisDate);
        Assert.Equal(4, shipment.MaterialAvailability.CustomsClearanceDays);
        Assert.Equal(1, shipment.MaterialAvailability.PutawayDays);
        Assert.NotNull(shipment.MaterialAvailability.ComputedOn);
        Assert.Equal(
            BusinessCalendar.AddBusinessDays(BusinessCalendar.AddBusinessDays(eta, 4), 1),
            shipment.MaterialAvailability.Date);
    }

    [Fact]
    public async Task Withdrawing_the_eta_withdraws_the_derived_date_rather_than_leaving_a_stale_one()
    {
        using var fixture = new InboundScenario();
        await fixture.SetPolicyAsync(customs: 2, putaway: 1);
        var shipment = await fixture.CreateShipmentAsync("eta-clear", quantity: 5m,
            eta: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10));
        Assert.NotNull(shipment.MaterialAvailability.Date);

        var cleared = await fixture.Execute(service => service.UpdateTrackingAsync(
            new UpdateInboundShipmentTrackingCommand(fixture.BusinessUnitId, shipment.Id, shipment.Version,
                "eta-clear-go", "qa", "corr-eta-clear", ClearEta: true)));

        Assert.Null(cleared.MaterialAvailability.Date);
        Assert.Contains("ETA", cleared.MaterialAvailability.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Changing_the_lead_times_re_derives_the_shipments_already_on_the_books()
    {
        using var fixture = new InboundScenario();
        var eta = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20);
        var shipment = await fixture.CreateShipmentAsync("policy-change", quantity: 5m, eta: eta);
        // No policy yet, so nothing is derived — which the screen shows as a reason rather than a
        // blank column.
        Assert.Null(shipment.MaterialAvailability.Date);

        await fixture.SetPolicyAsync(customs: 3, putaway: 2);

        await using var verify = fixture.Context();
        var row = await verify.SupplierShipments.SingleAsync(x => x.Id == shipment.Id);
        Assert.Equal(
            BusinessCalendar.AddBusinessDays(BusinessCalendar.AddBusinessDays(eta, 3), 2),
            row.MaterialAvailableDate);
    }

    // ==========================================================================================
    // Propagation onto the inbound supply row availability actually reads (FR-MAS-03)
    // ==========================================================================================

    [Fact]
    public async Task The_derived_date_is_pushed_onto_incoming_inventory_and_departure_marks_it_in_transit()
    {
        using var fixture = new InboundScenario();
        await fixture.SetPolicyAsync(customs: 2, putaway: 1);
        var eta = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(9);
        // The WHOLE open quantity is on this one shipment, so its date replaces the purchase
        // order's expectation outright.
        var shipment = await fixture.CreateShipmentAsync("project", quantity: 8m, eta: eta);

        await using (var before = fixture.Context())
        {
            var incoming = await before.IncomingInventory.SingleAsync();
            Assert.Equal(shipment.MaterialAvailability.Date, incoming.ExpectedOn);
            // Still at the factory: nothing has moved, so nothing is in transit.
            Assert.Equal(IncomingInventoryStatus.Ordered, incoming.Status);
        }

        await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "project-depart");

        await using var after = fixture.Context();
        Assert.Equal(IncomingInventoryStatus.InTransit,
            await after.IncomingInventory.Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Part_shipped_lines_keep_the_purchase_order_expectation_rather_than_promising_early()
    {
        using var fixture = new InboundScenario();
        await fixture.SetPolicyAsync(customs: 0, putaway: 0);
        // Half the line, arriving well before the purchase order's own expected date. Writing the
        // early date onto IncomingInventory would tell ATP the WHOLE line lands then, and a promise
        // made on that is a promise broken.
        var shipment = await fixture.CreateShipmentAsync("partial-project", quantity: 4m,
            eta: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));

        await using var verify = fixture.Context();
        var order = await verify.SupplierPurchaseOrders.SingleAsync();
        var incoming = await verify.IncomingInventory.SingleAsync();
        Assert.Equal(order.ExpectedOn, incoming.ExpectedOn);
        Assert.True(incoming.ExpectedOn > shipment.MaterialAvailability.Date);
    }

    // ==========================================================================================
    // Idempotency, concurrency and tenant isolation
    // ==========================================================================================

    [Fact]
    public async Task Replaying_a_create_returns_the_same_shipment_and_ships_the_quantity_once()
    {
        using var fixture = new InboundScenario();
        var command = fixture.Create("replay", quantity: 5m);

        var first = await fixture.Execute(service => service.CreateShipmentAsync(command));
        var second = await fixture.Execute(service => service.CreateShipmentAsync(command));

        Assert.Equal(first.Id, second.Id);
        await using var verify = fixture.Context();
        Assert.Equal(1, await verify.SupplierShipments.CountAsync());
        Assert.Equal(5m, await verify.SupplierPurchaseOrderLines.Select(x => x.ShippedQuantity).SingleAsync());
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_is_a_conflict()
    {
        using var fixture = new InboundScenario();
        await fixture.Execute(service => service.CreateShipmentAsync(fixture.Create("dup", quantity: 2m)));

        var exception = await Assert.ThrowsAsync<InboundLogisticsConflictException>(() =>
            fixture.Execute(service => service.CreateShipmentAsync(fixture.Create("dup", quantity: 3m))));
        Assert.Contains("idempotency", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stale_version_is_refused()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("stale", quantity: 2m);
        await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "stale-1");

        var exception = await Assert.ThrowsAsync<InboundLogisticsConflictException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.InTransit, "stale-2"));
        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_tenants_purchase_order_cannot_be_shipped()
    {
        using var fixture = new InboundScenario();
        var otherOrderId = await fixture.OtherTenantPurchaseOrderIdAsync();

        var exception = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.Execute(service => service.CreateShipmentAsync(new CreateInboundShipmentCommand(
                fixture.BusinessUnitId, otherOrderId,
                [new InboundShipmentLineCommand(1, 1m)], "cross-tenant", "qa", "corr-cross"))));
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.Context(fixture.OtherBusinessUnitId);
        Assert.Equal(0, await verify.SupplierShipments.CountAsync());
    }

    [Fact]
    public async Task A_shipment_cannot_be_raised_against_an_order_the_supplier_has_never_seen()
    {
        using var fixture = new InboundScenario();
        var draftLine = await fixture.CreateDraftOrderLineAsync("draft-order");

        var exception = await Assert.ThrowsAsync<InboundLogisticsConflictException>(() =>
            fixture.Execute(service => service.CreateShipmentAsync(new CreateInboundShipmentCommand(
                fixture.BusinessUnitId, draftLine.OrderId,
                [new InboundShipmentLineCommand(draftLine.LineId, 1m)],
                "draft-ship", "qa", "corr-draft"))));
        Assert.Contains("with the supplier", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================================
    // The Gate 4 / Gate 5 seam (FR-MAS-05)
    //
    // The decision: the goods receipt stays a deliberate human act, and the RECEIVED_AT_WAREHOUSE
    // milestone never fakes one. What crosses the boundary is the other direction — posting a
    // receipt settles the shipment that carried the material. These tests pin both halves: the
    // milestone must NOT move stock, and the receipt MUST reach the shipment.
    // ==========================================================================================

    [Fact]
    public async Task Recording_received_at_warehouse_books_nothing_into_stock_and_says_so()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("gate-seam-milestone", quantity: 8m);
        var arrived = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.ReceivedAtWarehouse,
            "gate-seam-milestone-arrive");

        // The milestone is a logistics fact: a truck reached the gate. It is not a receipt.
        Assert.Equal(InboundShipmentReceiptStates.NotReceipted, arrived.ReceiptState);
        Assert.Equal(0m, arrived.ReceiptedQuantity);
        Assert.Equal(8m, arrived.OutstandingReceiptQuantity);

        await using var verify = fixture.Context();
        Assert.Equal(0, await verify.GoodsReceipts.CountAsync());
        Assert.Equal(0, await verify.InventoryMovements.CountAsync());
        Assert.Equal(0, await verify.MaterialLots.CountAsync());
        Assert.Equal(0m, await verify.SupplierPurchaseOrderLines.Select(x => x.ReceivedQuantity).SingleAsync());
        // And the purchase order has NOT been told it was received, because it was not.
        var status = await verify.SupplierPurchaseOrders.Select(x => x.Status).SingleAsync();
        Assert.NotEqual(SupplierPurchaseOrderStatuses.Received, status);
        Assert.NotEqual(SupplierPurchaseOrderStatuses.PartiallyReceived, status);
    }

    [Fact]
    public async Task Posting_the_goods_receipt_settles_the_shipment_that_carried_the_material()
    {
        using var fixture = new InboundScenario();
        await fixture.SetPolicyAsync(customs: 2, putaway: 1);
        var shipment = await fixture.CreateShipmentAsync("gate-seam-receipt", quantity: 8m);
        Assert.Equal(InboundShipmentMilestones.ReadyAtFactory, shipment.Milestone);

        await fixture.PostReceiptAsync("gate-seam-receipt-gr", "GR-SEAM-1", 8m);

        await using var verify = fixture.Context();
        var line = await verify.SupplierShipmentLines.SingleAsync(x => x.SupplierShipmentId == shipment.Id);
        // The receipt reached the shipment. Remove the settlement call from PostGoodsReceiptAsync
        // and this is 0 — a shipment still reading as in flight after its stock has landed.
        Assert.Equal(8m, line.ReceivedQuantity);

        var row = await verify.SupplierShipments.SingleAsync(x => x.Id == shipment.Id);
        Assert.Equal(InboundShipmentMilestones.ReceivedAtWarehouse, row.Milestone);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), row.ReceivedAtWarehouseOn);
        // The derivation re-ran on the new fact: warehouse arrival, putaway only, no clearance.
        Assert.Equal(MaterialAvailableBasisKinds.ReceivedAtWarehouse, row.MaterialAvailableBasisKind);
        Assert.Equal(0, row.AppliedCustomsClearanceDays);

        // The receipt itself did what only a receipt can do — and lots follow automatically.
        Assert.Equal(SupplierPurchaseOrderStatuses.Received,
            await verify.SupplierPurchaseOrders.Select(x => x.Status).SingleAsync());
        Assert.Equal(8m, await verify.MaterialLots.Select(x => x.QuantityReceived).SingleAsync());

        // The crossing is on the ledger, attributable, and names the receipt it came from.
        var settled = await verify.ProcurementEvents.SingleAsync(x =>
            x.AggregateType == InboundShipmentApplicationService.AggregateType
            && x.EventType == InboundShipmentReceiptSettlement.ReceiptSettledEvent);
        Assert.Equal(shipment.Id, settled.AggregateId);
        Assert.Contains("GR-SEAM-1", settled.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_receipt_leaves_the_shipment_in_flight_with_the_shortfall_visible()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("gate-seam-part", quantity: 8m);
        await fixture.PostReceiptAsync("gate-seam-part-gr", "GR-SEAM-PART", 3m);

        var refreshed = await fixture.ShipmentAsync(shipment.Id);
        Assert.Equal(InboundShipmentReceiptStates.PartiallyReceipted, refreshed.ReceiptState);
        Assert.Equal(3m, refreshed.ReceiptedQuantity);
        Assert.Equal(5m, refreshed.OutstandingReceiptQuantity);
        // Three of eight units are on the shelf; the shipment has not arrived, and saying it had
        // would be the same lie in the opposite direction.
        Assert.Equal(InboundShipmentMilestones.ReadyAtFactory, refreshed.Milestone);
        Assert.Equal(3m, Assert.Single(refreshed.Lines).ReceivedQuantity);
    }

    [Fact]
    public async Task A_receipt_fills_the_oldest_open_shipment_first()
    {
        using var fixture = new InboundScenario();
        var first = await fixture.CreateShipmentAsync("gate-seam-fifo-1", quantity: 3m);
        var second = await fixture.CreateShipmentAsync("gate-seam-fifo-2", quantity: 5m);

        await fixture.PostReceiptAsync("gate-seam-fifo-gr", "GR-SEAM-FIFO", 4m);

        var older = await fixture.ShipmentAsync(first.Id);
        var newer = await fixture.ShipmentAsync(second.Id);

        Assert.Equal(InboundShipmentReceiptStates.Receipted, older.ReceiptState);
        Assert.Equal(InboundShipmentMilestones.ReceivedAtWarehouse, older.Milestone);
        Assert.Equal(InboundShipmentReceiptStates.PartiallyReceipted, newer.ReceiptState);
        Assert.Equal(1m, newer.ReceiptedQuantity);
        Assert.Equal(4m, newer.OutstandingReceiptQuantity);
    }

    [Fact]
    public async Task A_shipment_a_receipt_has_booked_in_cannot_be_cancelled()
    {
        using var fixture = new InboundScenario();
        var shipment = await fixture.CreateShipmentAsync("gate-seam-cancel", quantity: 8m);
        await fixture.PostReceiptAsync("gate-seam-cancel-gr", "GR-SEAM-CANCEL", 4m);

        var refreshed = await fixture.ShipmentAsync(shipment.Id);
        var exception = await Assert.ThrowsAsync<InboundLogisticsConflictException>(() =>
            fixture.MilestoneAsync(refreshed, InboundShipmentMilestones.Cancelled, "gate-seam-cancel-go",
                note: "Forwarder released the booking."));
        // Cancellation releases the shipped quantity back to the line. Doing that for units already
        // counted into stock would let the same material be shipped twice.
        Assert.Contains("goods receipt", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = fixture.Context();
        Assert.Equal(8m, await verify.SupplierPurchaseOrderLines.Select(x => x.ShippedQuantity).SingleAsync());
    }

    // ==========================================================================================
    // The entry-point master (FR-MAS-01 / E21)
    // ==========================================================================================

    [Fact]
    public async Task The_standard_saudi_locations_load_on_request_and_a_second_load_adds_nothing()
    {
        using var fixture = new InboundScenario();

        await using (var empty = fixture.Context())
            Assert.Equal(0, await empty.PortsOfEntry.CountAsync());

        var first = await fixture.Execute(service => service.ImportStandardSaudiPortsAsync(
            new ImportStandardSaudiPortsCommand(fixture.BusinessUnitId, "ports-1", "qa", "corr-ports-1")));
        Assert.Equal(SaudiPortCatalogue.Entries.Count, first.Count);
        Assert.Contains(first, x => x.Name == "Jeddah Islamic Port" && x.Kind == PortOfEntryKinds.Seaport);
        Assert.Contains(first, x => x.Name == "King Khalid International Airport" && x.Kind == PortOfEntryKinds.Airport);
        Assert.Contains(first, x => x.Name == "Riyadh Dry Port" && x.Kind == PortOfEntryKinds.DryPort);

        var second = await fixture.Execute(service => service.ImportStandardSaudiPortsAsync(
            new ImportStandardSaudiPortsCommand(fixture.BusinessUnitId, "ports-2", "qa", "corr-ports-2")));
        Assert.Equal(SaudiPortCatalogue.Entries.Count, second.Count);

        await using var verify = fixture.Context();
        Assert.Equal(SaudiPortCatalogue.Entries.Count, await verify.PortsOfEntry.CountAsync());
    }

    [Fact]
    public async Task A_location_code_is_unique_per_tenant_and_a_deactivated_location_cannot_be_used()
    {
        using var fixture = new InboundScenario();
        var port = await fixture.EnsurePortAsync();

        var duplicate = await Assert.ThrowsAsync<InboundLogisticsConflictException>(() =>
            fixture.Execute(service => service.CreatePortOfEntryAsync(new CreatePortOfEntryCommand(
                fixture.BusinessUnitId, "jeddah-islamic", "Anything", PortOfEntryKinds.Seaport,
                "port-dup", "qa", "corr-port-dup"))));
        Assert.Contains("already exists", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        await fixture.Execute(service => service.SetPortOfEntryActiveAsync(new SetPortOfEntryActiveCommand(
            fixture.BusinessUnitId, port.Id, false, port.Version, "port-off", "qa", "corr-port-off")));

        var shipment = await fixture.CreateShipmentAsync("inactive-port", quantity: 2m);
        shipment = await fixture.MilestoneAsync(shipment, InboundShipmentMilestones.DepartedOrigin, "inactive-depart");
        var refused = await Assert.ThrowsAsync<InboundLogisticsValidationException>(() =>
            fixture.MilestoneAsync(shipment, InboundShipmentMilestones.ArrivedSaudiPort, "inactive-arrive",
                portOfEntryId: port.Id));
        Assert.Contains("no longer an active", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================================
    // Fixture
    // ==========================================================================================

    private static InboundLogisticsPolicy Policy(int customs, int putaway) => new()
    {
        BusinessUnitId = 1, CustomsClearanceLeadDays = customs, PutawayLeadDays = putaway
    };
}

/// <summary>
/// Builds a real issued supplier purchase order (through the procurement service, gates and all)
/// and then exercises the inbound shipment service against it.
/// </summary>
internal sealed class InboundScenario : IDisposable
{
    private readonly ProcurementScenario _procurement = new();
    private long? _purchaseOrderId;
    private long? _purchaseOrderLineId;

    public long BusinessUnitId => _procurement.BusinessUnitId;
    public long OtherBusinessUnitId => _procurement.OtherBusinessUnitId;

    public ErpRfqAutomationContext Context(long? tenant = null) => _procurement.Context(tenant);

    public async Task<T> Execute<T>(Func<InboundShipmentApplicationService, Task<T>> operation)
    {
        await using var context = Context();
        return await operation(new InboundShipmentApplicationService(context));
    }

    /// <summary>The seeded order carries a single line for 8 units, issued and with the supplier.</summary>
    public async Task<(long OrderId, long LineId)> EnsurePurchaseOrderAsync()
    {
        if (_purchaseOrderId is { } orderId && _purchaseOrderLineId is { } lineId) return (orderId, lineId);
        var order = await _procurement.CreatePurchaseOrderAsync("inbound", quantity: 8m);
        _purchaseOrderId = order.Id;
        _purchaseOrderLineId = await _procurement.PurchaseOrderLineIdAsync(order.Id);
        return (_purchaseOrderId.Value, _purchaseOrderLineId.Value);
    }

    /// <summary>A DRAFT order, to prove a shipment cannot be raised before the supplier has it.</summary>
    public async Task<(long OrderId, long LineId)> CreateDraftOrderLineAsync(string key)
    {
        // The seeded RFQ line is for 10 and the issued order above covers 8, so a 2-unit award is
        // still within the net sourcing requirement and gives this draft its own line.
        var award = await _procurement.CreateAwardAsync(key, quantity: 2m);
        var draft = await _procurement.Execute(service => service.CreatePurchaseOrderAsync(
            _procurement.PurchaseOrder([award.Id], $"{key}-po")));
        await using var verify = Context();
        var line = await verify.SupplierPurchaseOrderLines
            .Where(x => x.SupplierPurchaseOrderId == draft.Id).Select(x => x.Id).SingleAsync();
        return (draft.Id, line);
    }

    public async Task<long> OtherTenantPurchaseOrderIdAsync()
    {
        // No purchase order exists in the other tenant; an id from this tenant's sequence is enough
        // to prove the read is tenant-predicated rather than id-predicated.
        var (orderId, _) = await EnsurePurchaseOrderAsync();
        return orderId + 500_000;
    }

    public CreateInboundShipmentCommand Create(string key, decimal quantity, DateOnly? eta = null)
    {
        var (orderId, lineId) = EnsurePurchaseOrderAsync().GetAwaiter().GetResult();
        return new CreateInboundShipmentCommand(BusinessUnitId, orderId,
            [new InboundShipmentLineCommand(lineId, quantity)], key, "qa", $"corr-{key}",
            CarrierName: "QA Forwarder", TrackingReference: $"BL-{key}", EtaDate: eta);
    }

    public Task<InboundShipmentView> CreateShipmentAsync(string key, decimal quantity, DateOnly? eta = null)
        => Execute(service => service.CreateShipmentAsync(Create(key, quantity, eta)));

    public Task<InboundShipmentView> MilestoneAsync(
        InboundShipmentView shipment, string milestone, string key,
        DateOnly? occurredOn = null, long? portOfEntryId = null, string? note = null)
        => Execute(service => service.RecordMilestoneAsync(new RecordInboundShipmentMilestoneCommand(
            BusinessUnitId, shipment.Id, shipment.Version, milestone,
            occurredOn ?? DateOnly.FromDateTime(DateTime.UtcNow), key, "qa", $"corr-{key}",
            portOfEntryId, null, note)));

    public Task<InboundLogisticsPolicyView> SetPolicyAsync(int customs, int putaway)
        => Execute(service => service.UpdatePolicyAsync(new UpdateInboundLogisticsPolicyCommand(
            BusinessUnitId, $"policy-{customs}-{putaway}", "qa", "corr-policy", customs, putaway)));

    /// <summary>Reads one shipment back through the service's own read model.</summary>
    public async Task<InboundShipmentView> ShipmentAsync(long shipmentId)
    {
        var (orderId, _) = await EnsurePurchaseOrderAsync();
        var shipments = await Execute(service =>
            service.GetPurchaseOrderShipmentsAsync(BusinessUnitId, orderId));
        return shipments.Single(x => x.Id == shipmentId);
    }

    /// <summary>
    /// Posts a real goods receipt through <c>ProcurementApplicationService</c> — the same path the
    /// controller uses — so the Gate 4/Gate 5 seam is exercised end to end rather than simulated.
    /// </summary>
    public async Task PostReceiptAsync(string key, string receiptNumber, decimal quantity)
    {
        var (orderId, lineId) = await EnsurePurchaseOrderAsync();
        long version;
        await using (var read = Context())
            version = await read.SupplierPurchaseOrders.Where(x => x.Id == orderId)
                .Select(x => x.Version).SingleAsync();
        await _procurement.Execute(service => service.PostGoodsReceiptAsync(
            _procurement.Receipt(orderId, lineId, quantity, version, key, receiptNumber)));
    }

    public async Task<PortOfEntryView> EnsurePortAsync()
        => await Execute(service => service.CreatePortOfEntryAsync(new CreatePortOfEntryCommand(
            BusinessUnitId, "JEDDAH-ISLAMIC", "Jeddah Islamic Port", PortOfEntryKinds.Seaport,
            "port-create", "qa", "corr-port-create", "SA", "Jeddah")));

    public void Dispose() => _procurement.Dispose();
}
