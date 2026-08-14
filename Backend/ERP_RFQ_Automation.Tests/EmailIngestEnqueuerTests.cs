using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What survives of the legacy enqueuer's tests.
///
/// <para>This class used to hold fourteen tests of <c>EmailIngestEnqueuer.EnqueueAsync</c> — the
/// fan-out that walked <c>message.Attachments</c> and produced one extraction job, and therefore
/// one Lead, per file. That method and both of its production callers are gone: the poller and
/// the reprocess endpoint now enter the canonical pipeline through
/// <c>IEmailInquiryIntakeService</c>, and the coverage moved with them to
/// <c>EmailCallerCutoverTests</c> and the PostgreSQL slice.</para>
///
/// <para>Deleting those tests with the code they described is deliberate. Keeping them green
/// against a shim would have asserted the shape of the defect — one job per attachment — as
/// though it were a requirement. What remains here is the part that is still real: the skipped-
/// attachment record, which is a display field on the triage surface and has nothing to do with
/// how components are discovered.</para>
/// </summary>
public class EmailIngestEnqueuerTests
{
    [Fact]
    public void TheSkipRecordIsTruncatedToFitTheColumnWithoutLosingTheCount()
    {
        // varchar(2000). A pathological message must still record something truthful rather
        // than fail the whole ingest on a column-length error.
        var ingest = new EmailIngest { Id = 11, MessageId = "m-1", FromEmail = "a@b.example" };
        var many = Enumerable.Range(0, 200)
            .Select(i => $"attachment-with-a-fairly-long-name-{i}.msg (unsupported file type '.msg')")
            .ToList();

        EmailIngestEnqueuer.RecordSkippedAttachments(ingest, many);

        Assert.NotNull(ingest.SkippedAttachmentsJson);
        Assert.True(ingest.SkippedAttachmentsJson!.Length <= 2000);
        Assert.Contains("more skipped attachment(s)", ingest.SkippedAttachmentsJson);
    }

    [Fact]
    public void TheSkipRecordIsValidJsonTheTriageSurfaceCanRead()
    {
        var ingest = new EmailIngest { Id = 11, MessageId = "m-1", FromEmail = "a@b.example" };

        EmailIngestEnqueuer.RecordSkippedAttachments(
            ingest, new[] { "deck.pptx (unsupported file type '.pptx')" });

        var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(
            ingest.SkippedAttachmentsJson!);
        Assert.Equal("deck.pptx (unsupported file type '.pptx')", Assert.Single(parsed!));
    }

    [Fact]
    public void TheLegacyAttachmentFanOut_IsGone()
    {
        // A structural assertion, and the only one in this class that is about absence.
        // EnqueueAsync produced one Lead per attachment and was blind to forwarded mail, because
        // MimeKit's Attachments yields only entities whose Content-Disposition says "attachment".
        // If it ever comes back, the two callers can silently be pointed at it again.
        Assert.DoesNotContain(
            typeof(EmailIngestEnqueuer).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static),
            method => method.Name == "EnqueueAsync");
    }
}
