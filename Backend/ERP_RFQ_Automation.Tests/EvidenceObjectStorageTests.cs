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

    [Fact]
    public async Task S3Store_AcceptsItsOwnUriWhenTheBucketNameHasUppercase()
    {
        // Regression: the bucket used to be recovered with Uri.Host, which LOWERCASES it —
        // hostnames are case-insensitive, bucket names are not. AWS S3 forbids uppercase so
        // this never showed there, but Backblaze B2 permits it. Against a real bucket named
        // "NexoraBucket" every write stored s3://NexoraBucket/... and every read parsed
        // "nexorabucket", so the service rejected its own objects as belonging to another
        // bucket — reported to operators as a hash mismatch on 42 intact documents.
        using var storage = CreateS3Storage("http://127.0.0.1:1", "NexoraBucket");

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            storage.OpenVerifiedReadAsync(
                "s3://NexoraBucket/Evidence/tenants/17/cleared/sha256/ab/source.pdf",
                new string('a', 64)));

        // It must fail trying to REACH the (unreachable) endpoint, never by refusing the URI.
        Assert.IsNotType<InvalidDataException>(error);
    }

    [Fact]
    public async Task S3Store_StillRejectsADifferentBucketThatDiffersOnlyInCase()
    {
        // Case preservation must not become case-insensitivity: "nexorabucket" and
        // "NexoraBucket" are two different buckets on a provider that permits mixed case,
        // and reading across that boundary is the containment failure the guard exists for.
        using var storage = CreateS3Storage("http://127.0.0.1:1", "NexoraBucket");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.OpenVerifiedReadAsync(
                "s3://nexorabucket/Evidence/tenants/17/cleared/sha256/ab/source.pdf",
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

    /// <summary>
    /// The 2026-08-12 split. Everything a write can raise that is NOT a verdict about the document
    /// in hand is the store failing, and must reach the caller as one batch-wide refusal rather
    /// than as N invitations to retry N files.
    /// </summary>
    [Theory]
    // The incident itself, plus the other ways a deployment is simply pointed at the wrong place.
    [InlineData("NoSuchBucket", true, true)]
    [InlineData("AccessDenied", true, true)]
    [InlineData("InvalidAccessKeyId", true, true)]
    [InlineData("SignatureDoesNotMatch", true, true)]
    [InlineData("PermanentRedirect", true, true)]
    // A provider that is merely having a bad day: unavailable, but not misconfigured.
    [InlineData("InternalError", true, false)]
    [InlineData("SlowDown", true, false)]
    [InlineData("ServiceUnavailable", true, false)]
    public void EvidenceStorageFaults_SeparatesMisconfigurationFromAnUnreachableProvider(
        string errorCode, bool expectedUnavailable, bool expectedConfiguration)
    {
        var exception = new AmazonS3Exception("provider detail") { ErrorCode = errorCode };

        Assert.Equal(expectedUnavailable,
            EvidenceStorageFaults.IsStoreUnavailable(exception, CancellationToken.None));
        Assert.Equal(expectedConfiguration, EvidenceStorageFaults.IsConfigurationFault(exception));
    }

    [Fact]
    public void EvidenceStorageFaults_TreatsPerDocumentVerdictsAsTheDocumentsOwn()
    {
        // A content-address conflict, a bad extension and an unresolvable per-object race are
        // facts about ONE file. Flattening them into "storage is down" would stop a whole batch
        // for one poison document — the mirror image of the bug being fixed.
        foreach (var perDocument in new Exception[]
                 {
                     new InvalidDataException("An existing evidence object conflicts with its content address."),
                     new ArgumentException("The evidence extension is invalid."),
                     new ArgumentOutOfRangeException("businessUnitId"),
                     new InvalidOperationException(EvidenceStorageFaults.UnresolvableRaceMessage)
                 })
        {
            Assert.True(EvidenceStorageFaults.IsPerDocumentFault(perDocument));
            Assert.False(EvidenceStorageFaults.IsStoreUnavailable(perDocument, CancellationToken.None));
        }
    }

    [Fact]
    public void EvidenceStorageFaults_LeavesACallerCancellationAsACancellation()
    {
        // A client that hung up mid-upload is not a storage outage, and reporting it as one would
        // pause uploads for everyone else.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.False(EvidenceStorageFaults.IsStoreUnavailable(
            new OperationCanceledException(cancelled.Token), cancelled.Token));
        // The same exception WITHOUT the caller having cancelled is the provider not answering.
        Assert.True(EvidenceStorageFaults.IsStoreUnavailable(
            new TaskCanceledException("The request timed out."), CancellationToken.None));
    }

    [Fact]
    public void EvidenceStorageUnavailableException_KeepsProviderDetailOutOfItsMessage()
    {
        var inner = new AmazonS3Exception("The specified bucket does not exist: NexoraB2")
        {
            ErrorCode = "NoSuchBucket"
        };

        var wrapped = EvidenceStorageFaults.Unavailable(inner);

        Assert.True(wrapped.IsConfigurationFault);
        Assert.Equal("Document storage is not configured, so uploads are paused.", wrapped.Message);
        Assert.DoesNotContain("NexoraB2", wrapped.Message);
        // Nothing is swallowed: the provider's own account is still there for the server log.
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public async Task UnconfiguredStore_RefusesEveryWriteInsteadOfFailingDependencyInjection()
    {
        // An S3 block with no bucket used to throw out of the DI factory, so every upload answered
        // an unhandled 500 — worse than the incident, because a 500 names nothing at all.
        var storage = new UnconfiguredEvidenceObjectStorage(
            new InvalidOperationException("S3 evidence storage requires service URL, credentials, and bucket."));
        var bytes = Encoding.UTF8.GetBytes("rfq");

        var refusal = await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() =>
            storage.WriteImmutableAsync(17, "quarantine", Sha256(bytes), ".csv", bytes));

        Assert.True(refusal.IsConfigurationFault);
        // Reported durable so readiness fails on the probe, rather than claiming the deployment
        // chose local ephemeral storage — a different and untrue diagnosis.
        Assert.True(storage.IsDurable);
        await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() => storage.ProbeAsync());
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
