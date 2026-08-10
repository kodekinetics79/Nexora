using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-COM-04. The customer-PO tolerances are a control that controls something.
///
/// The defect
/// ----------
/// <c>PriceTolerancePercent</c>, <c>PriceToleranceMinimumAmount</c> and
/// <c>QuantityTolerancePercent</c> were settable through the Commercial Policy screen, persisted,
/// versioned and audited — and read by nothing. The only comparison in the award path was exact
/// decimal equality (<c>line.UnitPrice.Value != quoteLine.UnitPrice</c>), so a manager could set 2%,
/// be told it saved, and every sub-halalah rounding difference still raised
/// <c>PRICE_DISCREPANCY</c>. That is the wiring contract's failure #5 and #7 together: a setting
/// with no reader, shown to the user as a working control.
///
/// What these tests prove
/// ----------------------
/// Not that the number round-trips — <c>CommercialPolicySettingsTests</c> already covers that.
/// These assert the DEPENDENCE: the discrepancy report changes its answer about the SAME purchase
/// order when the manager changes the policy. Reverting either comparison to <c>!=</c> fails
/// <see cref="Price_inside_the_configured_tolerance_is_not_a_discrepancy"/> immediately, and
/// hardcoding any tolerance in the comparison fails
/// <see cref="Tolerance_of_zero_restores_exact_equality"/>.
/// </summary>
public sealed class CustomerPoToleranceTests
{
    private const long Actor = 7_701;
    private const decimal QuotedUnitPrice = 100m;
    private const decimal QuotedQuantity = 10m;

    [Fact]
    public async Task Price_inside_the_configured_tolerance_is_not_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-inside", priceTolerancePercent: 2m);

        // 101.50 against a quoted 100.00 is 1.5% — inside the 2% the manager configured.
        var match = await MatchWithPoPriceAsync(fixture, "inside", 101.50m);

        var line = Assert.Single(match.Lines);
        Assert.DoesNotContain("PRICE_DISCREPANCY", line.Differences);
        Assert.Empty(line.Differences);
        Assert.Equal("EXACT_MATCH", line.MatchStatus);
        Assert.Equal(0, match.Header.DiscrepancyCount);
        Assert.Equal("EXACT_ACCEPTANCE", match.Header.MatchOutcome);
    }

    [Fact]
    public async Task Price_outside_the_configured_tolerance_is_still_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-outside", priceTolerancePercent: 2m);

        // 105.00 is 5% over. A tolerance is not an instruction to accept a different deal.
        var match = await MatchWithPoPriceAsync(fixture, "outside", 105m);

        var line = Assert.Single(match.Lines);
        Assert.Contains("PRICE_DISCREPANCY", line.Differences);
        Assert.Equal("DISCREPANCY", line.MatchStatus);
        Assert.Equal(1, match.Header.DiscrepancyCount);
        Assert.Equal("ACCEPTED_WITH_DIFFERENCES", match.Header.MatchOutcome);
    }

    /// <summary>
    /// Direction. The tolerance absorbs a difference of either sign, because its stated purpose —
    /// on the entity and on the settings screen — is rounding, and rounding has no direction. A
    /// one-sided rule would leave half the noise in place and would contradict its own label.
    /// </summary>
    [Fact]
    public async Task Tolerance_absorbs_a_customer_po_priced_below_the_quote_as_well_as_above()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-below", priceTolerancePercent: 2m);

        var match = await MatchWithPoPriceAsync(fixture, "below", 98.50m);

        Assert.Empty(Assert.Single(match.Lines).Differences);
    }

    /// <summary>
    /// The proof that the comparison READS the policy rather than carrying a constant of its own:
    /// the identical purchase order that was clean at 2% is a discrepancy at 0%.
    /// </summary>
    [Fact]
    public async Task Tolerance_of_zero_restores_exact_equality()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-zero", priceTolerancePercent: 0m);

        var match = await MatchWithPoPriceAsync(fixture, "zero", 101.50m);

        Assert.Contains("PRICE_DISCREPANCY", Assert.Single(match.Lines).Differences);
    }

    /// <summary>
    /// The absolute floor is a second, independent allowance: with the percentage at zero, a
    /// difference under the configured minimum is still rounding.
    /// </summary>
    [Fact]
    public async Task Minimum_amount_tolerates_a_difference_the_percentage_would_not()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-min", priceTolerancePercent: 0m, priceToleranceMinimumAmount: 2m);

        var match = await MatchWithPoPriceAsync(fixture, "min", 101.50m);

        Assert.Empty(Assert.Single(match.Lines).Differences);
    }

    /// <summary>
    /// The quantity tolerance is its own number and defaults to zero — a quantity is a count the
    /// buyer stated. A tenant trading in fractional units may widen it, and when it does the
    /// comparison must obey that too rather than the price tolerance or exact equality.
    /// </summary>
    [Fact]
    public async Task Quantity_inside_its_own_tolerance_is_not_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-qty", priceTolerancePercent: 2m, quantityTolerancePercent: 5m);

        // Ordered 10, awarded 9.6: a 0.4 difference against a 0.5 allowance.
        var match = await MatchWithPoPriceAsync(fixture, "qty", QuotedUnitPrice, awardedQuantity: 9.6m);

        var line = Assert.Single(match.Lines);
        Assert.DoesNotContain("QUANTITY_DISCREPANCY", line.Differences);
        // Still reported as a partial award: the 0.4 left on the quotation is a decision someone
        // made, not noise, and the tolerance is not allowed to close it.
        Assert.Contains("PARTIAL_QUOTE_AWARD", line.Differences);
    }

    [Fact]
    public async Task Quantity_outside_its_own_tolerance_is_still_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-qty-out", priceTolerancePercent: 2m, quantityTolerancePercent: 5m);

        var match = await MatchWithPoPriceAsync(fixture, "qty-out", QuotedUnitPrice, awardedQuantity: 8m);

        Assert.Contains("QUANTITY_DISCREPANCY", Assert.Single(match.Lines).Differences);
    }

    /// <summary>The inbox counts discrepancies on the same tolerances the match screen applies.</summary>
    [Fact]
    public async Task Inbox_row_counts_discrepancies_on_the_same_tolerance()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPolicyAsync(fixture, "tol-inbox", priceTolerancePercent: 2m);
        await MatchWithPoPriceAsync(fixture, "inbox", 101.50m);

        var rows = await fixture.Service.SearchPurchaseOrdersAsync(fixture.BusinessUnitId, null, 50);

        Assert.Equal(0, Assert.Single(rows).DiscrepancyCount);
    }

    [Theory]
    // Exact equality is inside every tolerance, including a tolerance of nothing at all.
    [InlineData(100, 100, 0, 0, true)]
    [InlineData(101.50, 100, 2, 0, true)]
    [InlineData(98.50, 100, 2, 0, true)]
    [InlineData(102.01, 100, 2, 0, false)]
    [InlineData(97.99, 100, 2, 0, false)]
    // A percentage alone misbehaves at small values; the minimum amount is what covers them.
    [InlineData(0.11, 0.10, 2, 0, false)]
    [InlineData(0.11, 0.10, 2, 0.01, true)]
    // A negative allowance written by direct SQL cannot turn a tolerance into a trigger.
    [InlineData(100, 100, -5, -5, true)]
    public void Within_is_symmetric_and_takes_the_wider_of_percentage_and_minimum(
        double actual, double reference, double percent, double minimum, bool expected)
        => Assert.Equal(expected, CommercialMatchingTolerance.Within(
            (decimal)actual, (decimal)reference, (decimal)percent, (decimal)minimum));

    /// <summary>
    /// Sets the tenant's tolerances through the same service the settings screen posts to, so the
    /// chain under test is the manager's edit, not a hand-written row.
    /// </summary>
    private static async Task SetPolicyAsync(CustomerAwardTestFixture fixture, string key,
        decimal priceTolerancePercent, decimal? priceToleranceMinimumAmount = null,
        decimal? quantityTolerancePercent = null)
    {
        await new CommercialMatchingPolicyService(fixture.Context).UpdateAsync(
            fixture.BusinessUnitId, Actor, "policy@example.com", key,
            new UpdateCommercialMatchingPolicyCommand(
                SupplierInputTaxRecoverablePercent: null,
                OutputTaxRatePercent: null,
                ClearOutputTaxRate: false,
                PriceTolerancePercent: priceTolerancePercent,
                PriceToleranceMinimumAmount: priceToleranceMinimumAmount,
                QuantityTolerancePercent: quantityTolerancePercent,
                Reason: "Buyer ERP rounds unit prices to two places; absorb the rounding."));
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Captures a customer PO at <paramref name="poUnitPrice"/> against the seeded quotation, awards
    /// it, and returns the match view the reviewer looks at.
    /// </summary>
    private static async Task<ClientPurchaseOrderMatchView> MatchWithPoPriceAsync(
        CustomerAwardTestFixture fixture, string key, decimal poUnitPrice, decimal? awardedQuantity = null)
    {
        var command = fixture.PurchaseOrderCommand($"PO-{key}", QuotedQuantity) with
        {
            // Nothing here is defaulted from our own quote line: the comparison is only meaningful
            // while the PO side is what the buyer actually wrote.
            Lines = [new("1", null, "Buyer widget", QuotedQuantity, null, poUnitPrice,
                QuotedQuantity * poUnitPrice)]
        };
        var purchaseOrder = await fixture.Service.CreatePurchaseOrderAsync(
            fixture.BusinessUnitId, $"po-{key}", $"corr-po-{key}", command, "tests");
        await fixture.CreateAwardAsync(purchaseOrder, $"award-{key}", awardedQuantity ?? QuotedQuantity);
        fixture.Context.ChangeTracker.Clear();
        return await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId, purchaseOrder.Id);
    }
}
