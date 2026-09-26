using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// "Why skip line N" offers reasons a LINE is not quoted. It used to be fed from the quote-outcome
/// list, so a rep skipping one valve read "Lost to competitor", "Customer cancelled", "No response"
/// and "Expired automatically" — how a quote ended, offered for a line nobody had quoted yet.
/// </summary>
public sealed class LineSkipReasonsTests
{
    private static OutcomeReasonDto Outcome(string code, string label) => new() { Code = code, Label = label };

    [Fact]
    public void Quote_outcome_words_are_not_offered_as_reasons_to_skip_a_line()
    {
        var composed = LeadDecisionWorkbenchService.ComposeReasonCodes(
            LineSkipReasons.Baseline,
            [
                Outcome("PRICE", "Price too high"), Outcome("LEAD_TIME", "Lead time too long"),
                Outcome("NO_STOCK", "Item unavailable"), Outcome("LOST_COMPETITOR", "Lost to competitor"),
                Outcome("CUSTOMER_CANCELLED", "Customer cancelled"), Outcome("NO_RESPONSE", "No response"),
                Outcome("AUTO_EXPIRED", "Expired automatically"), Outcome("OTHER", "Other")
            ]);

        var forALine = composed.Where(x => x.AppliesTo.Contains("NoBid")).Select(x => x.Label).ToArray();
        Assert.Equal(
            ["Price too high", "Lead time too long", "Item unavailable", "Outside approved product scope", "Other"],
            forALine);
        foreach (var outcomeOnly in new[] { "LOST_COMPETITOR", "CUSTOMER_CANCELLED", "NO_RESPONSE", "AUTO_EXPIRED" })
        {
            var reason = Assert.Single(composed, x => x.Code == outcomeOnly);
            Assert.DoesNotContain("NoBid", reason.AppliesTo);
            Assert.Contains("Decline", reason.AppliesTo);
        }
    }

    [Fact]
    public void A_reason_on_both_lists_is_one_reason_with_both_uses()
    {
        var composed = LeadDecisionWorkbenchService.ComposeReasonCodes(
            LineSkipReasons.Baseline, [Outcome("PRICE", "Price too high"), Outcome("NO_RESPONSE", "No response")]);

        var price = Assert.Single(composed, x => x.Code == "PRICE");
        Assert.Equal(["Decline", "NoBid"], price.AppliesTo.Order());
        // The whole-request decline still has the outcome list to pick from.
        Assert.Contains(composed, x => x.Code == "NO_RESPONSE" && x.AppliesTo.SequenceEqual(["Decline"]));
        // Line-skip-only words never reach the whole-request decline.
        var scope = Assert.Single(composed, x => x.Code == "OUT_OF_SCOPE");
        Assert.Equal(["NoBid"], scope.AppliesTo);
    }

    [Fact]
    public async Task A_tenant_reads_its_own_list_and_a_tenant_without_one_reads_the_baseline()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            Seed.BusinessUnit(seed, 2);
            seed.SetupMasters.Add(new SetupMaster
            {
                SetupType = LineSkipReasons.SetupType, SetupCode = "NO_CERT", SetupValue = "No certificate",
                Description = "Cannot supply the certificate asked for", BusinessUnitId = 1, IsActive = true,
                CreatedBy = "admin", CreatedOn = DateTime.UtcNow
            });
            seed.SetupMasters.Add(new SetupMaster
            {
                SetupType = LineSkipReasons.SetupType, SetupCode = "RETIRED", SetupValue = "Retired",
                BusinessUnitId = 1, IsActive = false, CreatedBy = "admin", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var reasons = new LineSkipReasons(context);

        var shaped = await reasons.GetAsync(1);
        var only = Assert.Single(shaped);
        Assert.Equal(("NO_CERT", "Cannot supply the certificate asked for"), (only.Code, only.Label));
        Assert.True(await reasons.IsGovernedAsync(1, "no_cert"));
        Assert.False(await reasons.IsGovernedAsync(1, "RETIRED"));
        Assert.False(await reasons.IsGovernedAsync(1, "PRICE"));

        // Tenant 2 has no rows yet: it reads the baseline, and never tenant 1's list.
        Assert.Equal(LineSkipReasons.Baseline.Select(x => x.Code), (await reasons.GetAsync(2)).Select(x => x.Code));
        Assert.True(await reasons.IsGovernedAsync(2, "OUT_OF_SCOPE"));
        Assert.False(await reasons.IsGovernedAsync(2, "NO_CERT"));
    }
}
