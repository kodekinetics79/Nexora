using System.Text;
using ERP_RFQ_Automation.Infrastructure.Storage;

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

    private LocalEvidenceObjectStorage CreateStorage() =>
        new(new LocalFileStorage(_root, _root));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
