using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-SPO-03 — "capture supplier acknowledgement as accept, reject or counter, including a revised
/// lead time when applicable".
///
/// <para>The column, its CHECK constraint and the SLA escalation sweep that reads it all existed;
/// the write path did not. Nothing anywhere in the platform could set
/// <c>AcknowledgementStatus</c>, so the escalation could only ever be silenced by a developer with
/// a SQL prompt, and FR-SPO-04's ACKNOWLEDGED state was unreachable. These tests pin the write
/// path's deliberate semantics — above all that a counter and a rejection are ANSWERS but not
/// AGREEMENT, and must never display as an acknowledged order — plus the two SLA clock defects
/// fixed alongside it.</para>
/// </summary>
public sealed class Gate4SupplierAcknowledgementTests
{
    /// <summary>The supplier's own person. Deliberately not the actor: Nexora has no supplier
    /// portal, so a buyer keys in what the supplier said, and the audit trail must keep the two
    /// identities apart.</summary>
    private const string SupplierContact = "Mansour Al-Otaibi (Gulf Metals)";

    private const string RecordingBuyer = "buyer@tenant.test";

    // ------------------------------------------------------------------ accept / counter / reject

    [Fact]
    public async Task An_accepted_order_advances_to_acknowledged_and_names_the_supplier_person()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-accept", quantity: 8m);
        var before = DateTime.UtcNow;

        var result = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-accept-cmd", SupplierAcknowledgementStatuses.Accepted,
                dispatched.Version)));

        Assert.Equal(SupplierPurchaseOrderStatuses.Acknowledged, result.Status);
        Assert.Equal(SupplierAcknowledgementStatuses.Accepted, result.AcknowledgementStatus);
        Assert.Equal(SupplierContact, result.AcknowledgedBy);
        Assert.InRange(result.AcknowledgedOn, before, DateTime.UtcNow);
        Assert.Equal(dispatched.Version + 1, result.Version);
        Assert.False(result.Replayed);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Acknowledged, row.Status);
        Assert.Equal(SupplierAcknowledgementStatuses.Accepted, row.AcknowledgementStatus);
        Assert.Equal(SupplierContact, row.AcknowledgedBy);
        Assert.NotNull(row.AcknowledgedOn);
        Assert.Equal(RecordingBuyer, row.ModifiedBy);

        var acknowledgedEvent = await verify.ProcurementEvents
            .SingleAsync(x => x.EventType == "SUPPLIER_PO_ACKNOWLEDGED");
        Assert.Equal(RecordingBuyer, acknowledgedEvent.Actor);
        using var payload = JsonDocument.Parse(acknowledgedEvent.PayloadJson);
        Assert.Equal(SupplierAcknowledgementStatuses.Accepted,
            payload.RootElement.GetProperty("acknowledgementStatus").GetString());
        // The supplier's person and our recording user are two different facts on one row.
        Assert.Equal(SupplierContact, payload.RootElement.GetProperty("acknowledgedBy").GetString());
        Assert.Equal(SupplierPurchaseOrderStatuses.Acknowledged,
            payload.RootElement.GetProperty("resultingStatus").GetString());
    }

    [Fact]
    public async Task A_counter_is_recorded_without_the_order_reading_as_acknowledged()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-counter", quantity: 8m);
        var shipDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(21);

        var result = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-counter-cmd", SupplierAcknowledgementStatuses.Countered,
                dispatched.Version, revisedLeadTimeDays: 45, committedShipDate: shipDate,
                note: "Mill slot moved; 45 days from PO date.")));

        // A counter is the supplier asking for different terms. It is an ANSWER, which is why it
        // stops the escalation, but it is not AGREEMENT, so the order must not read as acknowledged
        // to anyone glancing at the status column.
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, result.Status);
        Assert.Equal(SupplierAcknowledgementStatuses.Countered, result.AcknowledgementStatus);
        Assert.Equal(45, result.RevisedLeadTimeDays);
        Assert.Equal(shipDate, result.CommittedShipDate);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, row.Status);
        Assert.Equal(SupplierAcknowledgementStatuses.Countered, row.AcknowledgementStatus);
        Assert.NotNull(row.AcknowledgedOn);
        Assert.Equal(45, row.RevisedLeadTimeDays);
        // Written onto the order because the ship-date reminder sweep reads this column: a counter
        // that moved the date silently would keep chasing a date the supplier has disowned.
        Assert.Equal(shipDate, row.CommittedShipDate);
        Assert.Equal("Mill slot moved; 45 days from PO date.", row.AcknowledgementNote);
    }

    [Fact]
    public async Task A_rejection_is_recorded_without_the_order_reading_as_acknowledged()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-reject", quantity: 8m);

        var result = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-reject-cmd", SupplierAcknowledgementStatuses.Rejected,
                dispatched.Version, note: "Material discontinued; cannot supply.")));

        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, result.Status);
        Assert.Equal(SupplierAcknowledgementStatuses.Rejected, result.AcknowledgementStatus);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, row.Status);
        Assert.Equal(SupplierAcknowledgementStatuses.Rejected, row.AcknowledgementStatus);
        Assert.Equal("Material discontinued; cannot supply.", row.AcknowledgementNote);
        Assert.NotNull(row.AcknowledgedOn);
        // The refusal is still an audited decision, not a silent field edit.
        Assert.Single(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_ACKNOWLEDGED").ToListAsync());
    }

    // ------------------------------------------------------------------------------- input rules

    [Fact]
    public async Task An_answer_that_is_not_one_of_the_three_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-unknown", quantity: 8m);

        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-unknown-cmd", "MAYBE", dispatched.Version))));

        Assert.Contains("ACCEPTED, REJECTED or COUNTERED", exception.Message, StringComparison.Ordinal);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Fact]
    public async Task The_supplier_contact_is_mandatory()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-no-contact", quantity: 8m);

        // Whitespace is not a name. Without a supplier-side person the row records that "someone"
        // agreed, which is exactly the ambiguity the separate column exists to remove.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-no-contact-cmd", SupplierAcknowledgementStatuses.Accepted,
                    dispatched.Version, acknowledgedBy: "   "))));

        Assert.Contains("who at the supplier", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Fact]
    public async Task A_counter_must_state_a_revised_lead_time_or_a_committed_ship_date()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-empty-counter", quantity: 8m);

        // An empty counter says nothing the buyer can act on, and would only serve to silence the
        // escalation sweep.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-empty-counter-cmd",
                    SupplierAcknowledgementStatuses.Countered, dispatched.Version,
                    note: "They want to talk."))));

        Assert.Contains("revised lead time", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);

        // Either one alone is enough — a lead time OR a date, not both.
        var byLeadTime = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-empty-counter-fix", SupplierAcknowledgementStatuses.Countered,
                dispatched.Version, revisedLeadTimeDays: 30)));
        Assert.Equal(SupplierAcknowledgementStatuses.Countered, byLeadTime.AcknowledgementStatus);
    }

    [Fact]
    public async Task A_counter_stated_only_as_a_ship_date_is_accepted()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-counter-date", quantity: 8m);
        var shipDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);

        var result = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-counter-date-cmd", SupplierAcknowledgementStatuses.Countered,
                dispatched.Version, committedShipDate: shipDate)));

        Assert.Equal(SupplierAcknowledgementStatuses.Countered, result.AcknowledgementStatus);
        Assert.Null(result.RevisedLeadTimeDays);
        Assert.Equal(shipDate, result.CommittedShipDate);
    }

    [Fact]
    public async Task A_rejection_must_record_the_suppliers_reason()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-bare-reject", quantity: 8m);

        // A rejection with no reason cannot be acted on: nobody downstream knows whether to
        // re-source, re-price or chase.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-bare-reject-cmd",
                    SupplierAcknowledgementStatuses.Rejected, dispatched.Version))));

        Assert.Contains("reason", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);

        // Whitespace is not a reason either.
        await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-bare-reject-blank",
                    SupplierAcknowledgementStatuses.Rejected, dispatched.Version, note: "   "))));
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_revised_lead_time_must_be_positive(int leadTimeDays)
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync($"ack-lead-{leadTimeDays}", quantity: 8m);

        // The database CHECK says the same thing, but the SQLite lane runs with check constraints
        // disabled, so application validation is what actually holds here.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, $"ack-lead-cmd-{leadTimeDays}",
                    SupplierAcknowledgementStatuses.Countered, dispatched.Version,
                    revisedLeadTimeDays: leadTimeDays))));

        Assert.Contains("positive", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Theory]
    [InlineData(SupplierAcknowledgementStatuses.Accepted)]
    [InlineData(SupplierAcknowledgementStatuses.Rejected)]
    public async Task A_revised_lead_time_is_refused_unless_the_answer_is_a_counter(string status)
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync($"ack-lead-status-{status}", quantity: 8m);

        // A changed lead time IS the counter. Storing one under ACCEPTED would record the supplier
        // agreeing to the order as sent while quietly moving the date the customer promise rests
        // on, and a refused order has no schedule at all. Both are refused rather than reinterpreted.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, $"ack-lead-status-cmd-{status}", status, dispatched.Version,
                    revisedLeadTimeDays: 21, note: "They mentioned a longer lead time."))));

        Assert.Contains("counter-offer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNTERED", exception.Message, StringComparison.Ordinal);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Fact]
    public async Task A_rejected_order_cannot_carry_a_committed_ship_date()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-reject-ship-date", quantity: 8m);

        // A supplier who will not supply cannot also be committing to ship. Storing the date would
        // put the order back in front of the ship-date reminder sweep it was just excluded from.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-reject-ship-date-cmd",
                    SupplierAcknowledgementStatuses.Rejected, dispatched.Version,
                    committedShipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7),
                    note: "Cannot supply."))));

        Assert.Contains("no committed ship date", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Fact]
    public async Task An_accepted_order_may_still_confirm_a_committed_ship_date()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-accept-ship-date", quantity: 8m);
        var shipDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(9);

        // Accepting the order as sent AND naming the day it ships is one coherent answer, and it is
        // the answer that arms the ship-date reminder. Only the lead time is counter-only.
        var result = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-accept-ship-date-cmd", SupplierAcknowledgementStatuses.Accepted,
                dispatched.Version, committedShipDate: shipDate)));

        Assert.Equal(SupplierPurchaseOrderStatuses.Acknowledged, result.Status);
        Assert.Equal(shipDate, result.CommittedShipDate);
        Assert.Null(result.RevisedLeadTimeDays);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(shipDate, row.CommittedShipDate);
        Assert.Null(row.RevisedLeadTimeDays);
    }

    [Fact]
    public async Task A_committed_ship_date_in_the_past_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-past-date", quantity: 8m);

        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-past-date-cmd", SupplierAcknowledgementStatuses.Countered,
                    dispatched.Version,
                    committedShipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)))));

        Assert.Contains("past", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);

        // Today is not the past: a supplier phoning in "it ships today" is a real answer.
        var today = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-today-date-cmd", SupplierAcknowledgementStatuses.Countered,
                dispatched.Version, committedShipDate: DateOnly.FromDateTime(DateTime.UtcNow))));
        Assert.Equal(SupplierAcknowledgementStatuses.Countered, today.AcknowledgementStatus);
    }

    // -------------------------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Only_an_order_that_reached_the_supplier_can_be_acknowledged()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("ack-lifecycle", quantity: 8m);
        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "ack-lifecycle-po")));

        // A DRAFT is a proposal the supplier has never seen.
        var onDraft = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, draft.Id, "ack-lifecycle-draft", SupplierAcknowledgementStatuses.Accepted,
                    draft.Version))));
        Assert.Contains("dispatched", onDraft.Message, StringComparison.OrdinalIgnoreCase);

        // APPROVED is authorised internally and still has not been sent: an acknowledgement here
        // would be a supplier answering an order they were never asked.
        var approval = await fixture.ApproveAsync(draft.Id, "ack-lifecycle-approve");
        var onApproved = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, draft.Id, "ack-lifecycle-approved", SupplierAcknowledgementStatuses.Accepted,
                    approval.Version))));
        Assert.Contains("dispatched", onApproved.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, draft.Id);

        var issued = await fixture.Execute(service => service.IssuePurchaseOrderAsync(
            fixture.Issue(draft.Id, "ack-lifecycle-issue", approval.Version)));
        var accepted = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, draft.Id, "ack-lifecycle-ack", SupplierAcknowledgementStatuses.Accepted,
                issued.Version)));
        Assert.Equal(SupplierPurchaseOrderStatuses.Acknowledged, accepted.Status);
    }

    [Fact]
    public async Task An_order_that_already_carries_an_acknowledgement_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-twice", quantity: 8m);

        var first = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            Ack(fixture, dispatched.Id, "ack-twice-first", SupplierAcknowledgementStatuses.Countered,
                dispatched.Version, revisedLeadTimeDays: 40)));

        // A different key and the correct new version: this is a second, genuinely new request,
        // and it is refused because the supplier's answer is already on the record. Overwriting it
        // would destroy the counter the buyer still owes a decision on.
        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-twice-second", SupplierAcknowledgementStatuses.Accepted,
                    first.Version))));

        Assert.Contains("already carries", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(SupplierAcknowledgementStatuses.Countered, row.AcknowledgementStatus);
        Assert.Equal(40, row.RevisedLeadTimeDays);
        Assert.Single(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_ACKNOWLEDGED").ToListAsync());
    }

    [Fact]
    public async Task A_stale_expected_version_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-stale", quantity: 8m);

        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                Ack(fixture, dispatched.Id, "ack-stale-cmd", SupplierAcknowledgementStatuses.Accepted,
                    dispatched.Version - 1))));

        Assert.Contains("refresh", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    [Fact]
    public async Task Acknowledgement_cannot_reach_across_a_tenant_boundary()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-tenant", quantity: 8m);

        await using (var otherTenant = fixture.Context(fixture.OtherBusinessUnitId))
        {
            var service = new ProcurementApplicationService(otherTenant);
            await Assert.ThrowsAsync<ProcurementValidationException>(() =>
                service.AcknowledgePurchaseOrderAsync(new AcknowledgeSupplierPurchaseOrderCommand(
                    fixture.OtherBusinessUnitId, dispatched.Id, dispatched.Version,
                    SupplierAcknowledgementStatuses.Accepted, SupplierContact, "ack-tenant-cmd",
                    RecordingBuyer, "corr-ack-tenant")));
        }

        await AssertUnacknowledgedAsync(fixture, dispatched.Id);
    }

    // ------------------------------------------------------------------------------ idempotency

    [Fact]
    public async Task Replaying_the_same_key_returns_the_first_answer_and_does_not_bump_the_version()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-replay", quantity: 8m);
        var command = Ack(fixture, dispatched.Id, "ack-replay-cmd", SupplierAcknowledgementStatuses.Countered,
            dispatched.Version, revisedLeadTimeDays: 60, note: "Foundry backlog.");

        var first = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(command));
        var replay = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(command));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.AcknowledgedOn, replay.AcknowledgedOn);
        Assert.Equal(first.AcknowledgementStatus, replay.AcknowledgementStatus);
        Assert.Equal(first.RevisedLeadTimeDays, replay.RevisedLeadTimeDays);
        // A retried HTTP request must not walk the order's version forward, or the caller's next
        // optimistic write fails against a version nothing actually changed.
        Assert.Equal(first.Version, replay.Version);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(dispatched.Version + 1, row.Version);
        Assert.Single(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_ACKNOWLEDGED").ToListAsync());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_answer_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("ack-key-reuse", quantity: 8m);
        var command = Ack(fixture, dispatched.Id, "ack-key-reuse-cmd", SupplierAcknowledgementStatuses.Accepted,
            dispatched.Version);

        await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(command));

        // Same key, different answer. Replaying this as "already done" would report an acceptance
        // the caller never asked for; the request is refused instead.
        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                command with
                {
                    AcknowledgementStatus = SupplierAcknowledgementStatuses.Rejected,
                    Note = "Actually they refused."
                })));
        Assert.Contains("different request", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Even a change to nothing but the supplier's contact name is a different request.
        await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
                command with { AcknowledgedBy = "Someone else entirely" })));

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Equal(SupplierAcknowledgementStatuses.Accepted, row.AcknowledgementStatus);
        Assert.Equal(SupplierContact, row.AcknowledgedBy);
    }

    // ------------------------------------------------------------------ the SLA clocks it silences

    [Fact]
    public async Task A_rejected_order_is_dropped_from_the_ship_date_reminder_but_a_counter_is_not()
    {
        using var host = new AckSweepHost();
        var shipDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        await host.SeedAsync(ctx =>
        {
            // The supplier has said they will not supply. Reminding the buyer about a date that
            // supplier has already disowned trains people to ignore the mailbox.
            AckSweepHost.Order(ctx, 1, "PO-ACK-REJECTED", SupplierPurchaseOrderStatuses.Issued,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-1), approverId: AckSweepHost.BuyerId,
                committedShipDate: shipDate,
                ackStatus: SupplierAcknowledgementStatuses.Rejected, ackOn: DateTime.UtcNow.AddHours(-2));
            // A counter still names a date the supplier intends to hit, so the reminder stands.
            AckSweepHost.Order(ctx, 2, "PO-ACK-COUNTERED", SupplierPurchaseOrderStatuses.Sent,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-1), approverId: AckSweepHost.BuyerId,
                committedShipDate: shipDate,
                ackStatus: SupplierAcknowledgementStatuses.Countered, ackOn: DateTime.UtcNow.AddHours(-2));
        });

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal("Purchase order PO-ACK-COUNTERED", alert.EntityLabel);
        Assert.Equal("warn", alert.Level);
    }

    [Fact]
    public async Task No_escalation_fires_for_an_order_carrying_any_acknowledgement()
    {
        using var host = new AckSweepHost();
        await host.SeedAsync(ctx =>
        {
            // All three answers stop the chase. The buyer still owes a decision on a counter or a
            // rejection, but that is a different governed action — not an unanswered supplier.
            AckSweepHost.Order(ctx, 1, "PO-ACCEPTED", SupplierPurchaseOrderStatuses.Acknowledged,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-30), approverId: AckSweepHost.BuyerId,
                ackStatus: SupplierAcknowledgementStatuses.Accepted, ackOn: DateTime.UtcNow.AddDays(-29));
            AckSweepHost.Order(ctx, 2, "PO-COUNTERED", SupplierPurchaseOrderStatuses.Sent,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-30), approverId: AckSweepHost.BuyerId,
                ackStatus: SupplierAcknowledgementStatuses.Countered, ackOn: DateTime.UtcNow.AddDays(-29));
            AckSweepHost.Order(ctx, 3, "PO-REJECTED", SupplierPurchaseOrderStatuses.Issued,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-30), approverId: AckSweepHost.BuyerId,
                ackStatus: SupplierAcknowledgementStatuses.Rejected, ackOn: DateTime.UtcNow.AddDays(-29));
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task The_escalation_clock_starts_at_dispatch_not_at_internal_approval()
    {
        // REGRESSION. The clock used to start at ApprovedOn. An order approved a month ago and only
        // now sent to the supplier was therefore escalated on the very first sweep after dispatch —
        // the supplier was reported as late before they had been given a single working hour, and
        // the escalation channel filled with noise nobody could act on.
        using var host = new AckSweepHost();
        await host.SeedAsync(ctx => AckSweepHost.Order(ctx, 1, "PO-APPROVED-LONG-AGO",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-30),
            sentToSupplierOn: DateTime.UtcNow.AddMinutes(-5),
            approverId: AckSweepHost.BuyerId));

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task An_order_dispatched_long_ago_and_still_unanswered_does_escalate()
    {
        // The other half of the regression: moving the clock to dispatch must not disarm the sweep.
        using var host = new AckSweepHost();
        await host.SeedAsync(ctx => AckSweepHost.Order(ctx, 1, "PO-SENT-LONG-AGO",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-31),
            sentToSupplierOn: DateTime.UtcNow.AddDays(-30),
            approverId: AckSweepHost.BuyerId));

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal("escalated", alert.Level);
        Assert.Equal("Purchase order PO-SENT-LONG-AGO", alert.EntityLabel);
        Assert.Equal(AckSweepHost.SupervisorEmail, alert.ToEmail);
    }

    [Fact]
    public async Task An_order_with_no_recorded_dispatch_still_falls_back_to_approval()
    {
        // Rows raised before SentToSupplierOn was written carry NULL. Reading NULL as "never
        // dispatched" would exempt every legacy order from the chase for good.
        using var host = new AckSweepHost();
        await host.SeedAsync(ctx => AckSweepHost.Order(ctx, 1, "PO-LEGACY-NO-DISPATCH",
            SupplierPurchaseOrderStatuses.Issued,
            approvedOn: DateTime.UtcNow.AddDays(-30), approverId: AckSweepHost.BuyerId));

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public async Task A_policy_of_zero_or_less_means_not_configured_and_sends_nothing(
        int shipReminderDays, int ackEscalationHours)
    {
        // REGRESSION. A non-positive window used to read as a zero-hour deadline, so the first
        // sweep escalated every dispatched order in the tenant at once and reminded on every date.
        // That is how an alerting channel gets muted permanently by the people receiving it.
        using var host = new AckSweepHost(shipReminderDays, ackEscalationHours);
        await host.SeedAsync(ctx =>
        {
            AckSweepHost.Order(ctx, 1, "PO-UNANSWERED", SupplierPurchaseOrderStatuses.Sent,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-30), approverId: AckSweepHost.BuyerId);
            AckSweepHost.Order(ctx, 2, "PO-SHIPPING-SOON", SupplierPurchaseOrderStatuses.Sent,
                sentToSupplierOn: DateTime.UtcNow.AddDays(-1), approverId: AckSweepHost.BuyerId,
                committedShipDate: DateOnly.FromDateTime(DateTime.UtcNow));
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
        using var verify = host.UnscopedContext();
        Assert.Empty(await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// A command that keeps the supplier's person (<paramref name="acknowledgedBy"/>) and the
    /// internal user recording it (the actor) visibly different, because conflating them is the
    /// specific mistake this field exists to prevent.
    /// </summary>
    private static AcknowledgeSupplierPurchaseOrderCommand Ack(
        ProcurementScenario fixture, long purchaseOrderId, string key, string status, long version,
        string acknowledgedBy = SupplierContact, int? revisedLeadTimeDays = null,
        DateOnly? committedShipDate = null, string? note = null) => new(
        fixture.BusinessUnitId, purchaseOrderId, version, status, acknowledgedBy, key,
        RecordingBuyer, $"corr-{key}", revisedLeadTimeDays, committedShipDate, note);

    private static async Task AssertUnacknowledgedAsync(ProcurementScenario fixture, long purchaseOrderId)
    {
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == purchaseOrderId);
        Assert.Null(row.AcknowledgementStatus);
        Assert.Null(row.AcknowledgedOn);
        Assert.Null(row.AcknowledgedBy);
        Assert.Empty(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_ACKNOWLEDGED").ToListAsync());
    }

    private sealed record SentAlert(string ToEmail, string Level, string EntityLabel, long BusinessUnitId);

    private sealed class CapturingNotifications : ISlaNotifications
    {
        private readonly object _gate = new();
        public List<SentAlert> Sent { get; } = new();

        public Task<bool> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, level, entityLabel, businessUnitId));
            return Task.FromResult(true);
        }

        public Task<bool> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, "stale", "digest", businessUnitId));
            return Task.FromResult(true);
        }
    }

    private sealed class NoOpOutcomes : IQuoteOutcomeService
    {
        public Task<ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteResponseDTO> SetOutcomeAsync(
            long quoteId, long businessUnitId, string actorEmail, string outcome,
            string? reasonCode = null, string? note = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default)
            => Task.FromResult(false);

        public Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutcomeReasonDto>>(Array.Empty<OutcomeReasonDto>());
    }

    /// <summary>
    /// Production-shaped worker host, mirroring <c>SlaSupplierOrderSweepTests.SweepHost</c>: the
    /// worker pushes an ambient tenant scope which a scoped ITenantContext reads when the DbContext
    /// is built. The seed helper here additionally writes <c>SentToSupplierOn</c>, which is what the
    /// escalation clock now reads.
    /// </summary>
    private sealed class AckSweepHost : IDisposable
    {
        public const long Bu = 4_200;
        public const long RfqId = 4_210;
        public const long SupplierId = 4_220;
        public const long CurrencyId = 4_230;
        public const long ManagerRoleId = 4_240;
        public const long MemberRoleId = 4_241;
        public const long BuyerId = 4_250;
        public const long SupervisorId = 4_251;

        public const string BuyerEmail = "buyer@ack.test";
        public const string SupervisorEmail = "supervisor@ack.test";

        private static readonly DateTime Anchor = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<ErpRfqAutomationContext> _rawOptions;
        private readonly int _shipReminderDays;
        private readonly int _ackEscalationHours;

        public CapturingNotifications Notifications { get; } = new();

        public AckSweepHost(int shipReminderDays = 3, int ackEscalationHours = 48)
        {
            _shipReminderDays = shipReminderDays;
            _ackEscalationHours = ackEscalationHours;

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _rawOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .Options;
            using (var create = new ErpRfqAutomationContext(_rawOptions, new StubTenant(null)))
                create.Database.EnsureCreated();

            var services = new ServiceCollection();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext>(sp =>
                new StubTenant(sp.GetRequiredService<ITenantScopeAccessor>().BusinessUnitId));
            services.AddDbContext<ErpRfqAutomationContext>(
                o => o.UseSqlite(_connection), ServiceLifetime.Scoped);
            services.AddSingleton<ISlaNotifications>(Notifications);
            services.AddScoped<IQuoteOutcomeService, NoOpOutcomes>();
            _provider = services.BuildServiceProvider();
        }

        public SlaSweepWorker CreateWorker() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ITenantScopeAccessor>(),
            NullLogger<SlaSweepWorker>.Instance);

        public ErpRfqAutomationContext UnscopedContext() => new(_rawOptions, new StubTenant(null));

        public async Task SeedAsync(Action<ErpRfqAutomationContext> addOrders)
        {
            await using var seed = UnscopedContext();

            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();

            seed.SetupMasters.Add(Role(ManagerRoleId, "MANAGER", "Manager", RoleRanks.Manager));
            seed.SetupMasters.Add(Role(MemberRoleId, "MEMBER", "Member", RoleRanks.Member));
            AgentSeed.Rfq(seed, RfqId, Bu);
            AgentSeed.Supplier(seed, SupplierId, Bu, "QA Supplier", "supplier@ack.test");
            seed.Currencies.Add(new Currency
            {
                Id = CurrencyId, BusinessUnitId = Bu, Code = "SAR", CurrencyName = "Saudi Riyal",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
                CreatedBy = "seed", CreatedOn = Anchor
            });
            seed.Set<SlaPolicy>().Add(new SlaPolicy
            {
                BusinessUnitId = Bu,
                SupplierShipDateReminderDays = _shipReminderDays,
                SupplierAckEscalationHours = _ackEscalationHours,
                CreatedOn = Anchor,
                UpdatedOn = Anchor
            });
            await seed.SaveChangesAsync();

            seed.Users.Add(User(SupervisorId, SupervisorEmail, "Sam", ManagerRoleId, managerId: null));
            await seed.SaveChangesAsync();
            seed.Users.Add(User(BuyerId, BuyerEmail, "Bea", MemberRoleId, managerId: SupervisorId));
            await seed.SaveChangesAsync();

            addOrders(seed);
            await seed.SaveChangesAsync();
        }

        /// <summary>
        /// <paramref name="sentToSupplierOn"/> is the dispatch instant the escalation clock counts
        /// from; <paramref name="approvedOn"/> is the internal authorisation, which is only the
        /// fallback for rows raised before dispatch was recorded.
        /// </summary>
        public static SupplierPurchaseOrder Order(
            ErpRfqAutomationContext ctx, long id, string number, string status,
            DateTime? approvedOn = null, long? approverId = null, DateTime? sentToSupplierOn = null,
            DateOnly? committedShipDate = null, string? ackStatus = null, DateTime? ackOn = null)
        {
            var order = new SupplierPurchaseOrder
            {
                Id = id,
                BusinessUnitId = Bu,
                RfqId = RfqId,
                DemandSource = SupplierPurchaseOrderDemandSources.Stock,
                SupplierId = SupplierId,
                CurrencyId = CurrencyId,
                PurchaseOrderNumber = number,
                Status = status,
                TotalValue = 100m,
                ApprovedByUserId = approverId,
                ApprovedBy = approverId is null ? null : $"user-{approverId}",
                ApprovedOn = approverId is null ? null : approvedOn ?? sentToSupplierOn ?? Anchor,
                SentToSupplierOn = sentToSupplierOn,
                AcknowledgementStatus = ackStatus,
                AcknowledgedOn = ackOn,
                AcknowledgedBy = ackStatus is null ? null : "Supplier contact",
                CommittedShipDate = committedShipDate,
                IdempotencyKey = $"ack-sla-po-{id}",
                RequestHash = new string('a', 64),
                Version = 1,
                CreatedOn = approvedOn ?? sentToSupplierOn ?? Anchor,
                CreatedBy = "seed"
            };
            ctx.SupplierPurchaseOrders.Add(order);
            return order;
        }

        private static SetupMaster Role(long setupId, string code, string value, short rank) => new()
        {
            SetupId = setupId, SetupType = "Role", SetupCode = code, SetupValue = value,
            BusinessUnitId = Bu, RoleRank = rank, IsActive = true,
            CreatedBy = "seed", CreatedOn = Anchor
        };

        private static Models.User User(long id, string email, string firstName, long roleId, long? managerId) => new()
        {
            Id = id, FirstName = firstName, LastName = "Tester", Email = email,
            PasswordHash = "x", ImageUrl = "n/a", Buid = Bu, RoleId = roleId, ManagerId = managerId,
            IsActive = true, CreatedBy = "seed", CreatedOn = Anchor
        };

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
