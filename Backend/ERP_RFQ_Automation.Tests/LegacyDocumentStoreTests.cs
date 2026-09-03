using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The one writer behind the four legacy doors (docs/design/evidence-object-store-cutover.md):
/// with the switch off it reproduces the historical disk layout byte for byte; with it on the
/// same call lands in the object store under the content-addressed key scheme, in the zone that
/// keeps it clear of the retention purge's quarantine/cleared swap.
/// </summary>
public sealed class LegacyDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-legacy-store-tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("quotation body");
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public async Task Switch_off_writes_the_historical_disk_layout_and_still_records_the_digest()
    {
        var files = new LocalFileStorage(_root, Path.GetTempPath());
        var evidence = new InMemoryEvidenceStorage();
        var store = new LegacyDocumentStore(files, evidence, routeToObjectStore: false);

        var stored = await store.StoreAttachmentAsync(7, LegacyDocumentFolders.EmailAttachments, "12_abc_rfq.pdf", Bytes);

        Assert.False(stored.InObjectStore);
        Assert.Equal(Path.Combine("Uploads", "RFQ_Attachments", "12_abc_rfq.pdf"), stored.FilePath);
        Assert.Equal(Sha, stored.ContentSha256);
        Assert.True(File.Exists(Path.Combine(_root, "RFQ_Attachments", "12_abc_rfq.pdf")));
        Assert.Equal(0, evidence.WriteCalls);
        Assert.True(await store.ExistsAsync(stored.FilePath));
        await using var read = await store.OpenAsync(stored.FilePath);
        Assert.Equal(Bytes, ReadAll(read));

        var raw = await store.StoreRawMailAsync(7, Bytes);
        Assert.True(Path.IsPathRooted(raw.FilePath));
        Assert.StartsWith(Path.Combine(_root, "Raw_Emails"), raw.FilePath);
    }

    [Fact]
    public async Task Switch_on_writes_content_addressed_objects_in_the_legacy_and_raw_mail_zones()
    {
        var files = new LocalFileStorage(_root, Path.GetTempPath());
        var evidence = new InMemoryEvidenceStorage();
        var store = new LegacyDocumentStore(files, evidence, routeToObjectStore: true);

        var stored = await store.StoreAttachmentAsync(7, LegacyDocumentFolders.ManualAttachments, "12_abc_rfq.pdf", Bytes);
        var raw = await store.StoreRawMailAsync(7, Bytes);

        Assert.True(stored.InObjectStore);
        Assert.Equal(InMemoryEvidenceStorage.UriFor(7, "legacy", Sha, ".pdf"), stored.FilePath);
        Assert.Equal(InMemoryEvidenceStorage.UriFor(7, "raw-mail", Sha, ".eml"), raw.FilePath);
        Assert.False(Directory.Exists(Path.Combine(_root, "Manual_Attachments")));
        Assert.True(await store.ExistsAsync(stored.FilePath));

        // Read back through the verifying read, with the digest recovered from the key alone.
        await using var read = await store.OpenAsync(raw.FilePath, expectedSha256: null);
        Assert.Equal(Bytes, ReadAll(read));

        evidence.Tamper(stored.FilePath, Encoding.UTF8.GetBytes("edited after capture"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(stored.FilePath, Sha));
    }

    [Fact]
    public void Object_uris_are_recognised_and_their_digest_and_zone_recovered()
    {
        var uri = $"s3://NexoraBucket/Evidence/tenants/7/legacy/sha256/{Sha[..2]}/{Sha}.pdf";
        Assert.True(EvidenceObjectUris.IsObjectUri(uri));
        Assert.True(EvidenceObjectUris.TryParseDigest(uri, out var digest));
        Assert.Equal(Sha, digest);
        Assert.True(EvidenceObjectUris.TryParseZone(uri, out var zone));
        Assert.Equal("legacy", zone);

        // Production shapes that are NOT objects.
        Assert.False(EvidenceObjectUris.IsObjectUri(@"Uploads\RFQ_Attachments\394_b339_WhatsApp.jpeg"));
        Assert.False(EvidenceObjectUris.IsObjectUri("/var/data/nexora/uploads/Raw_Emails/x.eml"));
        Assert.False(EvidenceObjectUris.IsObjectUri(@"D:\Sites\site39520\wwwroot\Uploads\Raw_Emails\x.eml"));
        // A local evidence key is a key (zone parseable) but not an object URI.
        var localKey = $"uploads/Evidence/tenants/1/cleared/sha256/{Sha[..2]}/{Sha}.docx";
        Assert.False(EvidenceObjectUris.IsObjectUri(localKey));
        Assert.True(EvidenceObjectUris.TryParseZone(localKey, out var localZone));
        Assert.Equal("cleared", localZone);
    }

    [Fact]
    public async Task The_legacy_zone_is_admitted_by_the_identity_whitelist_and_nothing_else_new_is()
    {
        var evidence = new InMemoryEvidenceStorage();
        await evidence.WriteImmutableAsync(7, "legacy", Sha, ".pdf", Bytes);
        await Assert.ThrowsAsync<ArgumentException>(() => evidence.WriteImmutableAsync(7, "attachments", Sha, ".pdf", Bytes));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
