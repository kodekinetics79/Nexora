using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services.Measurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The measurement chain, end to end: an approved review produces labelled corpus cells,
/// and those cells produce either a defensible interval or an explicit refusal.
///
/// The refusals matter as much as the numbers. Two of the tests below exist purely to
/// prove the product CANNOT emit a percentage it has not earned — that is the property
/// the pilot's sales position depends on, and a regression in it would be invisible in
/// any test that only checked the happy path.
/// </summary>
public class ExtractionAccuracyMeasurementTests
{
    private const long Bu = 1;

    // ───────────────────────────────────────────── Wilson

    [Fact]
    public void Wilson_lower_bound_on_a_perfect_small_sample_is_far_below_one()
    {
        // 27 for 27 is a 100% point estimate. Publishing it would be the overclaim the
        // whole measurement design exists to prevent: the honest ceiling is ~87.5%.
        var lower = AccuracyMeasurementService.WilsonLowerBound(27, 27);
        Assert.NotNull(lower);
        Assert.InRange(lower!.Value, 0.870, 0.880);
    }

    [Fact]
    public void Wilson_lower_bound_tightens_as_the_sample_grows()
    {
        var small = AccuracyMeasurementService.WilsonLowerBound(30, 30)!.Value;
        var large = AccuracyMeasurementService.WilsonLowerBound(300, 300)!.Value;
        Assert.True(small < large, $"expected {small} < {large}");
        Assert.InRange(small, 0.880, 0.895);
    }

    [Fact]
    public void Wilson_lower_bound_of_a_half_sample_brackets_the_point_estimate_from_below()
    {
        var lower = AccuracyMeasurementService.WilsonLowerBound(50, 100)!.Value;
        Assert.True(lower < 0.50);
        Assert.InRange(lower, 0.39, 0.41);
    }

    [Fact]
    public void Wilson_lower_bound_over_no_trials_is_not_a_number()
        => Assert.Null(AccuracyMeasurementService.WilsonLowerBound(0, 0));

    // ───────────────────────────────────────────── projection

    [Fact]
    public void A_field_absent_from_both_images_is_not_counted_as_a_correct_prediction()
    {
        // The single most important rule in the projection. Scoring null → null as a win
        // would let a field the customer's documents never carry report near-perfect
        // accuracy off pure absence.
        var before = """{"id":1,"rfqno":"R-1","buyersName":null,"items":[]}""";
        var after = """{"id":1,"rfqno":"R-1","buyersName":null,"items":[]}""";

        var observations = ExtractionCorpusProjection.Diff(before, after);

        Assert.Contains(observations, o => o.FieldName == "rfqno" && o.Observed == 1 && o.Corrected == 0);
        Assert.DoesNotContain(observations, o => o.FieldName == "buyersName");
    }

    [Fact]
    public void A_value_the_machine_missed_and_the_reviewer_supplied_counts_as_an_error()
    {
        var before = """{"id":1,"rfqno":null,"items":[]}""";
        var after = """{"id":1,"rfqno":"RFQ-9001","items":[]}""";

        var rfqno = Assert.Single(ExtractionCorpusProjection.Diff(before, after),
            o => o.FieldName == "rfqno");
        Assert.Equal(1, rfqno.Observed);
        Assert.Equal(1, rfqno.Corrected);
        Assert.False(rfqno.Correct);
    }

    [Fact]
    public void Numerically_equal_readings_written_differently_are_not_corrections()
    {
        var before = """{"id":1,"items":[{"id":10,"quantity":5,"unitPrice":12.50}]}""";
        var after = """{"id":1,"items":[{"id":10,"quantity":5.0,"unitPrice":12.5}]}""";

        var observations = ExtractionCorpusProjection.Diff(before, after);
        Assert.Single(observations, o => o.FieldName == "quantity" && o.Corrected == 0);
        Assert.Single(observations, o => o.FieldName == "unitPrice" && o.Corrected == 0);
    }

    [Fact]
    public void Lines_the_reviewer_added_or_deleted_are_scored_as_inventory_errors()
    {
        // Line 10 survives, line 11 was invented by the machine, line 12 was missed by it.
        var before = """{"id":1,"items":[{"id":10,"quantity":5},{"id":11,"quantity":9}]}""";
        var after = """{"id":1,"items":[{"id":10,"quantity":5},{"id":12,"quantity":3}]}""";

        var inventory = Assert.Single(ExtractionCorpusProjection.Diff(before, after),
            o => o.FieldName == ExtractionCorpusProjection.LineInventoryField);
        Assert.Equal(ExtractionCorpusScopes.Document, inventory.Scope);
        Assert.Equal(2, inventory.Corrected); // one invented + one missed
        Assert.False(inventory.Correct);
    }

    [Fact]
    public void Field_scores_ignore_lines_the_reviewer_deleted()
    {
        // The deleted line's quantity is not a wrong reading of a real line; it is a line
        // that should not exist, already counted once as an inventory error.
        var before = """{"id":1,"items":[{"id":10,"quantity":5},{"id":11,"quantity":999}]}""";
        var after = """{"id":1,"items":[{"id":10,"quantity":5}]}""";

        var quantity = Assert.Single(ExtractionCorpusProjection.Diff(before, after),
            o => o.Scope == ExtractionCorpusScopes.Line && o.FieldName == "quantity");
        Assert.Equal(1, quantity.Observed);
        Assert.Equal(0, quantity.Corrected);
    }

    [Fact]
    public void An_unreadable_audit_yields_no_evidence_rather_than_bad_evidence()
    {
        Assert.Empty(ExtractionCorpusProjection.Diff("not json", "{}"));
        Assert.Empty(ExtractionCorpusProjection.Diff(null, null));
        Assert.Empty(ExtractionCorpusProjection.Diff("[]", "[]"));
    }

    // ───────────────────────────────────────────── write path

    [Fact]
    public async Task Approving_a_review_captures_the_corpus_inside_the_same_transaction()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 2, "Original Widget") });
            SeedEvidence(seed, 100);
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(Bu))
        {
            await new LeadRepository(ctx).SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "approve",
                Reason = "Checked against the source workbook.",
                Items = new()
                {
                    new LeadItemReviewDTO { Id = 1, LineItemNo = "L1", Quantity = 2, ProductShortName = "Corrected Widget" }
                }
            });
        }

        using var verify = db.ContextFor(Bu);
        var audit = verify.Set<LeadReviewAudit>().Single();
        var entries = verify.Set<ExtractionCorpusEntry>().ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(audit.Id, e.LeadReviewAuditId));
        Assert.All(entries, e => Assert.Equal(100, e.LeadId));

        var productName = Assert.Single(entries, e => e.FieldName == "productShortName");
        Assert.Equal(1, productName.ObservedCount);
        Assert.Equal(1, productName.CorrectedCount);
        Assert.False(productName.FieldCorrect);

        var lineNo = Assert.Single(entries, e => e.FieldName == "lineItemNo");
        Assert.True(lineNo.FieldCorrect);
    }

    [Fact]
    public async Task A_line_the_reviewer_deletes_is_recorded_as_an_inventory_error_end_to_end()
    {
        // Exercises the real write path, not just the projection: the after image is
        // serialized from the tracked aggregate AFTER the delete has flushed, so a removed
        // line must be absent from it and must surface as an inventory correction rather
        // than as sixteen field-level ones.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 110, Bu, parseStatus: "NeedsReview", items: new[]
            {
                Seed.LeadItem(1, "L1", 2, "Real Widget"),
                Seed.LeadItem(2, "L2", 7, "Hallucinated Widget")
            });
            SeedEvidence(seed, 110);
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(Bu))
        {
            await new LeadRepository(ctx).SubmitLeadReviewAsync(110, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "approve",
                Reason = "Second line is not in the source document.",
                Items = new()
                {
                    new LeadItemReviewDTO { Id = 1, LineItemNo = "L1", Quantity = 2, ProductShortName = "Real Widget" }
                }
            });
        }

        using var verify = db.ContextFor(Bu);
        var entries = verify.Set<ExtractionCorpusEntry>().ToList();

        var inventory = Assert.Single(entries,
            e => e.FieldName == ExtractionCorpusProjection.LineInventoryField);
        Assert.Equal(1, inventory.CorrectedCount);
        Assert.False(inventory.FieldCorrect);

        // The surviving line was read correctly, so its fields score clean.
        Assert.True(Assert.Single(entries, e => e.FieldName == "productShortName").FieldCorrect);
        Assert.True(Assert.Single(entries, e => e.FieldName == "quantity").FieldCorrect);
    }

    [Fact]
    public async Task A_save_is_work_in_progress_and_produces_no_labels()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 2) });
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(Bu))
        {
            await new LeadRepository(ctx).SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Items = new() { new LeadItemReviewDTO { Id = 1, LineItemNo = "L1", Quantity = 3 } }
            });
        }

        using var verify = db.ContextFor(Bu);
        Assert.Single(verify.Set<LeadReviewAudit>());
        Assert.Empty(verify.Set<ExtractionCorpusEntry>());
    }

    [Fact]
    public async Task The_audit_after_image_is_never_persisted_as_an_empty_placeholder()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 2) });
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(Bu))
        {
            await new LeadRepository(ctx).SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Items = new() { new LeadItemReviewDTO { Id = 1, LineItemNo = "L1", Quantity = 3 } }
            });
        }

        using var verify = db.ContextFor(Bu);
        var audit = verify.Set<LeadReviewAudit>().Single();
        Assert.NotEqual("{}", audit.AfterJson);
        Assert.Contains("\"quantity\":3", audit.AfterJson);
    }

    // ───────────────────────────────────────────── read path

    [Fact]
    public async Task No_percentage_is_published_below_the_document_floor()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCorpus(seed, documents: AccuracyMeasurementService.MinimumDocuments - 1, correct: true);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var report = await new AccuracyMeasurementService(ctx).GetFieldAccuracyAsync(Bu);

        var field = Assert.Single(report.Fields);
        Assert.Equal(AccuracyMeasurementService.StatusInsufficientData, field.Status);
        Assert.Null(field.LowerBoundPercent);
        Assert.Null(field.ObservedPercent);
        Assert.Equal(AccuracyMeasurementService.MinimumDocuments - 1, field.Documents);
        Assert.Contains("1 more approved document", field.StatusDetail);
        Assert.False(report.Publishable);
    }

    [Fact]
    public async Task At_the_floor_the_published_figure_is_the_lower_bound_not_the_point_estimate()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCorpus(seed, documents: AccuracyMeasurementService.MinimumDocuments, correct: true);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var report = await new AccuracyMeasurementService(ctx).GetFieldAccuracyAsync(Bu);

        var field = Assert.Single(report.Fields);
        Assert.Equal(AccuracyMeasurementService.StatusMeasured, field.Status);
        Assert.Equal(100m, field.ObservedPercent);
        // A perfect 30 for 30 publishes ~88.6%, never 100%.
        Assert.NotNull(field.LowerBoundPercent);
        Assert.InRange(field.LowerBoundPercent!.Value, 88.0m, 89.5m);
        Assert.True(report.Publishable);
    }

    [Fact]
    public async Task Accuracy_is_never_pooled_across_extraction_paths()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCorpus(seed, documents: 30, correct: true, path: "LocalParser", auditSeed: 1_000);
            SeedCorpus(seed, documents: 30, correct: false, path: "ExternalModel", auditSeed: 2_000);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var report = await new AccuracyMeasurementService(ctx).GetFieldAccuracyAsync(Bu);

        Assert.Equal(2, report.Fields.Count);
        Assert.Equal(100m, Assert.Single(report.Fields, f => f.ExtractionPath == "LocalParser").ObservedPercent);
        Assert.Equal(0m, Assert.Single(report.Fields, f => f.ExtractionPath == "ExternalModel").ObservedPercent);
    }

    [Fact]
    public async Task The_correction_signal_reads_audits_written_before_the_corpus_existed()
    {
        // The historic signal the product had been discarding: LeadReviewAudit rows with no
        // corresponding corpus entry still yield a per-field correction rate.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "Success");
            seed.SaveChanges();
            seed.Set<LeadReviewAudit>().Add(new LeadReviewAudit
            {
                BusinessUnitId = Bu,
                LeadId = 100,
                FromVersion = 1,
                ToVersion = 2,
                Action = "approve",
                ReviewedBy = "reviewer@example.com",
                BeforeJson = """{"id":100,"rfqno":"WRONG","buyersName":"Acme","items":[]}""",
                AfterJson = """{"id":100,"rfqno":"RIGHT","buyersName":"Acme","items":[]}""",
                ReviewedOn = DateTime.UtcNow
            });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var signal = await new AccuracyMeasurementService(ctx).GetCorrectionSignalAsync(Bu);

        Assert.Equal(1, signal.Approvals);
        var rfqno = Assert.Single(signal.Fields, f => f.FieldName == "rfqno");
        Assert.Equal(100m, rfqno.CorrectionRatePercent);
        var buyer = Assert.Single(signal.Fields, f => f.FieldName == "buyersName");
        Assert.Equal(0m, buyer.CorrectionRatePercent);
    }

    // ───────────────────────────────────────────── helpers

    private static void SeedCorpus(ErpRfqAutomationContext ctx, int documents, bool correct,
        string path = "LocalParser", long auditSeed = 0)
    {
        Seed.EnsureBusinessUnit(ctx, Bu);
        if (!ctx.Leads.Any(l => l.Id == 100 + auditSeed))
            Seed.Lead(ctx, 100 + auditSeed, Bu, parseStatus: "Success");
        ctx.SaveChanges();

        for (var i = 0; i < documents; i++)
        {
            var audit = new LeadReviewAudit
            {
                BusinessUnitId = Bu,
                LeadId = 100 + auditSeed,
                FromVersion = i + 1,
                ToVersion = i + 2,
                Action = "approve",
                ReviewedBy = "reviewer@example.com",
                BeforeJson = "{}",
                AfterJson = "{}",
                ReviewedOn = DateTime.UtcNow.AddMinutes(-i)
            };
            ctx.Set<LeadReviewAudit>().Add(audit);
            ctx.SaveChanges();

            ctx.Set<ExtractionCorpusEntry>().Add(new ExtractionCorpusEntry
            {
                BusinessUnitId = Bu,
                LeadId = 100 + auditSeed,
                LeadReviewAuditId = audit.Id,
                ExtractionPath = path,
                Scope = ExtractionCorpusScopes.Header,
                FieldName = "rfqno",
                ObservedCount = 1,
                CorrectedCount = correct ? 0 : 1,
                FieldCorrect = correct,
                CapturedOn = DateTime.UtcNow,
                ApprovedBy = "reviewer@example.com"
            });
        }
        ctx.SaveChanges();
    }

    private static void SeedEvidence(ErpRfqAutomationContext context, long leadId)
    {
        var corpus = DocumentCorpus.Create(Bu, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        context.SaveChanges();

        var hash = new string('b', 64);
        var source = SourceDocument.Create(Bu, corpus.Id, hash, $"lead-{leadId}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "quarantine", $"tenant/{Bu}/lead-{leadId}", "v1", 256);
        source.ReleaseFromQuarantine("cleared", $"tenant/{Bu}/lead-{leadId}", "v1");
        context.Set<SourceDocument>().Add(source);
        context.SaveChanges();

        var occurrence = SourceDocumentOccurrence.Create(Bu, source.Id, corpus.Id,
            $"corpus-test:{leadId}", "{}");
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        context.SaveChanges();

        var job = new ExtractionJob
        {
            SourceDocumentOccurrenceId = occurrence.Id,
            BatchId = corpus.BatchId,
            BusinessUnitId = Bu,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = hash,
            StoragePath = source.ObjectKey,
            FileName = source.OriginalFileName,
            FileType = "xlsx",
            Status = ExtractionStatus.Succeeded,
            ResultLeadId = leadId,
            CreatedOn = DateTime.UtcNow,
            UpdatedOn = DateTime.UtcNow
        };
        context.Set<ExtractionJob>().Add(job);
        context.SaveChanges();

        occurrence.BindExtractionJob(job.Id);
        occurrence.MarkProcessing();
        occurrence.MarkResolved();
        context.SaveChanges();
    }
}
