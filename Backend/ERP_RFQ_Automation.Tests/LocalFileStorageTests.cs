using System.Text;
using ERP_RFQ_Automation.Infrastructure.Storage;

namespace ERP_RFQ_Automation.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteImmutableAsync_PreservesFirstContentAndSupportsLegacyUploadsPath()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());

        var path = await storage.WriteImmutableAsync("Extraction/aa/document.txt", Encoding.UTF8.GetBytes("first"));
        await storage.WriteImmutableAsync("Extraction/aa/document.txt", Encoding.UTF8.GetBytes("second"));

        Assert.Equal("first", await File.ReadAllTextAsync(path));
        Assert.Equal(path, storage.ResolvePath("Uploads/Extraction/aa/document.txt"));
    }

    [Fact]
    public void ResolvePath_RejectsTraversalAndAbsolutePathsOutsideRoot()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());

        Assert.Throws<UnauthorizedAccessException>(() => storage.ResolvePath("../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => storage.ResolvePath(Path.Combine(Path.GetTempPath(), "outside.txt")));
    }

    [Fact]
    public void Constructor_RequiresExplicitRootForProduction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LocalFileStorage(configuredRoot: null, contentRoot: Path.GetTempPath(), requireConfiguredRoot: true));
        Assert.Throws<InvalidOperationException>(() =>
            new LocalFileStorage(
                _root,
                Path.GetTempPath(),
                requireConfiguredRoot: true,
                requiredMountPath: null,
                enforceRequiredMount: true));

        var configuredProductionStorage = new LocalFileStorage(
            _root,
            Path.GetTempPath(),
            requireConfiguredRoot: true,
            requiredMountPath: null,
            enforceRequiredMount: false);
        Assert.Equal(Path.GetFullPath(_root), configuredProductionStorage.RootPath);
    }

    [Fact]
    public void ResolvePath_RejectsSymlinkTraversal()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var outside = Path.Combine(Path.GetTempPath(), "nexora-storage-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(storage.GetPath("linked"), outside);
            Assert.Throws<UnauthorizedAccessException>(() => storage.ResolvePath("linked/secret.txt"));
        }
        finally
        {
            var link = Path.Combine(storage.RootPath, "linked");
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
