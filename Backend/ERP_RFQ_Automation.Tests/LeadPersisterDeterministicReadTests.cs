using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A document read straight from its own cells — a spreadsheet parsed natively, no model — is
/// verified on its evidence, not on a model-confidence threshold it never had. Six exact lines
/// used to reach the decision screen as "Nexora is not sure it read this line correctly" six
/// times, and Create RFQ stayed shut until a person re-typed what the cells already said.
/// </summary>
public sealed class LeadPersisterDeterministicReadTests
{
    private const long JobId = 17;

    /// <summary>One line whose name, quantity and unit each point at the cell they came from.</summary>
    private static LeadItemData CellBackedLine(int row, string name, int quantity, string unit)
        => Ext.Item(1.0, name, quantity) with
        {
            UnitOfMeasure = unit,
            UnitOfMeasureConfidence = 1.0,
            VerifiedEvidence =
            [
                new LeadItemEvidenceData("ProductShortName", name, name, $"'Sheet1'!B{row}", 1.0m),
                new LeadItemEvidenceData("Quantity", quantity.ToString(), quantity.ToString(), $"'Sheet1'!C{row}", 1.0m),
                new LeadItemEvidenceData("UnitOfMeasure", unit, unit, $"'Sheet1'!D{row}", 1.0m)
            ]
        };

    private static (ChunkedExtractionOutcome Outcome, IReadOnlyList<LeadExtractionResult> Results) Read(
        ExtractionProcessingPath path,
        double overallConfidence,
        LeadItemData[] lines,
        ExtractionOutcomeStatus status = ExtractionOutcomeStatus.Ok,
        CanonicalRfqImportResult? canonicalImport = null)
    {
        var result = Ext.Result(lines.ToList(), 0.2) with { Rfqno = "RFQ-CELLS-1", OverallConfidence = overallConfidence };
        var outcome = new ChunkedExtractionOutcome
        {
            Status = status,
            Result = result,
            ExpectedItemCount = lines.Length,
            ExtractedItemCount = lines.Length,
            ProcessingPath = path,
            CanonicalImport = canonicalImport,
            ReviewReason = status == ExtractionOutcomeStatus.Ok ? null : "Closing date unstated."
        };
        return (outcome, [result]);
    }

    [Fact]
    public void A_native_spreadsheet_read_with_exact_cell_evidence_on_every_line_is_verified_without_a_person()
    {
        // Header confidence is low — a bid list states no RFQ number or closing date — so the
        // blended figure (0.52) sits well under the 0.85 model threshold. Cells do not care.
        var (outcome, results) = Read(ExtractionProcessingPath.DeterministicRules, 0.52,
        [
            CellBackedLine(2, "Gate valve 2in", 4, "EA"),
            CellBackedLine(3, "Ball valve 1in", 12, "EA")
        ]);

        Assert.Equal(LeadPersister.AutoVerification.DeterministicRead,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: 0.85m));
        // And it does not need the model threshold to be configured at all.
        Assert.Equal(LeadPersister.AutoVerification.DeterministicRead,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: null));
    }

    [Fact]
    public void A_canonical_import_counts_as_a_deterministic_read()
    {
        var (outcome, results) = Read(ExtractionProcessingPath.NativeParser, 0.52,
            [CellBackedLine(2, "Gate valve 2in", 4, "EA")], canonicalImport: new CanonicalRfqImportResult());

        Assert.Equal(LeadPersister.AutoVerification.DeterministicRead,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: null));
    }

    [Fact]
    public void A_deterministic_read_missing_a_units_cell_still_waits_for_a_person()
    {
        var unitless = CellBackedLine(2, "Gate valve 2in", 4, "EA") with
        {
            UnitOfMeasure = null,
            VerifiedEvidence =
            [
                new LeadItemEvidenceData("ProductShortName", "Gate valve 2in", "Gate valve 2in", "'Sheet1'!B2", 1.0m),
                new LeadItemEvidenceData("Quantity", "4", "4", "'Sheet1'!C2", 1.0m)
            ]
        };
        var (outcome, results) = Read(ExtractionProcessingPath.DeterministicRules, 1.0, [unitless]);

        Assert.Equal(LeadPersister.AutoVerification.None,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: 0.85m));
    }

    [Fact]
    public void A_deterministic_read_the_parser_flagged_for_review_is_not_verified()
    {
        var (outcome, results) = Read(ExtractionProcessingPath.DeterministicRules, 1.0,
            [CellBackedLine(2, "Gate valve 2in", 4, "EA")], status: ExtractionOutcomeStatus.NeedsReview);

        Assert.Equal(LeadPersister.AutoVerification.None,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: null));
    }

    [Fact]
    public void A_model_read_still_answers_to_the_confidence_threshold_alone()
    {
        // Same evidence shape, but a model authored the values.
        var (outcome, results) = Read(ExtractionProcessingPath.LocalModel, 0.9,
            [CellBackedLine(2, "Gate valve 2in", 4, "EA")]);

        Assert.Equal(LeadPersister.AutoVerification.HighConfidence,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: 0.85m));
        Assert.Equal(LeadPersister.AutoVerification.None,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: 0.95m));
        // No threshold configured: every model read goes to a person, as before.
        Assert.Equal(LeadPersister.AutoVerification.None,
            LeadPersister.DecideAutoVerification(outcome, results, JobId, minConfidence: null));
    }

    [Fact]
    public void The_person_who_approves_can_be_either_system_verifier()
    {
        Assert.True(LeadPersister.IsSystemVerifier(LeadPersister.DeterministicReadActor));
        Assert.True(LeadPersister.IsSystemVerifier(LeadPersister.AutoVerifyActor));
        Assert.False(LeadPersister.IsSystemVerifier("rep@nexora.invalid"));
        Assert.False(LeadPersister.IsSystemVerifier(null));
    }
}
