using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-RFQ-06 requires message identity and attachment identity among the duplicate-detection
/// signals. <c>EmailThreadId</c>, <c>MimeType</c> and <c>FileSize</c> were passed to the identity
/// service as literal <c>null</c> by the only caller, so all three were empty on every one of the
/// 47 production occurrences and could never participate in a decision.
///
/// <para>These tests pin the two halves of the contract: the signals arrive populated from real
/// ingestion metadata on an email-sourced job, and a channel that genuinely has no mail message
/// records honest absence instead of a manufactured identity.</para>
/// </summary>
public sealed class LeadIngestionSignalWiringTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Email_sourced_job_carries_message_identity_and_document_facts_into_the_occurrence()
    {
        const long bu = 91;
        var job = Job(bu, ExtractionSourceType.Email, 'a', "customer-rfq.pdf");
        await SeedAsync(bu, job, mimeType: "application/pdf", byteSize: 20480);
        var metadata = new ExtractionJobMetadata
        {
            SourceOccurrenceId = "email:<CAFE-1@mail.test>:attachment:1",
            LogicalGroupKey = "email:<CAFE-1@mail.test>",
            EmailIngestId = 9201,
            FromEmail = "buyer@signals.test",
            Subject = "RFQ 4471",
            LeadSource = "Email"
        };

        await PersistAsync(bu, job, metadata);

        await using var context = _db.ContextFor(bu);
        var occurrence = await context.Set<LeadIngestionOccurrence>().SingleAsync();
        Assert.Equal("email:<CAFE-1@mail.test>", occurrence.EmailThreadId);
        Assert.Equal("email:<CAFE-1@mail.test>", occurrence.LogicalGroupKey);
        Assert.Equal("application/pdf", occurrence.MimeType);
        Assert.Equal(20480, occurrence.FileSize);
    }

    [Fact]
    public async Task Email_job_without_a_sidecar_group_key_falls_back_to_the_ingest_message_id()
    {
        const long bu = 92;
        var job = Job(bu, ExtractionSourceType.Email, 'b', "reply.pdf");
        await SeedAsync(bu, job, mimeType: "application/pdf", byteSize: 512);

        await PersistAsync(bu, job, new ExtractionJobMetadata
        {
            EmailIngestId = 9201, FromEmail = "buyer@signals.test", Subject = "RFQ 4471", LeadSource = "Email"
        });

        await using var context = _db.ContextFor(bu);
        var occurrence = await context.Set<LeadIngestionOccurrence>().SingleAsync();
        Assert.Equal("email:msg-9201", occurrence.EmailThreadId);
        Assert.Equal("application/pdf", occurrence.MimeType);
        Assert.Equal(512, occurrence.FileSize);
    }

    [Fact]
    public async Task Manual_upload_records_absent_message_identity_rather_than_inventing_one()
    {
        // A manual upload has no mail message. Absence must stay absence: the scorer reads a
        // populated signal as evidence, so a fabricated thread id would manufacture matches.
        const long bu = 93;
        var job = Job(bu, ExtractionSourceType.ManualUpload, 'c', "uploaded.xlsx");
        await SeedAsync(bu, job,
            mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", byteSize: 4096);

        await PersistAsync(bu, job, new ExtractionJobMetadata { UploadedBy = "operator@nexora.test" });

        await using var context = _db.ContextFor(bu);
        var occurrence = await context.Set<LeadIngestionOccurrence>().SingleAsync();
        Assert.Null(occurrence.EmailThreadId);
        Assert.Null(occurrence.LogicalGroupKey);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", occurrence.MimeType);
        Assert.Equal(4096, occurrence.FileSize);
    }

    private static ExtractionJob Job(long bu, ExtractionSourceType sourceType, char hashChar, string fileName) => new()
    {
        Id = 1,
        BatchId = Guid.NewGuid(),
        BusinessUnitId = bu,
        SourceType = sourceType,
        ContentHash = new string(hashChar, 64),
        StoragePath = Path.Combine(Path.GetTempPath(), $"signal-wiring-{Guid.NewGuid():N}.bin"),
        FileName = fileName,
        FileType = Path.GetExtension(fileName).TrimStart('.'),
        Attempts = 1
    };

    private async Task SeedAsync(long bu, ExtractionJob job, string mimeType, long byteSize)
    {
        await using var context = _db.ContextFor(null);
        Seed.BusinessUnit(context, bu);
        Seed.EmailConfig(context, 9101, bu);
        Seed.EmailIngest(context, 9201, 9101, "Pending");
        await context.SaveChangesAsync();
        var corpus = DocumentCorpus.Create(bu, job.BatchId,
            job.SourceType == ExtractionSourceType.Email ? CorpusSourceType.Email : CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(bu, corpus.Id, job.ContentHash, job.FileName!, mimeType,
            "test", $"signal-wiring/{job.ContentHash}", "v1", byteSize);
        source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
        context.Add(source);
        await context.SaveChangesAsync();
    }

    private async Task PersistAsync(long bu, ExtractionJob job, ExtractionJobMetadata metadata)
    {
        await File.WriteAllTextAsync(job.StoragePath, "one inquiry");
        try
        {
            await metadata.SaveAsync(job.StoragePath, bu);
            await using var context = _db.ContextFor(bu);
            var persister = new LeadPersister(context, new NoopLogger<LeadPersister>(),
                leadIdentity: new LeadIdentityApplicationService(context));
            var result = Ext.Result(Ext.Items(1, .9), .9) with { Rfqno = "RFQ-SIGNALS-1" };
            await persister.PersistAsync(job, new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                Result = result,
                ExpectedItemCount = result.Items!.Count,
                ExtractedItemCount = result.Items!.Count,
                AiProviderClass = AiProviderClass.Local
            });
        }
        finally
        {
            File.Delete(job.StoragePath);
            File.Delete(ExtractionJobMetadata.SidecarPath(job.StoragePath, bu));
        }
    }
}
