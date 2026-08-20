using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// "Upload the client PO and hook it to the quote."
///
/// The defect
/// ----------
/// <para>The hook was already being written and was not being read. <c>CreatePurchaseOrderAsync</c>
/// stamps <c>CustomerPurchaseOrder.QuoteId</c> and <c>RfqId</c> the moment the buyer's document is
/// captured, with a comment saying why: "so the matcher can reach the quotation without going
/// through an award that may not exist yet". <c>ProposePurchaseOrderMatchesAsync</c> honours that.
/// But the two projections a human actually looks at — <c>SearchPurchaseOrdersAsync</c> behind the
/// Client PO Inbox and <c>GetPurchaseOrderMatchAsync</c> behind the review screen — resolved the
/// quotation solely as <c>award?.QuoteId, award?.Quote.QuoteNo</c>. Before an allocation existed
/// the inbox printed "Quote match pending" and the review screen hid its "Customer Quote" button,
/// for a purchase order whose row in the database names the quotation outright.</para>
///
/// <para>That state is ordinary, not exotic. <c>CustomerAwardWorkspace</c> submits four sequential
/// requests — create PO, create award, confirm, convert — so any refusal after the first (the R17
/// tax gate, a stale quote revision, an over-allocation) leaves a saved purchase order whose only
/// record of what the buyer was answering is the column nothing displayed.</para>
///
/// What these tests pin
/// --------------------
/// <para>The SEAM, not the step: that the identity of the quotation survives onto the buyer's
/// document and then onto the sales order, and that the product can say so. Every purchase order
/// here is produced by the real <c>CustomerAwardApplicationService</c> write path from the fixture's
/// quotation — no test builds a purchase order and a quotation and then asserts they agree, which
/// would pass with the join cut.</para>
/// </summary>
public sealed class ClientPoQuoteAttachmentSeamTests
{
    private const decimal Quantity = 10m;

    /// <summary>
    /// The regression. A client PO uploaded against a quotation names that quotation on both
    /// screens BEFORE anyone has allocated a single line.
    ///
    /// <para>Deliberately no award: with one, the assertion would pass on the broken code too.
    /// This is the whole difference between "the link exists" and "the product admits it exists".</para>
    /// </summary>
    [Fact]
    public async Task An_uploaded_client_po_names_its_quotation_before_any_award_exists()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("attach-no-award", Quantity);
        fixture.Context.ChangeTracker.Clear();

        var quote = await fixture.Context.Quotes.AsNoTracking().SingleAsync(x => x.Id == fixture.QuoteId);
        var row = Assert.Single(await fixture.Service.SearchPurchaseOrdersAsync(
            fixture.BusinessUnitId, "PO-attach-no-award", 20));
        var match = await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId, purchaseOrder.Id);

        Assert.Equal(fixture.QuoteId, row.QuoteId);
        Assert.Equal(quote.QuoteNo, row.QuoteNumber);
        Assert.Equal(fixture.QuoteId, match.Header.QuoteId);
        Assert.Equal(quote.QuoteNo, match.Header.QuoteNumber);

        // Still unallocated, and still saying so. Naming the quotation is not the same claim as
        // having matched it line by line, and the review outcome must not soften because of it.
        Assert.Equal("POSSIBLE_MATCH_REVIEW", row.MatchOutcome);
        Assert.Null(match.AwardId);
        Assert.Equal("REVIEW_REQUIRED", Assert.Single(match.Lines).MatchStatus);
    }

    /// <summary>
    /// The spine. The commercial case and the Nexora Serial that the lead allocated reach the
    /// buyer's purchase order and then the sales order raised from it, without anyone restating
    /// them.
    ///
    /// <para>The serial is asserted as a STRING against the quotation's own, not as "some serial is
    /// present": a purchase order filed under a different case would still have a serial, and that
    /// is exactly the failure the Postgres trigger
    /// <c>nexora_validate_downstream_commercial_identity</c> exists to refuse in production. This
    /// suite runs on SQLite, where that trigger does not exist — so the domain-level carriage is
    /// what has to be proven here.</para>
    /// </summary>
    [Fact]
    public async Task The_quotations_case_and_serial_reach_the_client_po_and_then_the_sales_order()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("attach-spine", Quantity);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "attach-spine-award", Quantity);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "attach-spine-confirm", "corr-attach-spine-confirm", new(award.Version), "tests");
        var converted = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "attach-spine-convert", "corr-attach-spine-convert", new(confirmed.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var quote = await fixture.Context.Quotes.AsNoTracking().SingleAsync(x => x.Id == fixture.QuoteId);
        var storedPo = await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
            .Include(x => x.CommercialCase)
            .SingleAsync(x => x.Id == purchaseOrder.Id);
        var order = await fixture.Context.Orders.AsNoTracking().SingleAsync(x => x.Id == converted.Id);

        // SEAM: the buyer's document was filed under the quotation's case, and the serial it shows
        // on screen is the quotation's serial rather than one of its own.
        Assert.Equal(quote.CommercialCaseId, storedPo.CommercialCaseId);
        Assert.Equal(quote.NexoraSerial, storedPo.CommercialCase.MasterReference);
        Assert.Equal(quote.Id, storedPo.QuoteId);
        Assert.Equal(quote.Rfqid, storedPo.RfqId);

        // SEAM: Order.InheritCommercialIdentity(award.Quote). Cut it and the sales order is a
        // priced customer document standing outside the spine, which is what FR-COM-07 forbids.
        Assert.Equal(quote.CommercialCaseId, order.CommercialCaseId);
        Assert.Equal(quote.NexoraSerial, order.NexoraSerial);
        Assert.Equal(quote.Id, order.QuoteId);
        Assert.Equal(award.Id, order.CustomerAwardId);
        Assert.Equal(OrderSourceTypes.CustomerAward, order.SourceType);

        // And the same identity is what the inbox reports back, so the screen and the record agree.
        var row = Assert.Single(await fixture.Service.SearchPurchaseOrdersAsync(
            fixture.BusinessUnitId, "PO-attach-spine", 20));
        Assert.Equal(quote.NexoraSerial, row.NexoraSerial);
        Assert.Equal(converted.Id, row.CustomerOrderId);
    }

    /// <summary>
    /// The guard that makes "attach it to the quote" mean something. A purchase order may not be
    /// filed against a quotation whose commercial case, customer or currency is not its own.
    ///
    /// <para>Pinned because the fix above makes the stored <c>QuoteId</c> load-bearing on screen: a
    /// future "just let them pick any quote" would now mislabel a purchase order's lineage rather
    /// than merely storing an unused column. This is <c>ValidateQuoteIdentity</c>, and it is the
    /// application-level half of the database trigger that refuses a downstream document whose
    /// (case, serial, customer, contact) do not match its parent.</para>
    /// </summary>
    [Fact]
    public async Task A_client_po_cannot_be_filed_against_a_quotation_it_does_not_share_a_case_with()
    {
        using var fixture = new CustomerAwardTestFixture();
        var honest = fixture.PurchaseOrderCommand("PO-ATTACH-GUARD", Quantity);

        var wrongCase = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId, "attach-guard-case",
                "corr-attach-guard-case", honest with { CommercialCaseId = honest.CommercialCaseId + 1 }, "tests"));
        Assert.Contains("commercial case", wrongCase.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId, "attach-guard-customer",
                "corr-attach-guard-customer", honest with { CustomerId = honest.CustomerId + 1 }, "tests"));

        // Nothing was written on the way to being refused.
        Assert.Empty(await fixture.Context.CustomerPurchaseOrders.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Precedence, which is the subtle half of the fix. When an award exists it names the
    /// quotation, even though the purchase order carries a quotation id of its own.
    ///
    /// <para>The two can legitimately disagree: a buyer sends a PO against revision 1, sales issues
    /// revision 2, and the allocation is made against revision 2 because
    /// <c>LoadEligibleQuoteAsync</c> refuses a superseded quote. Everything else in the projection —
    /// the matched quote lines, the price and unit differences, the keys that block conversion — is
    /// computed from the AWARD's quotation. A header naming revision 1 beside lines compared to
    /// revision 2 would be worse than a header naming nothing at all.</para>
    ///
    /// <para>So this is not a preference; it is the rule that keeps the header and the body of the
    /// same screen talking about one document.</para>
    /// </summary>
    [Fact]
    public async Task An_award_against_a_later_revision_wins_over_the_purchase_orders_own_quote_link()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("attach-revision", Quantity);
        var (revisionId, revisionNo, revisionItemId, revisionNumber) = await SuperseidQuoteAsync(fixture);

        var award = await fixture.Service.CreateAwardAsync(fixture.BusinessUnitId, "attach-revision-award",
            "corr-attach-revision-award",
            new(purchaseOrder.Id, revisionId, 0, purchaseOrder.Version, revisionNo,
                [new(purchaseOrder.Lines.Single().Id, revisionItemId, Quantity)]), "tests");

        fixture.Context.ChangeTracker.Clear();
        var row = Assert.Single(await fixture.Service.SearchPurchaseOrdersAsync(
            fixture.BusinessUnitId, "PO-attach-revision", 20));
        var match = await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId, purchaseOrder.Id);

        Assert.Equal(revisionId, award.QuoteId);
        Assert.Equal(revisionId, row.QuoteId);
        Assert.Equal(revisionNumber, row.QuoteNumber);
        Assert.Equal(revisionId, match.Header.QuoteId);
        // The purchase order still remembers what the buyer was answering; the header just does not
        // report it while a stronger statement exists.
        Assert.Equal(fixture.QuoteId,
            (await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
                .SingleAsync(x => x.Id == purchaseOrder.Id)).QuoteId);
    }

    /// <summary>
    /// Issues revision 2 of the fixture's quotation the way production does: a new quote on the same
    /// RFQ, taking its commercial identity from its predecessor through
    /// <c>Quote.InheritCommercialIdentity(Quote)</c> rather than being handed a case by the test,
    /// and pointing back at the quote it supersedes so <c>LoadEligibleQuoteAsync</c> refuses the old
    /// one.
    /// </summary>
    private static async Task<(long Id, int RevisionNo, long QuoteItemId, string QuoteNo)> SuperseidQuoteAsync(
        CustomerAwardTestFixture fixture)
    {
        var source = await fixture.Context.Quotes
            .Include(x => x.QuoteItems)
            .SingleAsync(x => x.Id == fixture.QuoteId);
        var sourceItem = source.QuoteItems.Single(x => x.Id == fixture.QuoteItemId);
        var revision = new Quote
        {
            Id = fixture.QuoteId + 5_000,
            QuoteNo = $"{source.QuoteNo}-R2",
            Rfqid = source.Rfqid,
            CustomerId = source.CustomerId,
            BusinessUnitId = source.BusinessUnitId,
            QuoteDate = source.QuoteDate,
            StatusId = source.StatusId,
            CurrencyId = source.CurrencyId,
            TotalAmount = source.TotalAmount,
            CreatedBy = "tests",
            CreatedDate = source.CreatedDate,
            RevisionNo = source.RevisionNo + 1,
            RevisionOfQuoteId = source.Id,
        };
        revision.InheritCommercialIdentity(source);
        revision.QuoteItems.Add(new QuoteItem
        {
            Id = sourceItem.Id + 5_000,
            ProductId = sourceItem.ProductId,
            ItemDescription = sourceItem.ItemDescription,
            Quantity = sourceItem.Quantity,
            UnitPrice = sourceItem.UnitPrice,
            Discount = sourceItem.Discount,
            TaxAmount = sourceItem.TaxAmount,
            TaxRatePercentApplied = sourceItem.TaxRatePercentApplied,
            TotalAmount = sourceItem.TotalAmount,
            CreatedBy = "tests",
            CreatedDate = sourceItem.CreatedDate,
        });
        fixture.Context.Quotes.Add(revision);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        return (revision.Id, revision.RevisionNo, sourceItem.Id + 5_000, revision.QuoteNo);
    }
}
