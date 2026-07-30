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
