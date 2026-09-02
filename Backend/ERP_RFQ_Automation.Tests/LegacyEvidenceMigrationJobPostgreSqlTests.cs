using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The one-off disk-to-object migration (docs/design/evidence-object-store-cutover.md §3), run
/// against rows shaped exactly like production's: Windows-separator relative attachment paths
/// from the pre-Render host, absolute raw-mail paths under the root, an absolute path on a host
/// that no longer exists, an extraction job whose path is a local evidence key, and a tombstoned
/// row. Run twice: the second pass moves nothing, re-verifies every object, and reports the
/// same refusals.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class LegacyEvidenceMigrationJobPostgreSqlTests : IDisposable
{
    private const long Tenant = 7_710_001;
    private const long LeadId = 7_710_101;
    private const long MailboxId = 7_710_201;
    private readonly PostgreSqlTestDatabase _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-legacy-migration", Guid.NewGuid().ToString("N"));

    public LegacyEvidenceMigrationJobPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public async Task Running_twice_moves_every_reachable_file_once_verifies_it_and_leaves_lost_rows_alone()
    {
        var files = new LocalFileStorage(_root, Path.GetTempPath());
        var evidence = new InMemoryEvidenceStorage();

        // ---- rows shaped like production ------------------------------------------------------
        var mailBytes = Write("RFQ_Attachments", "394_b339_WhatsApp_Image.jpeg", "jpeg bytes from the mail door");
        var manualBytes = Write("Manual_Attachments", "337_687f_RFQ#6000218024.pdf", "pdf bytes from the manual door");
        var evidenceBytes = Encoding.UTF8.GetBytes("bytes already under a local evidence key");
        var evidenceSha = Sha(evidenceBytes);
        var evidenceKey = $"Evidence/tenants/{Tenant}/cleared/sha256/{evidenceSha[..2]}/{evidenceSha}.docx";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, evidenceKey))!);
        await File.WriteAllBytesAsync(Path.Combine(_root, evidenceKey), evidenceBytes);
        var rawPath = Path.Combine(_root, "Raw_Emails", "12c8e239-54ea-402e-9041-3caed7ac8423.eml");
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        await File.WriteAllBytesAsync(rawPath, Encoding.UTF8.GetBytes("From: a@b\r\n\r\nraw"));

        long attMail, attManual, attEvidence, attLost, attTombstone, ingestOk, ingestLostHost, jobOk, jobMismatch, jobLost;
        await using (var ctx = _database.ContextFor(null))
        {
            Seed.Lead(ctx, LeadId, Tenant);
            Seed.EmailConfig(ctx, MailboxId, Tenant);
            await ctx.SaveChangesAsync();

            Attachment a1 = Row(@"Uploads\RFQ_Attachments\394_b339_WhatsApp_Image.jpeg", "394_b339_WhatsApp_Image.jpeg"),
                       a2 = Row(@"Uploads\Manual_Attachments\337_687f_RFQ#6000218024.pdf", "337_687f_RFQ#6000218024.pdf"),
                       a3 = Row("uploads/" + evidenceKey, "spec.docx"),
                       a4 = Row("Uploads/Extraction/lost-on-the-old-container.pdf", "lost.pdf"),
                       a5 = Row("purged:source-document/1", "purged.pdf");
            ctx.Attachments.AddRange(a1, a2, a3, a4, a5);

            var i1 = Seed.EmailIngest(ctx, 0, MailboxId, "Pending"); i1.MessageId = $"legacy-migration-{Guid.NewGuid():N}"; i1.RawEmailPath = rawPath;
            var i2 = Seed.EmailIngest(ctx, 0, MailboxId, "Pending"); i2.MessageId = $"legacy-migration-lost-{Guid.NewGuid():N}";
            i2.RawEmailPath = @"D:\Sites\site39520\wwwroot\Uploads\Raw_Emails\7779c8ee.eml";

            var j1 = Job(Path.Combine(_root, evidenceKey), evidenceSha);
            var j2 = Job(Path.Combine(_root, evidenceKey), new string('0', 64));
            var j3 = Job("/app/Uploads/Extraction/gone.pdf", Sha(Encoding.UTF8.GetBytes("gone")));
            ctx.AddRange(j1, j2, j3);
            await ctx.SaveChangesAsync();
            (attMail, attManual, attEvidence, attLost, attTombstone) = (a1.Id, a2.Id, a3.Id, a4.Id, a5.Id);
            (ingestOk, ingestLostHost) = (i1.Id, i2.Id);
            (jobOk, jobMismatch, jobLost) = (j1.Id, j2.Id, j3.Id);
        }

        // ---- first run ---------------------------------------------------------------------------
        LegacyMigrationReport first;
        await using (var ctx = _database.ContextFor(null))
            first = await new LegacyEvidenceMigrationJob(ctx, files, evidence, NullLogger<LegacyEvidenceMigrationJob>.Instance).RunAsync();

        Assert.Equal(5, first.Migrated);      // two door copies, the evidence-key attachment, the raw mail, the job
        Assert.Equal(1, first.HashMismatch);  // j2
        // a4 (inside the root, file gone) and i2: a Windows path from the old host is not rooted
        // on Linux, resolves under the root, and is simply not there. Both left untouched.
        Assert.Equal(2, first.SourceMissing);
        Assert.Equal(1, first.Refused);       // j3: absolute path outside the storage root
        Assert.Equal(0, first.Verified);

        await using (var ctx = _database.ContextFor(null))
        {
            var rows = await ctx.Attachments.Where(a => a.ParentId == LeadId).ToDictionaryAsync(a => a.Id);
            Assert.Equal(InMemoryEvidenceStorage.UriFor(Tenant, "legacy", Sha(mailBytes), ".jpeg"), rows[attMail].FilePath);
            Assert.Equal(Sha(mailBytes), rows[attMail].ContentSha256);
            Assert.Equal(InMemoryEvidenceStorage.UriFor(Tenant, "legacy", Sha(manualBytes), ".pdf"), rows[attManual].FilePath);
            // An evidence-key path keeps its zone: the bytes are the source document's.
            Assert.Equal(InMemoryEvidenceStorage.UriFor(Tenant, "cleared", evidenceSha, ".docx"), rows[attEvidence].FilePath);
            Assert.Equal("Uploads/Extraction/lost-on-the-old-container.pdf", rows[attLost].FilePath);
            Assert.Null(rows[attLost].ContentSha256);
            Assert.Equal("purged:source-document/1", rows[attTombstone].FilePath);

            var ingests = await ctx.EmailIngests.IgnoreQueryFilters().Where(e => e.EmailConfigurationId == MailboxId).ToDictionaryAsync(e => e.Id);
            Assert.StartsWith("test-evidence://", ingests[ingestOk].RawEmailPath);
            Assert.Contains("/raw-mail/sha256/", ingests[ingestOk].RawEmailPath);
            Assert.StartsWith(@"D:\Sites", ingests[ingestLostHost].RawEmailPath);

            var jobs = await ctx.Set<ExtractionJob>().IgnoreQueryFilters().Where(j => j.BusinessUnitId == Tenant).ToDictionaryAsync(j => j.Id);
            Assert.Equal(InMemoryEvidenceStorage.UriFor(Tenant, "cleared", evidenceSha, ".docx"), jobs[jobOk].StoragePath);
            Assert.Equal(Path.Combine(_root, evidenceKey), jobs[jobMismatch].StoragePath);
            Assert.Equal("/app/Uploads/Extraction/gone.pdf", jobs[jobLost].StoragePath);
        }
        // The evidence-key attachment and the job named the SAME bytes: one object, not two.
        Assert.Equal(4, evidence.Objects.Count);
        Assert.True(File.Exists(rawPath), "the job copies; it never deletes");

        // ---- second run: idempotent, verifying ------------------------------------------------------
        var writesAfterFirst = evidence.WriteCalls;
        LegacyMigrationReport second;
        await using (var ctx = _database.ContextFor(null))
            second = await new LegacyEvidenceMigrationJob(ctx, files, evidence, NullLogger<LegacyEvidenceMigrationJob>.Instance).RunAsync();

        Assert.Equal(0, second.Migrated);
        Assert.Equal(4, second.Verified); // three attachment objects + the raw mail re-read against their digests
        Assert.Equal(1, second.HashMismatch);
        Assert.Equal(2, second.SourceMissing);
        Assert.Equal(1, second.Refused);
        Assert.Equal(writesAfterFirst, evidence.WriteCalls);
        Assert.Equal(4, evidence.Objects.Count);

        // A corrupted object is caught by the verification pass, not silently counted.
        evidence.Tamper(InMemoryEvidenceStorage.UriFor(Tenant, "legacy", Sha(mailBytes), ".jpeg"), Encoding.UTF8.GetBytes("bit rot"));
        await using (var ctx = _database.ContextFor(null))
        {
            var third = await new LegacyEvidenceMigrationJob(ctx, files, evidence, NullLogger<LegacyEvidenceMigrationJob>.Instance).RunAsync();
            Assert.Equal(3, third.Verified);
            Assert.Equal(2, third.Refused);
        }
    }

    [Fact]
    public async Task Refuses_to_migrate_into_a_store_that_is_not_durable()
    {
        await using var ctx = _database.ContextFor(null);
        var job = new LegacyEvidenceMigrationJob(ctx, new LocalFileStorage(_root, Path.GetTempPath()),
            new InMemoryEvidenceStorage { IsDurable = false }, NullLogger<LegacyEvidenceMigrationJob>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync());
    }

    private byte[] Write(string folder, string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Directory.CreateDirectory(Path.Combine(_root, folder));
        File.WriteAllBytes(Path.Combine(_root, folder, name), bytes);
        return bytes;
    }

    private static Attachment Row(string path, string fileName) => new()
    {
        ParentType = "Lead", ParentId = LeadId, FileName = fileName, FilePath = path,
        MimeType = "application/octet-stream", ContentType = "application", CreatedOn = DateTime.UtcNow, UploadedDate = DateTime.UtcNow
    };

    private static ExtractionJob Job(string storagePath, string contentHash) => new()
    {
        BusinessUnitId = Tenant, BatchId = Guid.NewGuid(), SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = contentHash, StoragePath = storagePath, FileName = Path.GetFileName(storagePath),
        Status = ExtractionStatus.Succeeded, NextAttemptAt = DateTime.UtcNow, CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
    };

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
