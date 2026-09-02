using System.Collections.Concurrent;
using ERP_RFQ_Automation.Infrastructure.Storage;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// A durable-looking evidence store that keeps objects in memory under the REAL key scheme
/// (<see cref="LocalEvidenceObjectStorage.BuildKey"/>, zone whitelist included) and verifies
/// digests on the way out exactly as the S3 and local stores do. Exists so writer/reader tests
/// can prove what was stored, where, and that a tampered object is refused.
/// </summary>
public sealed class InMemoryEvidenceStorage : IEvidenceObjectStorage
{
    public const string Bucket = "test-bucket";
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public bool IsDurable { get; init; } = true;
    public int WriteCalls { get; private set; }
    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256, string extension,
        ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        WriteCalls++;
        LocalEvidenceObjectStorage.ValidateIdentity(businessUnitId, zone, sha256);
        var key = LocalEvidenceObjectStorage.BuildKey(businessUnitId, zone, sha256, extension).Replace('\\', '/');
        var uri = $"test-evidence://{Bucket}/{key}";
        // Content-addressed: a second write of the same bytes is a no-op, like the S3 HEAD check.
        _objects.GetOrAdd(uri, content.ToArray());
        return Task.FromResult(new EvidenceObject(uri, Bucket, key, sha256, sha256, content.Length));
    }

    public async Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256, CancellationToken ct = default)
    {
        if (!_objects.TryGetValue(storageUri, out var bytes))
            throw new FileNotFoundException("No such evidence object.", storageUri);
        var stream = new MemoryStream(bytes, writable: false);
        await LocalEvidenceObjectStorage.VerifyAsync(stream, expectedSha256, bytes.Length, ct);
        stream.Position = 0;
        return stream;
    }

    public void Tamper(string storageUri, byte[] replacement) => _objects[storageUri] = replacement;

    public static string UriFor(long businessUnitId, string zone, string sha256, string extension)
        => $"test-evidence://{Bucket}/{LocalEvidenceObjectStorage.BuildKey(businessUnitId, zone, sha256, extension).Replace('\\', '/')}";
}
