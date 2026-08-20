using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the RFQ screen tells a salesperson has to be a sentence about the RFQ, and it has to be
/// consistent with the other things the same screen says.
///
/// Two defects are pinned here. The first is a status code rendered as prose: the recommendation
/// read "0 blockers remain and the customer deadline is deadline not recorded", produced by
/// interpolating the SLA vocabulary with its underscores swapped for spaces, and it reported a
/// blocker count when the real cause was that no line was left to quote. The second is a DTO
/// field that was declared and never populated: RfqResponseDTO.Readiness derives from ItemCount,
/// which only the list projection ever set, so the detail endpoint answered "Review Required" for
/// every RFQ in existence.
/// </summary>
public sealed class RecommendationProseTests
{
    [Fact]
    public void Sla_risk_is_rendered_as_a_sentence_not_as_its_code()
    {
        // The regression: none of these may read back as the code with its underscores removed.
        Assert.Equal("the enquiry stated no customer deadline",
            CommercialLearningRules.SlaRiskSentence("DEADLINE_NOT_RECORDED"));
        Assert.Equal("the customer deadline has already passed",
            CommercialLearningRules.SlaRiskSentence("OVERDUE"));
        Assert.Equal("the customer deadline is less than 24 hours away",
            CommercialLearningRules.SlaRiskSentence("AT_RISK"));
        Assert.Equal("the customer deadline is still ahead",
            CommercialLearningRules.SlaRiskSentence("ON_TRACK"));

        // A code nobody mapped names itself rather than asserting something false about the
        // deadline, so the next addition to the vocabulary is visible instead of silently wrong.
        Assert.Contains("not recognised", CommercialLearningRules.SlaRiskSentence("SOMETHING_NEW"));
        Assert.Contains("SOMETHING_NEW", CommercialLearningRules.SlaRiskSentence("SOMETHING_NEW"));
    }

    [Theory]
    [InlineData(1, "1 blocker remains and the customer deadline has already passed.")]
    [InlineData(4, "4 blockers remain and the customer deadline has already passed.")]
    public void Blocker_counts_agree_in_number(int blockers, string expected) =>
        Assert.Equal(expected, CommercialLearningRules.NoQuoteReviewExplanation(3, 3, blockers, "OVERDUE"));

    [Fact]
    public void No_quote_review_names_the_condition_that_produced_it()
    {
        // Reached via lines.Length == 0, which is what the live RFQ hit. The old copy reported
        // "0 blockers remain" — true, and completely beside the point.
        var nothingToQuote = CommercialLearningRules.NoQuoteReviewExplanation(0, 21, 0, "DEADLINE_NOT_RECORDED");
        Assert.Equal("None of the 21 RFQ lines is marked for quote, so there is nothing to price, "
            + "and the enquiry stated no customer deadline.", nothingToQuote);

        // Reached via the deadline instead, with the lines perfectly fine.
        Assert.Equal("Nothing is blocking the 7 lines being quoted, but the customer deadline has already passed.",
            CommercialLearningRules.NoQuoteReviewExplanation(7, 21, 0, "OVERDUE"));

        Assert.Equal("This RFQ has no lines to price, and the enquiry stated no customer deadline.",
            CommercialLearningRules.NoQuoteReviewExplanation(0, 0, 0, "DEADLINE_NOT_RECORDED"));
    }

    [Fact]
    public void Ready_explanation_states_the_lines_it_actually_judged()
    {
        // The partial bid. "Every RFQ line has a current evidence-backed fulfilment route" claimed
        // 21 lines while asserting over 7, which is exactly what made the tile row look like it
        // contradicted the readiness sentence.
        Assert.Equal("All 7 lines marked for quote have a current evidence-backed fulfilment route. "
            + "The remaining 14 lines are not being quoted.",
            CommercialLearningRules.ViableReadyExplanation(7, 21, true));

        // Nothing triaged yet: the service falls back to every non-declined line, so the sentence
        // must not claim a marked-for-quote scope that does not exist.
        Assert.Equal("All 21 RFQ lines have a current evidence-backed fulfilment route.",
            CommercialLearningRules.ViableReadyExplanation(21, 21, false));

        Assert.Equal("The single RFQ line has a current evidence-backed fulfilment route.",
            CommercialLearningRules.ViableReadyExplanation(1, 1, false));
        Assert.Equal("The 1 line marked for quote has a current evidence-backed fulfilment route. "
            + "The remaining 5 lines are not being quoted.",
            CommercialLearningRules.ViableReadyExplanation(1, 6, true));
    }

    [Fact]
    public void Recover_coverage_does_not_ask_for_zero_blocked_lines()
    {
        Assert.Equal("Resolve 1 blocked line of the 3 lines being quoted before preparing the customer quote.",
            CommercialLearningRules.RecoverCoverageExplanation(1, 0, 3));
        Assert.Equal("Resolve 5 blocked lines of the 9 lines being quoted before preparing the customer quote.",
            CommercialLearningRules.RecoverCoverageExplanation(5, 0, 9));

        // An RFQ can reach this decision on header evidence alone — no customer identity, no
        // lineage — with every line clean. "Resolve 0 blocked lines" was the old output.
        Assert.Equal("2 RFQ-level blockers need attention before quoting.",
            CommercialLearningRules.RecoverCoverageExplanation(0, 2, 4));
    }

    [Fact]
    public async Task Rfq_with_every_line_declined_is_told_why_in_prose()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var line = await setup.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            line.DecideParticipation(Rfqitem.ParticipationNoQuote, "Out of scope for this bid", "qa", DateTime.UtcNow);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var result = await new CommercialLearningService(context)
            .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);

        Assert.Equal("NO_QUOTE_REVIEW", result.CommercialDecision);
        Assert.Equal("DEADLINE_NOT_RECORDED", result.SlaRisk);

        var explanation = result.NextBestAction.Explanation;
        // The exact string the live tenant showed, and the shape that produced it.
        Assert.DoesNotContain("deadline not recorded", explanation);
        Assert.DoesNotContain("0 blockers", explanation);
        Assert.Contains("marked for quote", explanation);
        Assert.Contains("the enquiry stated no customer deadline", explanation);
    }

    [Fact]
    public async Task Rfq_detail_reports_readiness_from_the_lines_it_returned()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var lead = Seed.Lead(context, 921, 92);
        Seed.Customer(context, 9921, 92, "Readiness Customer");
        lead.ResolveCommercialIdentity(9921, null, "CONFIRMED");
        await context.SaveChangesAsync();

        var rfq = new Rfq
        {
            Id = 9921,
            Rfqno = "RFQ-9921",
            BuyersName = "Readiness Buyer",
            RecDate = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc),
            BusinessUnitId = 92,
            LeadId = lead.Id,
            CreatedBy = "seed",
            CreatedDate = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc)
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        context.Rfqitems.Add(new Rfqitem
        {
            Id = 99211,
            Rfqid = rfq.Id,
            Quantity = 4,
            UnitOfMeasure = "EA",
            ManufacturerPartNumber = "MPN-99211",
            CreatedBy = "seed",
            CreatedDate = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();

        var dto = await new RfqRepository(context).GetByIdAsync(rfq.Id, 92);

        // The lines came back on the very same DTO, so a readiness derived from a zero count is
        // not a conservative default — it is a contradiction with the payload beside it.
        Assert.Single(dto.Rfqitems);
        Assert.Equal(1, dto.ItemCount);
        Assert.Equal("Ready for Quote", dto.Readiness);
    }
}
