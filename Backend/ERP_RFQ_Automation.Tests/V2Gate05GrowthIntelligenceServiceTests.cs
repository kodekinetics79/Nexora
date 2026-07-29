using ERP_RFQ_Automation.CommercialIntelligence.Growth;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class V2Gate05GrowthIntelligenceServiceTests
{
    private static readonly DateTime AsOf = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Percent_and_trend_do_not_invent_missing_denominators()
    {
        Assert.Null(GrowthIntelligenceRules.Percent(0, 0));
        Assert.Null(GrowthIntelligenceRules.Percent(3, 0));
        Assert.Null(GrowthIntelligenceRules.TrendPercent(8, 0));
        Assert.Equal(60m, GrowthIntelligenceRules.Percent(3, 5));
        Assert.Equal(25m, GrowthIntelligenceRules.TrendPercent(10, 8));
    }

    [Fact]
    public void First_response_cohort_includes_missing_and_slow_cases()
    {
        var assigned = AsOf.AddHours(-30);
        var rows = GrowthIntelligenceRules.MissingOrSlowFirstResponses(
        [
            new FirstResponseObservation(1, 10, assigned, null),
            new FirstResponseObservation(2, 10, assigned, assigned.AddHours(25)),
            new FirstResponseObservation(3, 10, assigned, assigned.AddHours(2)),
            new FirstResponseObservation(4, 10, AsOf.AddHours(-2), null)
        ], AsOf);

        Assert.Equal([1L, 2L], rows.Select(x => x.AssignmentId).ToArray());
        Assert.False(GrowthIntelligenceRules.IsQualifyingFirstResponse(CommercialActivityType.Note));
        Assert.False(GrowthIntelligenceRules.IsQualifyingFirstResponse(CommercialActivityType.CustomerResponded));
        Assert.True(GrowthIntelligenceRules.IsQualifyingFirstResponse(CommercialActivityType.Call));
    }

    [Fact]
    public void Stable_keys_are_order_independent_and_change_with_source_evidence()
    {
        EvidenceFact[] evidence =
        [
            new("COUNT", "2", "Rfq", 20, AsOf.AddDays(-1)),
            new("STATUS", "OPEN", "Rfq", 20, AsOf.AddDays(-1))
        ];
        var firstVersion = GrowthIntelligenceRules.SourceVersion(evidence);
        var reorderedVersion = GrowthIntelligenceRules.SourceVersion(evidence.Reverse());
        var firstKey = GrowthIntelligenceRules.StableFindingKey(9, "NO_NEXT_ACTION", 30, "Rfq", 20,
            firstVersion);
        var secondKey = GrowthIntelligenceRules.StableFindingKey(9, "NO_NEXT_ACTION", 30, "Rfq", 20,
            reorderedVersion);
        var changedVersion = GrowthIntelligenceRules.SourceVersion(
            [evidence[0] with { Value = "3" }, evidence[1]]);

        Assert.Equal(64, firstKey.Length);
        Assert.Equal(firstVersion, reorderedVersion);
        Assert.Equal(firstKey, secondKey);
        Assert.NotEqual(firstKey, GrowthIntelligenceRules.StableFindingKey(9, "NO_NEXT_ACTION", 30,
            "Rfq", 20, changedVersion));
    }

    [Theory]
    [InlineData("Lead", 11, "/procurement/leads/view/11")]
    [InlineData("Rfq", 12, "/procurement/rfqs/view/12")]
    [InlineData("Quote", 13, "/sales/quotes/view/13")]
    [InlineData("Customer", 14, "/customers/14")]
    [InlineData("SalesRep", 15, "/sales/reps/15")]
    [InlineData("SupplierQuote", 16, "/procurement/supplier-quotes/16")]
    public void Drill_down_routes_use_canonical_normal_application_paths(string aggregateType, long aggregateId,
        string expected) => Assert.Equal(expected, GrowthIntelligenceRules.Route(aggregateType, aggregateId));

    [Fact]
    public void Dormant_high_value_requires_sufficient_single_currency_cohort()
    {
        var rows = Enumerable.Range(1, 5).Select(id => new DormantCustomerObservation(id, 1,
            id * 100m, AsOf.AddDays(-100))).ToArray();

        var selected = GrowthIntelligenceRules.SelectDormantHighValueCustomers(rows, AsOf);

        Assert.Equal([4L, 5L], selected.Select(x => x.CustomerId).ToArray());
        Assert.Empty(GrowthIntelligenceRules.SelectDormantHighValueCustomers(rows.Take(4), AsOf));
    }

    [Fact]
    public void Dormant_high_value_omits_multi_currency_and_recent_customers()
    {
        DormantCustomerObservation[] rows =
        [
            new(1, 1, 900m, AsOf.AddDays(-120)),
            new(1, 2, 100m, AsOf.AddDays(-120)),
            new(2, 1, 800m, AsOf.AddDays(-120)),
            new(3, 1, 700m, AsOf.AddDays(-120)),
            new(4, 1, 600m, AsOf.AddDays(-120)),
            new(5, 1, 500m, AsOf.AddDays(-120)),
            new(6, 1, 1_000m, AsOf.AddDays(-5))
        ];

        var selected = GrowthIntelligenceRules.SelectDormantHighValueCustomers(rows, AsOf);

        Assert.DoesNotContain(selected, x => x.CustomerId is 1 or 6);
        Assert.Contains(selected, x => x.CustomerId == 2);
    }

    [Fact]
    public void Acknowledgement_snapshot_is_append_only_and_identical_replay_is_detected()
    {
        var finding = Finding();
        var command = Command(finding.FindingKey);
        var requestHash = GrowthIntelligenceRules.AcknowledgementRequestHash(command, finding);
        var acknowledgement = GrowthIntelligenceRules.CreateAcknowledgement(9, command, finding, requestHash);

        Assert.Equal("ACKNOWLEDGED", acknowledgement.DecisionCode);
        Assert.Equal(GrowthIntelligencePolicy.Version, acknowledgement.PolicyVersion);
        Assert.Contains("OVERDUE_FOLLOW_UP", acknowledgement.FindingCode);
        Assert.True(GrowthIntelligenceRules.IsIdempotentReplay(acknowledgement, requestHash));
        Assert.False(GrowthIntelligenceRules.IsIdempotentReplay(acknowledgement,
            new string('a', 64)));
    }

    [Fact]
    public void Acknowledgement_rejects_a_stale_finding_key()
    {
        var finding = Finding();
        var command = Command(new string('f', 64));
        var requestHash = GrowthIntelligenceRules.AcknowledgementRequestHash(command, finding);

        var error = Assert.Throws<InvalidOperationException>(() =>
            GrowthIntelligenceRules.CreateAcknowledgement(9, command, finding, requestHash));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Health_and_margin_remain_explicit_when_evidence_is_absent()
    {
        var assessment = GrowthIntelligenceRules.AssessCustomerHealth(AsOf, null,
            new FollowUpHealth(0, 0, 0, 0, null), null, null, 0);
        var margin = new MarginAvailability("unavailable", null, 0,
            "No immutable quote-time landed-cost evidence.");

        Assert.Equal("insufficient-evidence", assessment.Band);
        Assert.Equal("unavailable", margin.Status);
        Assert.Null(margin.Percent);
        Assert.Equal(0, margin.SampleSize);
    }

    [Theory]
    [InlineData("PRICE", false)]
    [InlineData("NO_RESPONSE", false)]
    [InlineData("INCORRECT_COMMITMENT", true)]
    [InlineData("UNCLASSIFIED_TENANT_REASON", false)]
    [InlineData(null, false)]
    public void Execution_loss_rule_does_not_blame_external_or_missing_reasons(string? reason, bool expected) =>
        Assert.Equal(expected, GrowthIntelligenceRules.IsExecutionClassifiedLoss(reason));

    [Fact]
    public void Viable_rfq_requires_positive_demand_open_deadline_and_non_terminal_state()
    {
        var rfq = new Rfq { Id = 10, BidClosingDate = AsOf.AddDays(1) };
        Rfqitem[] positiveDemand = [new() { Rfqid = 10, Quantity = 2 }];

        Assert.True(GrowthIntelligenceRules.IsViableRfq(rfq, positiveDemand, "QUOTE_SENT", AsOf));
        Assert.False(GrowthIntelligenceRules.IsViableRfq(rfq, positiveDemand, "LOST", AsOf));
        Assert.False(GrowthIntelligenceRules.IsViableRfq(rfq, [new() { Rfqid = 10, Quantity = 0 }], null, AsOf));
        rfq.BidClosingDate = AsOf.AddSeconds(-1);
        Assert.False(GrowthIntelligenceRules.IsViableRfq(rfq, positiveDemand, null, AsOf));
    }

    [Fact]
    public void Quote_follow_up_excludes_authoritative_terminal_states_but_not_sent_state()
    {
        var open = new Quote { ValidUntil = AsOf.AddDays(1), Status = new SetupMaster { SetupCode = "SENT" } };
        Assert.False(GrowthIntelligenceRules.IsTerminalQuote(open, false, "QUOTE_SENT", AsOf));
        Assert.True(GrowthIntelligenceRules.IsTerminalQuote(open, true, "QUOTE_SENT", AsOf));
        Assert.True(GrowthIntelligenceRules.IsTerminalQuote(
            new Quote { OutcomeOn = AsOf, ValidUntil = AsOf.AddDays(1) }, false, null, AsOf));
        Assert.True(GrowthIntelligenceRules.IsTerminalQuote(
            new Quote { ValidUntil = AsOf.AddSeconds(-1) }, false, null, AsOf));
        Assert.True(GrowthIntelligenceRules.IsTerminalQuote(open, false, "CANCELLED", AsOf));
    }

    [Fact]
    public void Account_follow_up_does_not_suppress_an_rfq_specific_next_action()
    {
        FollowUpTask[] tasks =
        [
            new() { AggregateType = "Customer", AggregateId = 20, CustomerId = 20, Status = FollowUpStatus.Open },
            new() { AggregateType = "Rfq", AggregateId = 99, CustomerId = 20, Status = FollowUpStatus.Completed }
        ];

        Assert.False(GrowthIntelligenceRules.HasRfqSpecificOpenFollowUp(99, tasks));
        tasks[1].Status = FollowUpStatus.InProgress;
        Assert.True(GrowthIntelligenceRules.HasRfqSpecificOpenFollowUp(99, tasks));
    }

    [Theory]
    [InlineData("23505", "IX_sales_coaching_acknowledgements_BusinessUnitId_IdempotencyK~", true)]
    [InlineData("23505", "another_constraint", false)]
    [InlineData("23503", "IX_sales_coaching_acknowledgements_BusinessUnitId_IdempotencyK~", false)]
    public void Only_the_acknowledgement_replay_constraint_is_treated_as_idempotent(
        string sqlState, string constraintName, bool expected) => Assert.Equal(expected,
        GrowthIntelligenceRules.IsIdempotencyUniqueViolation(sqlState, constraintName));

    [Fact]
    public void Acknowledgement_model_has_tenant_filter_and_unique_replay_key()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite("Data Source=:memory:").Options;
        using var context = new ErpRfqAutomationContext(options, new StubTenant(9));
        var entity = context.Model.FindEntityType(typeof(SalesCoachingAcknowledgement));

        Assert.NotNull(entity?.GetQueryFilter());
        Assert.Contains(entity!.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(x => x.Name).SequenceEqual(
                [nameof(SalesCoachingAcknowledgement.BusinessUnitId),
                 nameof(SalesCoachingAcknowledgement.IdempotencyKey)]));
    }

    private static CoachingFinding Finding()
    {
        EvidenceFact[] evidence =
            [new("DUE_AT", AsOf.AddDays(-1).ToString("O"), "FollowUpTask", 81, AsOf.AddDays(-1))];
        var version = GrowthIntelligenceRules.SourceVersion(evidence);
        var key = GrowthIntelligenceRules.StableFindingKey(9, "OVERDUE_FOLLOW_UP", 30,
            "FollowUpTask", 81, version);
        return new CoachingFinding(key, "OVERDUE_FOLLOW_UP", 30, 30, [], "FollowUpTask", 81, version,
            50, "NX-50", 24m, 0m, 1, 1m, AsOf, "Complete the follow-up.", "/sales/today", evidence, null);
    }

    private static AcknowledgeCoachingFindingCommand Command(string findingKey) => new(findingKey,
        "ACKNOWLEDGED", "Reviewed with the account owner.", 40, "ack-1", "corr-1",
        AsOf.AddDays(-30), AsOf, AsOf, 30);

}
