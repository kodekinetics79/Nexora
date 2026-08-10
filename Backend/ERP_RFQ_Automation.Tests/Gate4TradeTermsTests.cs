using System.Text.Json;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-SPO-06 — Incoterm, ports of loading and discharge, and per-line customs data (HS code and
/// country of origin) on a supplier purchase order.
///
/// <para>A KSA importer cannot clear a shipment without these. They had no home on the order at
/// all, so every one of them lived in a buyer's mailbox and had to be re-keyed into the customs
/// broker's forms from memory. The rules pinned here are the ones that make a sparse correction
/// safe: an omitted field means LEAVE UNCHANGED, never clear, so fixing one line's HS code cannot
/// silently wipe the Incoterm the rest of the order depends on — and the whole surface closes at
/// dispatch, because the Incoterm the supplier holds and the Incoterm we hold must not
/// diverge.</para>
/// </summary>
public sealed class Gate4TradeTermsTests
{
    private const string Buyer = "buyer@tenant.test";

    // -------------------------------------------------------------------------- set at creation

    [Fact]
    public async Task Terms_named_when_the_order_is_raised_are_carried_onto_it()
    {
        using var fixture = new ProcurementScenario();
        await SetProductOriginAsync(fixture, "Germany");
        var (draft, lineId) = await DraftAsync(fixture, "terms-create",
            incoterm: "cif", portOfLoading: " Hamburg ", portOfDischarge: "Jeddah Islamic Port");

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        // Stored upper-case and trimmed, so "cif" and "CIF" are one term rather than two.
        Assert.Equal("CIF", row.Incoterm);
        Assert.Equal("Hamburg", row.PortOfLoading);
        Assert.Equal("Jeddah Islamic Port", row.PortOfDischarge);

        var line = await verify.SupplierPurchaseOrderLines.SingleAsync(x => x.Id == lineId);
        // Seeded from the product master at creation rather than left null for someone to discover
        // at the border.
        Assert.Equal("Germany", line.CountryOfOrigin);
        Assert.Null(line.HsCode);
    }

    [Fact]
    public async Task An_order_raised_without_terms_carries_none_rather_than_a_guess()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-create-none");

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Null(row.Incoterm);
        Assert.Null(row.PortOfLoading);
        Assert.Null(row.PortOfDischarge);
        // The seeded product carries no country of origin, so neither does the line. A default here
        // would be a customs declaration nobody made.
        var line = await verify.SupplierPurchaseOrderLines.SingleAsync(x => x.Id == lineId);
        Assert.Null(line.CountryOfOrigin);
    }

    // ------------------------------------------------------------------- omitted means unchanged

    [Fact]
    public async Task Correcting_one_line_leaves_the_orders_own_terms_alone()
    {
        using var fixture = new ProcurementScenario();
        await SetProductOriginAsync(fixture, "Germany");
        var (draft, lineId) = await DraftAsync(fixture, "terms-sparse-line",
            incoterm: "CIF", portOfLoading: "Hamburg", portOfDischarge: "Jeddah Islamic Port");

        // The common case: the broker comes back with the right HS code and nothing else changes.
        var amended = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, "terms-sparse-line-cmd", draft.Version,
                lines: [new PurchaseOrderLineTradeTerms(lineId, "7318.15", null)])));

        Assert.Equal("CIF", amended.Incoterm);
        Assert.Equal("Hamburg", amended.PortOfLoading);
        Assert.Equal("Jeddah Islamic Port", amended.PortOfDischarge);
        var amendedLine = Assert.Single(amended.Lines);
        Assert.Equal("7318.15", amendedLine.HsCode);
        // The country of origin was not mentioned, so it survives. Reading an omitted field as a
        // deletion would empty the customs declaration on every narrow correction.
        Assert.Equal("Germany", amendedLine.CountryOfOrigin);
        Assert.Equal(draft.Version + 1, amended.Version);
        Assert.False(amended.Replayed);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal("CIF", row.Incoterm);
        Assert.Equal("Hamburg", row.PortOfLoading);
        var line = await verify.SupplierPurchaseOrderLines.SingleAsync(x => x.Id == lineId);
        Assert.Equal("7318.15", line.HsCode);
        Assert.Equal("Germany", line.CountryOfOrigin);
    }

    [Fact]
    public async Task Correcting_one_header_term_leaves_the_lines_and_the_other_terms_alone()
    {
        using var fixture = new ProcurementScenario();
        await SetProductOriginAsync(fixture, "Germany");
        var (draft, lineId) = await DraftAsync(fixture, "terms-sparse-header",
            incoterm: "CIF", portOfLoading: "Hamburg", portOfDischarge: "Jeddah Islamic Port");
        var seeded = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, "terms-sparse-header-seed", draft.Version,
                lines: [new PurchaseOrderLineTradeTerms(lineId, "7318.15", "Germany")])));

        var amended = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, "terms-sparse-header-cmd", seeded.Version,
                portOfDischarge: "King Abdullah Port")));

        Assert.Equal("King Abdullah Port", amended.PortOfDischarge);
        Assert.Equal("CIF", amended.Incoterm);
        Assert.Equal("Hamburg", amended.PortOfLoading);
        var line = Assert.Single(amended.Lines);
        Assert.Equal("7318.15", line.HsCode);
        Assert.Equal("Germany", line.CountryOfOrigin);
    }

    // ------------------------------------------------------------------------------ input rules

    [Fact]
    public async Task An_incoterm_outside_the_2020_set_is_refused_and_the_set_is_named()
    {
        using var fixture = new ProcurementScenario();
        var (draft, _) = await DraftAsync(fixture, "terms-bad-incoterm", incoterm: "CIF");

        // Free text here becomes a contractual allocation of freight and risk that no system can
        // interpret. The refusal lists the legal codes so the buyer can pick one.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-bad-incoterm-cmd", draft.Version, incoterm: "DDU"))));

        Assert.Contains("Incoterms 2020", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DAP", exception.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    [Theory]
    [InlineData("fob", "FOB")]
    [InlineData(" ExW ", "EXW")]
    public async Task An_incoterm_is_normalised_to_its_canonical_code(string entered, string stored)
    {
        using var fixture = new ProcurementScenario();
        var (draft, _) = await DraftAsync(fixture, $"terms-case-{stored}");

        var amended = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, $"terms-case-cmd-{stored}", draft.Version, incoterm: entered)));

        Assert.Equal(stored, amended.Incoterm);
    }

    [Fact]
    public async Task A_line_that_does_not_belong_to_the_order_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-foreign-line", incoterm: "CIF");

        // An id the caller invented, or one belonging to another order. Writing customs data onto
        // whichever row happened to match would declare one shipment's goods as another's.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-foreign-line-cmd", draft.Version,
                    lines: [new PurchaseOrderLineTradeTerms(lineId + 9_999, "7318.15", "Germany")]))));

        Assert.Contains("do not belong to this purchase order", exception.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    [Fact]
    public async Task A_line_cannot_appear_twice_in_one_amendment()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-dupe-line", incoterm: "CIF");

        // Two entries for one line have no defined winner; silently applying the last would make
        // the outcome depend on serialisation order.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-dupe-line-cmd", draft.Version,
                    lines:
                    [
                        new PurchaseOrderLineTradeTerms(lineId, "7318.15", null),
                        new PurchaseOrderLineTradeTerms(lineId, "8481.80", null)
                    ]))));

        Assert.Contains("only once", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    [Fact]
    public async Task An_amendment_that_names_nothing_at_all_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var (draft, _) = await DraftAsync(fixture, "terms-empty", incoterm: "CIF");

        // An empty amendment would bump the version and write an audit row asserting a change that
        // never happened, which is worse than no record at all.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-empty-cmd", draft.Version))));

        Assert.Contains("changes nothing", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_TRADE_TERMS_AMENDED").ToListAsync());
    }

    [Fact]
    public async Task A_line_listed_with_no_values_is_refused_and_nothing_is_written()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-empty-line", incoterm: "CIF");

        // A line entry asking for nothing used to slip past the "changes nothing" guard purely
        // because the list was non-empty: the version moved and an amendment event was written
        // recording a change that never happened. A false entry in the audit trail is worse than a
        // no-op, so the whole request is refused before anything is touched.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-empty-line-cmd", draft.Version,
                    lines: [new PurchaseOrderLineTradeTerms(lineId, null, null)]))));

        Assert.Contains("without an HS code", exception.Message, StringComparison.Ordinal);
        Assert.Contains(lineId.ToString(), exception.Message, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    [Fact]
    public async Task A_whitespace_only_line_entry_is_refused_the_same_way()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-blank-line", incoterm: "CIF");

        // Whitespace normalises away, so " " is the same request as null. Treating the two
        // differently would make a buyer's stray space bar the difference between a refusal and a
        // phantom amendment.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-blank-line-cmd", draft.Version,
                    lines: [new PurchaseOrderLineTradeTerms(lineId, "   ", null)]))));

        Assert.Contains("without an HS code", exception.Message, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    [Fact]
    public async Task One_empty_line_entry_refuses_the_whole_amendment()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-mixed-lines", incoterm: "CIF");

        // A meaningful edit alongside an empty one. The empty entry is what is refused — the
        // message names it and says nothing about the line belonging to the order — and the
        // meaningful edit beside it is NOT applied. An amendment is one decision, so it lands
        // whole or not at all.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-mixed-lines-cmd", draft.Version,
                    portOfLoading: "Hamburg",
                    lines:
                    [
                        new PurchaseOrderLineTradeTerms(lineId, "7318.15", "Germany"),
                        new PurchaseOrderLineTradeTerms(lineId + 9_999, null, null)
                    ]))));

        Assert.Contains("without an HS code", exception.Message, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(fixture, draft.Id, "CIF", draft.Version);
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Null(row.PortOfLoading);
        var line = await verify.SupplierPurchaseOrderLines.SingleAsync(x => x.Id == lineId);
        Assert.Null(line.HsCode);
    }

    [Theory]
    [InlineData("7318.15", null)]
    [InlineData(null, "Germany")]
    public async Task A_line_entry_naming_one_field_is_valid_and_leaves_the_other_alone(
        string? hsCode, string? countryOfOrigin)
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, $"terms-half-line-{hsCode ?? countryOfOrigin}");
        // Give the line both values first, so "left alone" is observable rather than vacuous.
        var seeded = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, $"terms-half-line-seed-{hsCode ?? countryOfOrigin}", draft.Version,
                lines: [new PurchaseOrderLineTradeTerms(lineId, "0000.00", "Elbonia")])));

        var amended = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, $"terms-half-line-cmd-{hsCode ?? countryOfOrigin}", seeded.Version,
                lines: [new PurchaseOrderLineTradeTerms(lineId, hsCode, countryOfOrigin)])));

        var line = Assert.Single(amended.Lines);
        // The named field is set; the omitted one keeps what it had. Omission is not deletion.
        Assert.Equal(hsCode ?? "0000.00", line.HsCode);
        Assert.Equal(countryOfOrigin ?? "Elbonia", line.CountryOfOrigin);
        Assert.Equal(seeded.Version + 1, amended.Version);
    }

    [Fact]
    public async Task Over_long_customs_and_port_values_are_refused()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-too-long", incoterm: "CIF");

        var port = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-too-long-port", draft.Version,
                    portOfLoading: new string('x', 121)))));
        Assert.Contains("120 characters", port.Message, StringComparison.Ordinal);

        var hsCode = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-too-long-hs", draft.Version,
                    lines: [new PurchaseOrderLineTradeTerms(lineId, new string('9', 21), null)]))));
        Assert.Contains("20 characters", hsCode.Message, StringComparison.Ordinal);

        var origin = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-too-long-origin", draft.Version,
                    lines: [new PurchaseOrderLineTradeTerms(lineId, null, new string('c', 101))]))));
        Assert.Contains("100 characters", origin.Message, StringComparison.Ordinal);

        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    // -------------------------------------------------------------------------------- lifecycle

    [Fact]
    public async Task An_approved_order_can_still_have_its_terms_corrected()
    {
        using var fixture = new ProcurementScenario();
        var (draft, _) = await DraftAsync(fixture, "terms-approved");
        var approval = await fixture.ApproveAsync(draft.Id, "terms-approved-approve");

        // Approval authorises the spend, not the paperwork. The broker's answer routinely arrives
        // between approval and dispatch, and there is nothing to protect yet — the supplier has
        // not been told anything.
        var amended = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, "terms-approved-cmd", approval.Version, incoterm: "FOB")));

        Assert.Equal("FOB", amended.Incoterm);
        Assert.Equal(approval.Version + 1, amended.Version);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        // Correcting the paperwork must not undo the approval.
        Assert.Equal(SupplierPurchaseOrderStatuses.Approved, row.Status);
        Assert.NotNull(row.ApprovedOn);
    }

    [Fact]
    public async Task Terms_cannot_be_changed_once_the_order_is_with_the_supplier()
    {
        using var fixture = new ProcurementScenario();
        var dispatched = await fixture.CreatePurchaseOrderAsync("terms-dispatched", quantity: 8m);

        // Once the supplier holds the order, the Incoterm they hold and the Incoterm we hold must
        // not diverge silently. Changing terms after that is a re-issue, not an edit — and the
        // refusal says so rather than leaving the buyer to guess.
        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, dispatched.Id, "terms-dispatched-cmd", dispatched.Version,
                    incoterm: "DDP"))));

        Assert.Contains("before the order goes to the supplier", exception.Message, StringComparison.Ordinal);
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == dispatched.Id);
        Assert.Null(row.Incoterm);
        Assert.Equal(dispatched.Version, row.Version);
    }

    [Fact]
    public async Task A_stale_expected_version_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var (draft, _) = await DraftAsync(fixture, "terms-stale", incoterm: "CIF");

        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                Amend(fixture, draft.Id, "terms-stale-cmd", draft.Version + 1, incoterm: "FOB"))));

        Assert.Contains("refresh", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    [Fact]
    public async Task Amendment_cannot_reach_across_a_tenant_boundary()
    {
        using var fixture = new ProcurementScenario();
        var (draft, _) = await DraftAsync(fixture, "terms-tenant", incoterm: "CIF");

        await using (var otherTenant = fixture.Context(fixture.OtherBusinessUnitId))
        {
            var service = new ProcurementApplicationService(otherTenant);
            await Assert.ThrowsAsync<ProcurementValidationException>(() =>
                service.AmendPurchaseOrderTradeTermsAsync(new AmendPurchaseOrderTradeTermsCommand(
                    fixture.OtherBusinessUnitId, draft.Id, draft.Version, "terms-tenant-cmd", Buyer,
                    "corr-terms-tenant", "DDP")));
        }

        await AssertUnchangedAsync(fixture, draft.Id, "CIF", draft.Version);
    }

    // ------------------------------------------------------------------------------ idempotency

    [Fact]
    public async Task Replaying_the_same_key_returns_the_first_result_and_does_not_bump_the_version()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-replay", incoterm: "CIF");
        var command = Amend(fixture, draft.Id, "terms-replay-cmd", draft.Version, incoterm: "FOB",
            portOfLoading: "Hamburg",
            lines: [new PurchaseOrderLineTradeTerms(lineId, "7318.15", "Germany")]);

        var first = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(command));
        var replay = await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(command));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Incoterm, replay.Incoterm);
        Assert.Equal(first.PortOfLoading, replay.PortOfLoading);
        // A retried request must not walk the version forward, or the caller's next optimistic
        // write fails against a version nothing actually changed.
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(first.Lines.Single().HsCode, replay.Lines.Single().HsCode);

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(draft.Version + 1, row.Version);
        Assert.Single(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_TRADE_TERMS_AMENDED").ToListAsync());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_amendment_is_refused()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-key-reuse", incoterm: "CIF");
        var command = Amend(fixture, draft.Id, "terms-key-reuse-cmd", draft.Version, incoterm: "FOB");

        await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(command));

        // Same key, different content. Replaying this as "already done" would report an FOB order
        // as DDP, or vice versa.
        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                command with { Incoterm = "DDP" })));
        Assert.Contains("different request", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Adding a line edit to an otherwise identical amendment is a different request too.
        await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
                command with { Lines = [new PurchaseOrderLineTradeTerms(lineId, "7318.15", null)] })));

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal("FOB", row.Incoterm);
    }

    // ----------------------------------------------------------------------------- audit + read

    [Fact]
    public async Task The_amendment_is_audited_with_the_terms_that_resulted()
    {
        using var fixture = new ProcurementScenario();
        var (draft, lineId) = await DraftAsync(fixture, "terms-audit",
            incoterm: "CIF", portOfLoading: "Hamburg");

        await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, "terms-audit-cmd", draft.Version,
                portOfDischarge: "Jeddah Islamic Port",
                lines: [new PurchaseOrderLineTradeTerms(lineId, "7318.15", "Germany")])));

        await using var verify = fixture.Context();
        var amendment = await verify.ProcurementEvents
            .SingleAsync(x => x.EventType == "SUPPLIER_PO_TRADE_TERMS_AMENDED");
        Assert.Equal(Buyer, amendment.Actor);
        using var payload = JsonDocument.Parse(amendment.PayloadJson);
        // The RESULTING terms, not the delta: an auditor reading one row must be able to see what
        // the order said afterwards without replaying every earlier amendment.
        Assert.Equal("CIF", payload.RootElement.GetProperty("incoterm").GetString());
        Assert.Equal("Hamburg", payload.RootElement.GetProperty("portOfLoading").GetString());
        Assert.Equal("Jeddah Islamic Port", payload.RootElement.GetProperty("portOfDischarge").GetString());
        var auditedLine = Assert.Single(payload.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(lineId, auditedLine.GetProperty("Id").GetInt64());
        Assert.Equal("7318.15", auditedLine.GetProperty("HsCode").GetString());
        Assert.Equal("Germany", auditedLine.GetProperty("CountryOfOrigin").GetString());
    }

    [Fact]
    public async Task The_workbench_shows_the_terms_a_broker_needs()
    {
        using var fixture = new ProcurementScenario();
        await SetProductOriginAsync(fixture, "Germany");
        var (draft, lineId) = await DraftAsync(fixture, "terms-read",
            incoterm: "CIF", portOfLoading: "Hamburg", portOfDischarge: "Jeddah Islamic Port");
        await fixture.Execute(service => service.AmendPurchaseOrderTradeTermsAsync(
            Amend(fixture, draft.Id, "terms-read-cmd", draft.Version,
                lines: [new PurchaseOrderLineTradeTerms(lineId, "7318.15", null)])));

        // Written but not readable is the same as not captured: the buyer has to hand these to a
        // customs broker off the screen.
        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));

        var order = Assert.Single(workbench.PurchaseOrders);
        Assert.Equal("CIF", order.Incoterm);
        Assert.Equal("Hamburg", order.PortOfLoading);
        Assert.Equal("Jeddah Islamic Port", order.PortOfDischarge);
        var line = Assert.Single(order.Lines);
        Assert.Equal("7318.15", line.HsCode);
        Assert.Equal("Germany", line.CountryOfOrigin);
    }

    // ---------------------------------------------------------------------------------- helpers

    private static AmendPurchaseOrderTradeTermsCommand Amend(
        ProcurementScenario fixture, long purchaseOrderId, string key, long version,
        string? incoterm = null, string? portOfLoading = null, string? portOfDischarge = null,
        IReadOnlyCollection<PurchaseOrderLineTradeTerms>? lines = null) => new(
        fixture.BusinessUnitId, purchaseOrderId, version, key, Buyer, $"corr-{key}",
        incoterm, portOfLoading, portOfDischarge, lines);

    /// <summary>A DRAFT purchase order and the id of its single line.</summary>
    private static async Task<(PurchaseOrderResult Order, long LineId)> DraftAsync(
        ProcurementScenario fixture, string key, string? incoterm = null,
        string? portOfLoading = null, string? portOfDischarge = null)
    {
        var award = await fixture.CreateAwardAsync(key, quantity: 8m);
        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], $"{key}-po") with
            {
                Incoterm = incoterm,
                PortOfLoading = portOfLoading,
                PortOfDischarge = portOfDischarge
            }));
        return (draft, await fixture.PurchaseOrderLineIdAsync(draft.Id));
    }

    /// <summary>Country of origin lives on the product master and is copied onto the line when the
    /// order is raised, so it has to be set before creation to be observed.</summary>
    private static async Task SetProductOriginAsync(ProcurementScenario fixture, string countryOfOrigin)
    {
        await using var setup = fixture.Context();
        var product = await setup.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.CountryOfOrigin = countryOfOrigin;
        await setup.SaveChangesAsync();
    }

    private static async Task AssertUnchangedAsync(
        ProcurementScenario fixture, long purchaseOrderId, string? incoterm, long version)
    {
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == purchaseOrderId);
        Assert.Equal(incoterm, row.Incoterm);
        Assert.Equal(version, row.Version);
    }

    /// <summary>
    /// The order is untouched AND no amendment was recorded. The version and the event row are
    /// asserted together on purpose: a refusal that still bumps the version breaks the caller's
    /// next optimistic write, and one that still writes an event puts a change in the audit trail
    /// that never happened.
    /// </summary>
    private static async Task AssertNothingWrittenAsync(
        ProcurementScenario fixture, long purchaseOrderId, string? incoterm, long version)
    {
        await AssertUnchangedAsync(fixture, purchaseOrderId, incoterm, version);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.ProcurementEvents
            .Where(x => x.EventType == "SUPPLIER_PO_TRADE_TERMS_AMENDED").ToListAsync());
    }
}
