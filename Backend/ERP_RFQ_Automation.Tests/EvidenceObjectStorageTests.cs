using System.Text;
using Amazon.S3;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

public sealed class EvidenceObjectStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-evidence-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalStore_UsesTenantScopedContentAddress_AndVerifiesReads()
    {
        var bytes = Encoding.UTF8.GetBytes("rfq,quantity\nA-1,10\n");
        var hash = Sha256(bytes);
        var storage = CreateStorage();

        var written = await storage.WriteImmutableAsync(17, "cleared", hash, ".csv", bytes);

        Assert.Contains(Path.Combine("tenants", "17", "cleared", "sha256"), written.StorageUri);
        Assert.Equal(hash, written.Version);
        await using var read = await storage.OpenVerifiedReadAsync(written.StorageUri, hash);
        using var reader = new StreamReader(read);
        Assert.Equal(Encoding.UTF8.GetString(bytes), await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task LocalStore_RejectsMutableObjectAtExistingContentAddress()
    {
        var original = Encoding.UTF8.GetBytes("original");
        var replacement = Encoding.UTF8.GetBytes("tampered");
        var hash = Sha256(original);
        var storage = CreateStorage();
        var written = await storage.WriteImmutableAsync(17, "quarantine", hash, ".txt", original);
        await File.WriteAllBytesAsync(written.StorageUri, replacement);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.WriteImmutableAsync(17, "quarantine", hash, ".txt", original));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.OpenVerifiedReadAsync(written.StorageUri, hash));
    }

    [Theory]
    [InlineData(0, "cleared")]
    [InlineData(1, "public")]
    public async Task LocalStore_RejectsInvalidTenantOrZone(long businessUnitId, string zone)
    {
        var bytes = Encoding.UTF8.GetBytes("rfq");
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            CreateStorage().WriteImmutableAsync(businessUnitId, zone, Sha256(bytes), ".csv", bytes));
    }

    [Fact]
    public async Task S3Store_RejectsObjectUriFromDifferentBucketBeforeNetworkRead()
    {
        using var storage = CreateS3Storage("http://127.0.0.1:1", "tenant-evidence");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.OpenVerifiedReadAsync(
                "s3://attacker-controlled/Evidence/tenants/17/cleared/source.pdf",
                new string('a', 64)));

        Assert.Contains("configured storage bucket", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://localhost:9000")]
    [InlineData("http://[::1]:9000")]
    [InlineData("https://objects.example.com")]
    public void S3ProbeEndpoint_AllowsHttpsAndLoopbackHttp(string serviceUrl)
    {
        S3EvidenceObjectStorage.ValidateServiceEndpoint(serviceUrl);
    }

    [Theory]
    [InlineData("http://objects.example.com")]
    [InlineData("ftp://objects.example.com")]
    [InlineData("not-a-url")]
    public void S3ProbeEndpoint_RejectsInsecureOrInvalidNonLocalEndpoint(string serviceUrl)
    {
        Assert.Throws<InvalidOperationException>(() =>
            S3EvidenceObjectStorage.ValidateServiceEndpoint(serviceUrl));
    }

    [Fact]
    public void S3Store_RejectsInsecureNonLocalEndpointDuringConstruction()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CreateS3Storage("http://objects.example.com", "tenant-evidence"));

        Assert.Contains("requires HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S3Probe_RequiresEnabledVersioningWhenProviderReportsStatus()
    {
        S3EvidenceObjectStorage.EnsureVersioningEnabled(VersionStatus.Enabled);

        Assert.Throws<InvalidOperationException>(() =>
            S3EvidenceObjectStorage.EnsureVersioningEnabled(VersionStatus.Suspended));
        Assert.Throws<InvalidOperationException>(() =>
            S3EvidenceObjectStorage.EnsureVersioningEnabled(null));
    }

    [Fact]
    public async Task LocalStore_PurgesBytesAndReportsWhatItActuallyFreed()
    {
        var bytes = Encoding.UTF8.GetBytes("rfq,quantity\nA-1,10\n");
        var hash = Sha256(bytes);
        var storage = CreateStorage();
        var written = await storage.WriteImmutableAsync(17, "cleared", hash, ".csv", bytes);

        // Measured before deleting, so a dry run and the real run agree on the figure.
        Assert.Equal(bytes.LongLength,
            await storage.TryMeasureObjectAsync(written.Bucket, written.Key, written.Version));

        var purged = await storage.TryDeletePurgedObjectAsync(written.Bucket, written.Key, written.Version);
        Assert.True(purged.Deleted);
        Assert.Equal(bytes.LongLength, purged.BytesFreed);
        Assert.False(File.Exists(written.StorageUri));

        // Re-running is a no-op that reports zero rather than throwing — the purge is
        // idempotent and an absent object is a reconciliation, not a failure.
        var again = await storage.TryDeletePurgedObjectAsync(written.Bucket, written.Key, written.Version);
        Assert.False(again.Deleted);
        Assert.Equal(0, again.BytesFreed);
        Assert.Null(await storage.TryMeasureObjectAsync(written.Bucket, written.Key, written.Version));
    }

    [Fact]
    public async Task LocalStore_RefusesToPurgeAKeyThatEscapesTheStorageRoot()
    {
        var storage = CreateStorage();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            storage.TryDeletePurgedObjectAsync("local", "../../escape.csv", "v1"));
        // A key that belongs to a different provider is refused rather than resolved
        // locally, so an S3-written object can never be "purged" by deleting a lookalike
        // path on disk.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.TryDeletePurgedObjectAsync("some-s3-bucket", "Evidence/x.csv", "v1"));
    }

    [Fact]
    public void S3Purge_TreatsAContentHashAsNoVersionId()
    {
        // Local storage records the content hash where S3 records a version id, and a bucket
        // with versioning suspended returns none at all. Sending a 64-hex hash to S3 as a
        // VersionId would simply 400; sending a real version id is mandatory, because a
        // delete WITHOUT one on a versioned bucket only writes a delete marker and frees
        // nothing while reporting success.
        Assert.Null(S3EvidenceObjectStorage.NormalizeVersionId(new string('a', 64)));
        Assert.Null(S3EvidenceObjectStorage.NormalizeVersionId(null));
        Assert.Null(S3EvidenceObjectStorage.NormalizeVersionId("   "));
        Assert.Equal("3sL4kqtJlcpXroDTDmJ+rmSpXd3dIbrHY+MTRCxf3vjVBH40Nr8X8gdRQBpUMLUo",
            S3EvidenceObjectStorage.NormalizeVersionId(
                " 3sL4kqtJlcpXroDTDmJ+rmSpXd3dIbrHY+MTRCxf3vjVBH40Nr8X8gdRQBpUMLUo "));
    }

    private LocalEvidenceObjectStorage CreateStorage() =>
        new(new LocalFileStorage(_root, _root));

    private static S3EvidenceObjectStorage CreateS3Storage(string serviceUrl, string bucket) =>
        new(Options.Create(new S3EvidenceStorageOptions
        {
            ServiceUrl = serviceUrl,
            AccessKeyId = "test-access-key",
            SecretAccessKey = "test-secret-key",
            Bucket = bucket,
        }));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
