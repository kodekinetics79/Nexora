using System.Text;
using ERP_RFQ_Automation.Infrastructure.Storage;

namespace ERP_RFQ_Automation.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteImmutableAsync_RejectsConflictingContentAndSupportsLegacyUploadsPath()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());

        var path = await storage.WriteImmutableAsync("Extraction/aa/document.txt", Encoding.UTF8.GetBytes("first"));
        Assert.Equal(path, await storage.WriteImmutableAsync(
            "Extraction/aa/document.txt", Encoding.UTF8.GetBytes("first")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.WriteImmutableAsync("Extraction/aa/document.txt", Encoding.UTF8.GetBytes("second")));

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

    [Fact]
    public async Task TryDeleteAsync_RemovesOneFileAndTreatsAnAbsentFileAsSuccess()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var path = await storage.WriteImmutableAsync("Evidence/aa/purge-me.txt",
            Encoding.UTF8.GetBytes("bytes"));

        Assert.True(await storage.TryDeleteAsync("Evidence/aa/purge-me.txt"));
        Assert.False(File.Exists(path));

        // Absent is success, not an error: a retention purge must be idempotent, and the
        // production documents whose bytes were lost before the persistent disk existed have
        // to reconcile rather than fail.
        Assert.False(await storage.TryDeleteAsync("Evidence/aa/purge-me.txt"));
        Assert.False(await storage.TryDeleteAsync("Evidence/never/existed.txt"));
    }

    [Fact]
    public async Task TryDeleteAsync_CannotEscapeTheStorageRoot()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var outsideDirectory = Path.Combine(Path.GetTempPath(), "nexora-storage-outside-delete",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var outside = Path.Combine(outsideDirectory, "must-survive.txt");
        await File.WriteAllTextAsync(outside, "not evidence");
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.TryDeleteAsync("../must-survive.txt"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.TryDeleteAsync(outside));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.TryDeleteAsync("Evidence/../../must-survive.txt"));
            Assert.True(File.Exists(outside));

            // A symlinked segment is refused for deletion exactly as it is for reads —
            // otherwise a link planted inside the root would be a deletion primitive
            // pointed anywhere on the volume.
            Directory.CreateSymbolicLink(storage.GetPath("linked"), outsideDirectory);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.TryDeleteAsync("linked/must-survive.txt"));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            var link = Path.Combine(storage.RootPath, "linked");
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteAsync_RefusesDirectoriesAndTheRootItself()
    {
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Evidence/bb/keep.txt", Encoding.UTF8.GetBytes("keep"));

        // Files only. There is no recursive form, so no stored path can become a
        // "delete this whole tree" instruction.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.TryDeleteAsync("Evidence"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.TryDeleteAsync("Evidence/bb"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.TryDeleteAsync(storage.RootPath));
        Assert.True(File.Exists(storage.ResolvePath("Evidence/bb/keep.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
