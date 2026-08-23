using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.CanonicalRecord;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Specification §1: ONE queryable record per processed email. These tests pin the record's
/// eleven contract facts against real persisted state — the same rows the write side
/// produces — and pin the final-status derivation rules documented on
/// <see cref="CanonicalIntakeRecordService.DeriveFinalStatus"/>.
/// </summary>
public sealed class CanonicalIntakeRecordTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best effort */ }
        _db.Dispose();
    }

    private static readonly DateTime T0 = new(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);

    private string TempEml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cir-{Guid.NewGuid():N}.eml");
        File.WriteAllText(path, "From: buyer@customer.com\r\nSubject: RFQ\r\n\r\nbody");
        _tempFiles.Add(path);
        return path;
    }

    private static string Hash(char c) => new(c, 64);

    // =====================================================================================
    // A journey-complete message: every one of the 11 contract facts, populated.
    // =====================================================================================

    [Fact]
    public async Task A_journey_complete_message_yields_one_record_with_all_eleven_facts()
    {
        const long Bu = 9101;
        long leadId, itemId, attachmentJobId, ingestId;
        await using (var ctx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(ctx, Bu);
            var cfg = Seed.EmailConfig(ctx, 91011, Bu);
            var ingest = Seed.EmailIngest(ctx, 91012, cfg.Id, "NeedsReview");
            ingestId = ingest.Id;
            ingest.EmailSubject = "RFQ 4711 — valves";
            ingest.ToEmail = cfg.EmailAddress;
            ingest.TriageOutcome = "Inquiry";
            ingest.TriageReasonJson = "[\"qty_uom_pattern\"]";
            ingest.TriageDecidedOn = T0;
            ingest.InReplyToMessageId = "earlier@customer.example";
            ingest.ReferencesJson = "[\"root@customer.example\",\"earlier@customer.example\"]";
            ingest.RawEmailPath = TempEml();
            // ING-06 through the REAL single owner of the durable skip record.
            EmailIngestEnqueuer.RecordSkippedAttachments(ingest,
                new[] { "forwarded.msg (unsupported file type '.msg')" });

            // The produced lead, with two lines.
            var lead = new Lead
            {
                Id = 91013,
                Rfqno = "RFQ-4711",
                BuyersName = "Acme Buyer",
                Clientemail = "buyer@customer.com",
                RecDate = T0,
                BidClosingDate = T0.AddDays(10),
                LeadSource = "Email",
                EmailSource = "PDF",
                Aiconfidence = 0.91m,
                NoOfLineItems = 2,
                CreatedBy = "seed",
                CreatedDate = T0,
                BusinessUnitId = Bu,
                EmailIngestsId = ingest.Id,
                CurrentRevisionNumber = 1
            };
            lead.LeadItems.Add(new LeadItem
            {
                Id = 91014, LineItemNo = "10", ProductShortName = "Gate Valve",
                ProductShortDescription = "Gate valve 6 inch", Quantity = 250,
                UnitOfMeasure = "EA", ManufacturerPartNumber = "PN-100", Aiconfidence = 0.98m
            });
            lead.LeadItems.Add(new LeadItem
            {
                Id = 91015, LineItemNo = "20", ProductShortName = "Ball Valve",
                Quantity = null, UnitOfMeasure = "EA", Aiconfidence = 0.72m
            });
            ctx.Leads.Add(lead);
            leadId = lead.Id;
            itemId = 91014;
            await ctx.SaveChangesAsync();

            // ---- Evidence ledger: one attachment + the body, both enqueued. ----
            var corpus = DocumentCorpus.Create(Bu, Guid.NewGuid(), CorpusSourceType.Email);
            ctx.Add(corpus);
            await ctx.SaveChangesAsync();

            var pdf = SourceDocument.Create(Bu, corpus.Id, Hash('a'), "boq.pdf",
                "application/pdf", "evidence", "cleared/aa/boq.pdf", "v1", 1234);
            pdf.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            var body = SourceDocument.Create(Bu, corpus.Id, Hash('b'), "RFQ_4711_body.txt",
                "text/plain", "evidence", "cleared/bb/body.txt", "v1", 200);
            body.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            ctx.AddRange(pdf, body);
            await ctx.SaveChangesAsync();

            var attachmentJob = new ExtractionJob
            {
                BatchId = corpus.BatchId, BusinessUnitId = Bu,
                SourceType = ExtractionSourceType.Email, ContentHash = Hash('a'),
                StoragePath = "/evidence/cleared/aa/boq.pdf", FileName = "boq.pdf",
                FileType = "pdf", Status = ExtractionStatus.Succeeded,
                ResultLeadId = lead.Id, CreatedOn = T0, UpdatedOn = T0
            };
            var bodyJob = new ExtractionJob
            {
                BatchId = corpus.BatchId, BusinessUnitId = Bu,
                SourceType = ExtractionSourceType.Email, ContentHash = Hash('b'),
                StoragePath = "/evidence/cleared/bb/body.txt", FileName = "RFQ_4711_body.txt",
                FileType = "txt", Status = ExtractionStatus.Succeeded,
                CreatedOn = T0, UpdatedOn = T0
            };
            ctx.AddRange(attachmentJob, bodyJob);
            await ctx.SaveChangesAsync();
            attachmentJobId = attachmentJob.Id;

            var groupKey = $"email:{ingest.MessageId}";
            var pdfOccurrence = SourceDocumentOccurrence.Create(Bu, pdf.Id, corpus.Id,
                "k-attachment-1",
                "{\"fileName\":\"boq.pdf\",\"metadata\":{\"SourceOccurrenceId\":\"email:msg:attachment:1\"}}");
            pdfOccurrence.SetLogicalGroup(groupKey);
            pdfOccurrence.BindExtractionJob(attachmentJob.Id);
            pdfOccurrence.MarkProcessing();
            pdfOccurrence.MarkResolved();
            var bodyOccurrence = SourceDocumentOccurrence.Create(Bu, body.Id, corpus.Id,
                "k-body",
                "{\"fileName\":\"RFQ_4711_body.txt\",\"metadata\":{\"SourceOccurrenceId\":\"email:msg:body\"}}");
            bodyOccurrence.SetLogicalGroup(groupKey);
            bodyOccurrence.BindExtractionJob(bodyJob.Id);
            bodyOccurrence.MarkProcessing();
            bodyOccurrence.MarkResolved();
            ctx.AddRange(pdfOccurrence, bodyOccurrence);
            await ctx.SaveChangesAsync();

            // ---- Deterministic-path evidence: run → page → region → inquiry/line. ----
            var runGuid = Guid.NewGuid();
            var run = ExtractionRun.Create(Bu, pdf.Id, runGuid, attachmentJob.Id, 1,
                "native-spreadsheet/1.0", "rfq/1");
            run.Start();
            ctx.Add(run);
            var page = DocumentPage.Create(Bu, pdf.Id, 1, 100, 200);
            ctx.Add(page);
            await ctx.SaveChangesAsync();
            var region = DocumentRegion.Create(Bu, page.Id, DocumentRegionType.TableCell,
                0, 0, 10, 5, "250", 0.97m, sourceAddress: "'Sheet1'!B7");
            ctx.Add(region);
            var inquiry = CanonicalInquiry.Create(Bu, corpus.Id, 1);
            inquiry.PopulateHeader("RFQ-4711", "Acme Buyer", T0, T0.AddDays(10));
            inquiry.BindLead(lead.Id);
            ctx.Add(inquiry);
            await ctx.SaveChangesAsync();
            var line = CanonicalLineItem.Create(Bu, inquiry.Id, 1, "Gate valve 6 inch", 250, "EA");
            line.BindLeadItem(itemId);
            ctx.Add(line);
            await ctx.SaveChangesAsync();
            var lineEvidence = FieldEvidence.ForLineItem(Bu, region.Id, line.Id, "Quantity",
                "250", "250", 0.98m, "native-spreadsheet", runGuid);
            var headerEvidence = FieldEvidence.ForInquiry(Bu, region.Id, inquiry.Id,
                "CustomerRfqNumber", "RFQ 4711", "RFQ-4711", 0.99m, "native-spreadsheet", runGuid);
            ctx.AddRange(lineEvidence, headerEvidence);
            // On the SQLite test model the Npgsql-only evidence-ledger configuration does not
            // run, so the ExtractionRun navigation gets a conventional shadow FK that must be
            // pointed at the run row explicitly. PostgreSQL joins run <- evidence on RunId.
            ctx.Entry(lineEvidence).Property("ExtractionRunId").CurrentValue = run.Id;
            ctx.Entry(headerEvidence).Property("ExtractionRunId").CurrentValue = run.Id;
            ctx.Add(ValidationFinding.ForLineItem(Bu, run.Id, line.Id, "LINE_QTY_SUSPECT",
                ValidationSeverity.Warning, "Quantity 250 exceeds the historical mean."));
            run.Complete(1, 1, 1, 1, 2, 1);
            await ctx.SaveChangesAsync();

            // ---- Identity graph: occurrence, revision 1, audit. ----
            var batch = new LeadIngestionBatch
            {
                Id = Guid.NewGuid(), BusinessUnitId = Bu, SourceChannel = "Email",
                CreatedBy = "worker", CreatedAtUtc = T0, UpdatedAtUtc = T0
            };
            ctx.Add(batch);
            await ctx.SaveChangesAsync();
            var occurrence = new LeadIngestionOccurrence
            {
                BusinessUnitId = Bu, BatchId = batch.Id, LeadId = lead.Id,
                SourceDocumentId = pdf.Id, SourceDocumentOccurrenceId = pdfOccurrence.Id,
                ExtractionJobId = attachmentJob.Id, SourceChannel = "Email",
                IdempotencyKey = "identity-k-1", EmailThreadId = ingest.MessageId,
                Sender = "buyer@customer.com", Subject = ingest.EmailSubject,
                ContentHash = Hash('a'), LogicalInquiryFingerprint = Hash('f'),
                Classification = LeadOccurrenceClassification.New, Confidence = 1m,
                DecisionReasonsJson = "[\"no_candidate_matched\"]",
                ProcessingPath = LeadProcessingPath.LocalModel,
                SourceReceivedAtUtc = new DateTimeOffset(T0),
                IngestedAtUtc = T0, CreatedAtUtc = T0,
                ActorId = "extraction-worker", CorrelationId = "corr-1"
            };
            ctx.Add(occurrence);
            await ctx.SaveChangesAsync();
            var revision = new LeadRevision
            {
                BusinessUnitId = Bu, LeadId = lead.Id, RevisionNumber = 1,
                EstablishedByOccurrenceId = occurrence.Id,
                LogicalInquiryFingerprint = Hash('f'), SnapshotJson = "{}",
                CreatedAtUtc = T0, CreatedBy = "worker",
                ProcessingPath = LeadProcessingPath.LocalModel
            };
            ctx.Add(revision);
            await ctx.SaveChangesAsync();
            occurrence.LeadRevisionId = revision.Id;
            ctx.Add(new LeadIdentityAuditEvent
            {
                BusinessUnitId = Bu, LeadId = lead.Id, OccurrenceId = occurrence.Id,
                EventType = "LEAD_CREATED", ActorType = "Service", ActorId = "extraction-worker",
                CorrelationId = "corr-1", IdempotencyKey = "audit-1",
                OccurredAtUtc = new DateTimeOffset(T0)
            });
            ctx.Add(new LeadReviewAudit
            {
                BusinessUnitId = Bu, LeadId = lead.Id, FromVersion = 1, ToVersion = 2,
                Action = "approve", ReviewedBy = "reviewer@tenant.com", Reason = "Verified",
                BeforeJson = "{}", AfterJson = "{}", ReviewedOn = T0.AddHours(2)
            });
            await ctx.SaveChangesAsync();
        }

        await using var reader = _db.ContextFor(Bu);
        var record = await new CanonicalIntakeRecordService(reader)
            .GetByEmailIngestIdAsync(Bu, ingestId);

        Assert.NotNull(record);

        // 1. Source email occurrence.
        Assert.Equal(ingestId, record!.SourceEmail.EmailIngestId);
        Assert.Equal($"msg-{ingestId}", record.SourceEmail.MessageId);
        Assert.Equal("inbox91011@example.com", record.SourceEmail.Mailbox);
        Assert.Equal("earlier@customer.example", record.SourceEmail.InReplyToMessageId);
        Assert.Equal(new[] { "root@customer.example", "earlier@customer.example" },
            record.SourceEmail.References);
        Assert.True(record.SourceEmail.RawEmailAvailable, "the stored .eml exists");
        Assert.Equal("NeedsReview", record.SourceEmail.ParseStatus);

        // 2. Classification and confidence.
        Assert.Equal("Inquiry", record.Classification.TriageOutcome);
        Assert.Equal(new[] { "qty_uom_pattern" }, record.Classification.TriageReasonCodes);
        Assert.Equal(0.91m, record.Classification.AiConfidence);
        Assert.Equal("LocalModel", record.Classification.ProcessingPath);

        // 3. Original message metadata.
        Assert.Equal("buyer@customer.com", record.Message.From);
        Assert.Equal("inbox91011@example.com", record.Message.To);
        Assert.Equal("RFQ 4711 — valves", record.Message.Subject);

        // 4. The UNIFIED attachment inventory: enqueued AND skipped, in ONE list.
        Assert.Equal(3, record.Inventory.Count);
        var attachment = Assert.Single(record.Inventory,
            e => e.Kind == "Attachment" && e.Disposition == "Enqueued");
        Assert.Equal("boq.pdf", attachment.FileName);
        Assert.Equal(Hash('a'), attachment.ContentHash);
        Assert.Equal("Cleared", attachment.SecurityStatus);
        Assert.Equal("Resolved", attachment.IntakeStatus);
        Assert.Equal(attachmentJobId, attachment.ExtractionJobId);
        Assert.Equal("Succeeded", attachment.JobStatus);
        Assert.Equal(leadId, attachment.ResultLeadId);
        var bodyEntry = Assert.Single(record.Inventory, e => e.Kind == "Body");
        Assert.Equal("Enqueued", bodyEntry.Disposition);
        var skipped = Assert.Single(record.Inventory, e => e.Disposition == "Skipped");
        Assert.Equal("forwarded.msg", skipped.FileName);
        Assert.Equal("unsupported file type '.msg'", skipped.SkippedReason);

        // 5. Extracted RFQ header.
        Assert.NotNull(record.Header);
        Assert.Equal(leadId, record.Header!.LeadId);
        Assert.Equal("RFQ-4711", record.Header.RfqNumber);
        Assert.Equal("Acme Buyer", record.Header.BuyerName);
        Assert.Equal(1, record.Header.CurrentRevisionNumber);
        Assert.Empty(record.OtherLeadIds);

        // 6. Extracted RFQ lines with per-line confidence — the unknown quantity stays null.
        Assert.Equal(2, record.Lines.Count);
        Assert.Equal(0.98m, record.Lines[0].Confidence);
        Assert.Equal(250, record.Lines[0].Quantity);
        Assert.Null(record.Lines[1].Quantity);

        // 7. Per-field evidence, anchored to the customer's own coordinates.
        Assert.True(record.Evidence.PerFieldEvidenceRecorded);
        Assert.Null(record.Evidence.Note);
        Assert.Equal(2, record.Evidence.Fields.Count);
        var qty = Assert.Single(record.Evidence.Fields, f => f.FieldName == "Quantity");
        Assert.Equal("Line", qty.Scope);
        Assert.Equal(itemId, qty.LeadItemId);
        Assert.Equal("'Sheet1'!B7", qty.SourceAddress);
        Assert.Single(record.Evidence.Fields, f => f.Scope == "Header");

        // 8. Validation issues, reachable from the record a reviewer opens.
        var issue = Assert.Single(record.ValidationIssues);
        Assert.Equal("LINE_QTY_SUSPECT", issue.Code);
        Assert.Equal("Warning", issue.Severity);
        Assert.Equal("Line", issue.Scope);
        Assert.Equal(itemId, issue.LeadItemId);

        // 9. The duplicate/revision decision.
        var decision = Assert.Single(record.Identity.Occurrences);
        Assert.Equal("New", decision.Classification);
        Assert.Equal(1, decision.RevisionNumber);
        Assert.Equal(new[] { "no_candidate_matched" }, decision.DecisionReasons);
        Assert.Empty(record.Identity.MatchCandidates);

        // 10. Audit history: identity + review events, merged, oldest first.
        Assert.Equal(2, record.AuditTrail.Count);
        Assert.Equal("identity", record.AuditTrail[0].Source);
        Assert.Equal("LEAD_CREATED", record.AuditTrail[0].EventType);
        Assert.Equal("review", record.AuditTrail[1].Source);
        Assert.Equal("approve", record.AuditTrail[1].EventType);
        Assert.True(record.AuditTrail[0].At <= record.AuditTrail[1].At);

        // 11. ONE derived status.
        Assert.Equal(IntakeFinalStatus.NeedsReview, record.FinalStatus);

        // And the by-lead lookup lands on the SAME record.
        var byLead = await new CanonicalIntakeRecordService(reader).GetByLeadIdAsync(Bu, leadId);
        Assert.NotNull(byLead);
        Assert.Equal(record.SourceEmail.EmailIngestId, byLead!.SourceEmail.EmailIngestId);
        Assert.Equal(leadId, byLead.Header!.LeadId);
        Assert.Equal(record.FinalStatus, byLead.FinalStatus);
    }

    // =====================================================================================
    // A noise-rejected message: an honest record — classification, no lead, Rejected.
    // =====================================================================================

    [Fact]
    public async Task A_noise_rejected_message_yields_an_honest_record_with_no_lead_and_status_rejected()
    {
        const long Bu = 9201;
        long ingestId;
        await using (var ctx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(ctx, Bu);
            var cfg = Seed.EmailConfig(ctx, 92011, Bu);
            var ingest = Seed.EmailIngest(ctx, 92012, cfg.Id, "Rejected");
            ingestId = ingest.Id;
            ingest.EmailSubject = "Automatic reply: out of office";
            ingest.TriageOutcome = "Noise";
            ingest.TriageReasonJson = "[\"auto_submitted_header\",\"noreply_sender\"]";
            ingest.TriageDecidedOn = T0;
            ingest.RawEmailPath = TempEml();
            await ctx.SaveChangesAsync();
        }

        await using var reader = _db.ContextFor(Bu);
        var record = await new CanonicalIntakeRecordService(reader)
            .GetByEmailIngestIdAsync(Bu, ingestId);

        Assert.NotNull(record);
        Assert.Equal("Noise", record!.Classification.TriageOutcome);
        Assert.Equal(new[] { "auto_submitted_header", "noreply_sender" },
            record.Classification.TriageReasonCodes);
        // Honest absences: no lead, no AI confidence, no inventory, no evidence, no findings.
        Assert.Null(record.Header);
        Assert.Null(record.Classification.AiConfidence);
        Assert.Empty(record.Lines);
        Assert.Empty(record.Inventory);
        Assert.False(record.Evidence.PerFieldEvidenceRecorded);
        Assert.Empty(record.ValidationIssues);
        Assert.Empty(record.Identity.Occurrences);
        Assert.Empty(record.AuditTrail);
        // The raw message is still replayable — visible and reversible.
        Assert.True(record.SourceEmail.RawEmailAvailable);
        Assert.Equal(IntakeFinalStatus.Rejected, record.FinalStatus);
    }

    // =====================================================================================
    // A dead-lettered message: the failure is VISIBLE on the record, not buried in a queue.
    // =====================================================================================

    [Fact]
    public async Task A_dead_lettered_message_shows_the_failure_visibly()
    {
        const long Bu = 9301;
        long ingestId;
        await using (var ctx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(ctx, Bu);
            var cfg = Seed.EmailConfig(ctx, 93011, Bu);
            var ingest = Seed.EmailIngest(ctx, 93012, cfg.Id, ExtractionWorker.DeadLetterParseStatus);
            ingestId = ingest.Id;
            ingest.TriageOutcome = "Inquiry";
            ingest.TriageReasonJson = "[\"qty_uom_pattern\"]";
            await ctx.SaveChangesAsync();

            var corpus = DocumentCorpus.Create(Bu, Guid.NewGuid(), CorpusSourceType.Email);
            ctx.Add(corpus);
            await ctx.SaveChangesAsync();
            var doc = SourceDocument.Create(Bu, corpus.Id, Hash('c'), "protected.pdf",
                "application/pdf", "evidence", "cleared/cc/protected.pdf", "v1", 999);
            doc.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            ctx.Add(doc);
            await ctx.SaveChangesAsync();

            var job = new ExtractionJob
            {
                BatchId = corpus.BatchId, BusinessUnitId = Bu,
                SourceType = ExtractionSourceType.Email, ContentHash = Hash('c'),
                StoragePath = "/evidence/cleared/cc/protected.pdf", FileName = "protected.pdf",
                FileType = "pdf", Status = ExtractionStatus.DeadLetter,
                Attempts = 5, MaxAttempts = 5,
                LastError = "The document is password-protected and cannot be opened.",
                CreatedOn = T0, UpdatedOn = T0
            };
            ctx.Add(job);
            await ctx.SaveChangesAsync();

            var occurrence = SourceDocumentOccurrence.Create(Bu, doc.Id, corpus.Id,
                "k-protected", "{\"fileName\":\"protected.pdf\"}");
            occurrence.SetLogicalGroup($"email:{ingest.MessageId}");
            occurrence.BindExtractionJob(job.Id);
            occurrence.MarkProcessing();
            occurrence.MarkDeadLetter("extraction_exhausted");
            ctx.Add(occurrence);
            await ctx.SaveChangesAsync();
        }

        await using var reader = _db.ContextFor(Bu);
        var record = await new CanonicalIntakeRecordService(reader)
            .GetByEmailIngestIdAsync(Bu, ingestId);

        Assert.NotNull(record);
        Assert.Equal(IntakeFinalStatus.DeadLettered, record!.FinalStatus);
        var entry = Assert.Single(record.Inventory);
        Assert.Equal("protected.pdf", entry.FileName);
        Assert.Equal("DeadLetter", entry.JobStatus);
        Assert.Equal("DeadLetter", entry.IntakeStatus);
        Assert.Contains("password-protected", entry.JobLastError);
        Assert.Null(entry.ResultLeadId);
        Assert.Null(record.Header);
    }

    // =====================================================================================
    // Tenant scoping is fail-closed: another tenant's record resolves to nothing.
    // =====================================================================================

    [Fact]
    public async Task Another_tenants_message_and_lead_resolve_to_nothing()
    {
        const long Bu = 9401;
        long ingestId, leadId;
        await using (var ctx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(ctx, Bu);
            var lead = Seed.Lead(ctx, 94013, Bu);
            leadId = lead.Id;
            ingestId = lead.EmailIngestsId!.Value;
            Seed.BusinessUnit(ctx, 9402);
            await ctx.SaveChangesAsync();
        }

        // An UNSCOPED context (worker shape): only the explicit predicates protect the read.
        await using var reader = _db.ContextFor(null);
        var service = new CanonicalIntakeRecordService(reader);
        Assert.Null(await service.GetByEmailIngestIdAsync(9402, ingestId));
        Assert.Null(await service.GetByLeadIdAsync(9402, leadId));
        Assert.NotNull(await service.GetByEmailIngestIdAsync(Bu, ingestId));
    }

    // =====================================================================================
    // The final-status derivation rules, pinned one by one (see the service's rule comment).
    // =====================================================================================

    private static ExtractionJob Job(ExtractionStatus status) => new() { Status = status };
    private static readonly IntakeOccurrenceStatus[] NoOccurrences = Array.Empty<IntakeOccurrenceStatus>();

    [Fact]
    public void Rule1_in_flight_work_beats_every_terminal_claim()
    {
        Assert.Equal(IntakeFinalStatus.InProgress, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Queued", new[] { Job(ExtractionStatus.Extracting) }, NoOccurrences, false, null));
        // No jobs yet + Pending/Queued: the fan-out / crash-window state.
        Assert.Equal(IntakeFinalStatus.InProgress, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Pending", Array.Empty<ExtractionJob>(), NoOccurrences, false, null));
        Assert.Equal(IntakeFinalStatus.InProgress, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Queued", Array.Empty<ExtractionJob>(), NoOccurrences, false, null));
    }

    [Fact]
    public void Rule1_a_stale_queued_flag_is_outranked_by_an_all_terminal_ledger()
    {
        // Jobs exist and are ALL terminal: the stale "Queued" ParseStatus does not
        // manufacture an in-flight claim.
        var lead = new Lead { LeadSource = "Email", CreatedBy = "t" };
        Assert.Equal(IntakeFinalStatus.Completed, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Queued", new[] { Job(ExtractionStatus.Succeeded) }, NoOccurrences, true, lead));
    }

    [Fact]
    public void Rule2a_a_lead_plus_a_dead_lettered_sibling_is_completed_with_failures()
    {
        var lead = new Lead { LeadSource = "Email", CreatedBy = "t" };
        Assert.Equal(IntakeFinalStatus.CompletedWithFailures,
            CanonicalIntakeRecordService.DeriveFinalStatus(
                "NeedsReview",
                new[] { Job(ExtractionStatus.Succeeded), Job(ExtractionStatus.DeadLetter) },
                NoOccurrences, true, lead));
        // An occurrence-level loss counts the same way.
        Assert.Equal(IntakeFinalStatus.CompletedWithFailures,
            CanonicalIntakeRecordService.DeriveFinalStatus(
                "NeedsReview", new[] { Job(ExtractionStatus.Succeeded) },
                new[] { IntakeOccurrenceStatus.Rejected }, true, lead));
    }

    [Fact]
    public void Rule2b_and_2c_review_state_separates_needs_review_from_completed()
    {
        var unreviewed = new Lead
        {
            LeadSource = "Email", CreatedBy = "t",
            RequiresCommercialReview = true, ReviewApprovedOn = null
        };
        Assert.Equal(IntakeFinalStatus.NeedsReview, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Success", new[] { Job(ExtractionStatus.Succeeded) }, NoOccurrences, true, unreviewed));
        Assert.Equal(IntakeFinalStatus.NeedsReview, CanonicalIntakeRecordService.DeriveFinalStatus(
            "NeedsReview", new[] { Job(ExtractionStatus.Succeeded) }, NoOccurrences, true,
            new Lead { LeadSource = "Email", CreatedBy = "t" }));

        var approved = new Lead
        {
            LeadSource = "Email", CreatedBy = "t",
            RequiresCommercialReview = true, ReviewApprovedOn = T0
        };
        Assert.Equal(IntakeFinalStatus.Completed, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Success", new[] { Job(ExtractionStatus.Succeeded) }, NoOccurrences, true, approved));
    }

    [Fact]
    public void Rule3_no_lead_outcomes_rejected_dead_lettered_failed_processed_unknown()
    {
        Assert.Equal(IntakeFinalStatus.Rejected, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Rejected", Array.Empty<ExtractionJob>(), NoOccurrences, false, null));
        Assert.Equal(IntakeFinalStatus.DeadLettered, CanonicalIntakeRecordService.DeriveFinalStatus(
            ExtractionWorker.DeadLetterParseStatus, Array.Empty<ExtractionJob>(),
            NoOccurrences, false, null));
        Assert.Equal(IntakeFinalStatus.DeadLettered, CanonicalIntakeRecordService.DeriveFinalStatus(
            "NeedsReview", new[] { Job(ExtractionStatus.DeadLetter) }, NoOccurrences, false, null));
        Assert.Equal(IntakeFinalStatus.Failed, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Failed - nothing to extract", Array.Empty<ExtractionJob>(), NoOccurrences, false, null));
        Assert.Equal(IntakeFinalStatus.ProcessedNoLead, CanonicalIntakeRecordService.DeriveFinalStatus(
            "Success", new[] { Job(ExtractionStatus.Succeeded) }, NoOccurrences, false, null));
        Assert.Equal(IntakeFinalStatus.Unknown, CanonicalIntakeRecordService.DeriveFinalStatus(
            null, Array.Empty<ExtractionJob>(), NoOccurrences, false, null));
    }

    // =====================================================================================
    // Small parsers stay tolerant: a corrupt column must never break the audit surface.
    // =====================================================================================

    [Fact]
    public void Skipped_entry_and_json_parsers_are_tolerant()
    {
        Assert.Equal(("forwarded.msg", "unsupported file type '.msg'"),
            CanonicalIntakeRecordService.SplitSkippedEntry(
                "forwarded.msg (unsupported file type '.msg')"));
        Assert.Equal(("... and 3 more skipped attachment(s)", null),
            CanonicalIntakeRecordService.SplitSkippedEntry("... and 3 more skipped attachment(s)"));
        Assert.Empty(CanonicalIntakeRecordService.ParseJsonStringArray("{not json"));
        Assert.Empty(CanonicalIntakeRecordService.ParseJsonStringArray(null));
    }
}
