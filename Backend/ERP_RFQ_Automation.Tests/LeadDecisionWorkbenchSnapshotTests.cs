using ERP_RFQ_Automation.CommercialCases.Participation;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadDecisionWorkbenchSnapshotTests
{
    [Theory]
    [InlineData("{\"line\":\"10\",\"quantity\":null,\"uom\":\"EA\"}", null)]
    [InlineData("{\"line\":\"10\",\"Quantity\":null,\"quantity\":7,\"uom\":\"EA\"}", 7)]
    [InlineData("{\"line\":\"10\",\"Quantity\":9,\"uom\":\"EA\"}", 9)]
    [InlineData("{\"line\":\"10\",\"quantity\":\"not-stated\",\"uom\":\"EA\"}", null)]
    public void Nullable_or_non_numeric_quantities_do_not_break_the_decision_workbench(
        string snapshotJson, int? expectedQuantity)
    {
        var snapshot = LeadRevisionLineSnapshot.Parse(snapshotJson);

        Assert.Equal(expectedQuantity, snapshot.Quantity);
        Assert.Equal("10", snapshot.LineNumber);
        Assert.Equal("EA", snapshot.UnitOfMeasure);
    }

    [Fact]
    public void Participation_becomes_stale_when_a_new_fit_assessment_supersedes_its_input()
    {
        var decision = new LeadParticipationDecision
        {
            FitAssessmentId = 41,
            IsCommitted = true
        };

        Assert.Equal("COMMITTED", LeadDecisionParticipationState.Resolve(decision,
            new LeadFitAssessment { Id = 41 }, hasDecisionOnPriorRevision: false));
        Assert.Equal("STALE", LeadDecisionParticipationState.Resolve(decision,
            new LeadFitAssessment { Id = 42 }, hasDecisionOnPriorRevision: false));
        Assert.Equal("STALE", LeadDecisionParticipationState.Resolve(decision,
            latestFit: null, hasDecisionOnPriorRevision: false));
    }

    [Fact]
    public void Participation_on_an_older_lead_revision_is_reported_as_stale()
    {
        Assert.Equal("NONE", LeadDecisionParticipationState.Resolve(decision: null,
            latestFit: null, hasDecisionOnPriorRevision: false));
        Assert.Equal("STALE", LeadDecisionParticipationState.Resolve(decision: null,
            latestFit: null, hasDecisionOnPriorRevision: true));
    }
}
