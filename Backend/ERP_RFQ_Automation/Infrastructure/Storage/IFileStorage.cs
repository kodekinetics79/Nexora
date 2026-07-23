using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Infrastructure.Storage;

public interface IFileStorage
{
    string RootPath { get; }
    string ResolvePath(string storagePath);
    string GetPath(params string[] segments);
    Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default);
}

/// <summary>
/// Filesystem-backed storage with one configured root. Render deployments mount a
/// persistent disk at this root. This centralizes containment and production-volume
/// checks; a future object-store adapter will also require converting remaining legacy
/// file writers from physical paths to stream-based operations.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootWithSeparator;
    private readonly StringComparison _pathComparison;

    public LocalFileStorage(IWebHostEnvironment env, IConfiguration configuration)
        : this(
            configuration["Storage:RootPath"],
            env.ContentRootPath,
            env.IsProduction(),
            configuration["Storage:RequiredMountPath"])
    {
    }

    public LocalFileStorage(
        string? configuredRoot,
        string contentRoot,
        bool requireConfiguredRoot = false,
        string? requiredMountPath = null)
    {
        if (requireConfiguredRoot && string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException(
                "Storage:RootPath is required in Production. Refusing to use ephemeral container storage.");
        if (requireConfiguredRoot && string.IsNullOrWhiteSpace(requiredMountPath))
            throw new InvalidOperationException(
                "Storage:RequiredMountPath is required in Production when using filesystem evidence storage.");

        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(contentRoot, "Uploads")
            : configuredRoot;

        if (!Path.IsPathRooted(root))
            root = Path.Combine(contentRoot, root);

        RootPath = Path.GetFullPath(root);
        if (!string.IsNullOrWhiteSpace(requiredMountPath))
            VerifyRequiredMount(RootPath, requiredMountPath);
        Directory.CreateDirectory(RootPath);
        var rootInfo = new DirectoryInfo(RootPath);
        if (rootInfo.LinkTarget is not null)
            RootPath = rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new InvalidOperationException("The configured storage root symlink cannot be resolved.");

        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _rootWithSeparator = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        // Fail at startup if the configured volume is present but not writable.
        var probe = Path.Combine(RootPath, ".nexora-storage-probe-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe, bufferSize: 1, FileOptions.DeleteOnClose)) { }
    }

    public string RootPath { get; }

    public string GetPath(params string[] segments)
    {
        if (segments is null || segments.Length == 0)
            return RootPath;

        return ResolvePath(Path.Combine(segments));
    }

    public string ResolvePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new ArgumentException("A storage path is required.", nameof(storagePath));

        string candidate;
        if (Path.IsPathRooted(storagePath))
        {
            candidate = Path.GetFullPath(storagePath);
        }
        else
        {
            var normalized = storagePath.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            var uploadsPrefix = "Uploads" + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(uploadsPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[uploadsPrefix.Length..];

            candidate = Path.GetFullPath(Path.Combine(RootPath, normalized));
        }

        if (!candidate.Equals(RootPath, _pathComparison)
            && !candidate.StartsWith(_rootWithSeparator, _pathComparison))
        {
            throw new UnauthorizedAccessException("The requested path is outside the configured storage root.");
        }

        RejectSymbolicLinkTraversal(candidate);
        return candidate;
    }

    private void RejectSymbolicLinkTraversal(string candidate)
    {
        var relative = Path.GetRelativePath(RootPath, candidate);
        if (relative == ".")
            return;

        var current = RootPath;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info?.LinkTarget is not null)
                throw new UnauthorizedAccessException("Symbolic links are not allowed inside evidence storage paths.");
        }
    }

    private static void VerifyRequiredMount(string rootPath, string requiredMountPath)
    {
        var mountPath = Path.GetFullPath(requiredMountPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var mountPrefix = mountPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!rootPath.Equals(mountPath, comparison) && !rootPath.StartsWith(mountPrefix, comparison))
            throw new InvalidOperationException("Storage:RootPath must be located under Storage:RequiredMountPath.");

        if (OperatingSystem.IsLinux())
        {
            const string mountInfoPath = "/proc/self/mountinfo";
            if (!File.Exists(mountInfoPath))
                throw new InvalidOperationException("Cannot verify the configured persistent storage mount.");

            var isMounted = File.ReadLines(mountInfoPath).Any(line =>
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 5) return false;
                var mountedAt = fields[4]
                    .Replace("\\040", " ")
                    .Replace("\\011", "\t")
                    .Replace("\\134", "\\");
                return Path.GetFullPath(mountedAt).Equals(mountPath, StringComparison.Ordinal);
            });
            if (!isMounted)
                throw new InvalidOperationException(
                    $"Required persistent storage mount '{mountPath}' is not present. Refusing to start on ephemeral disk.");
        }
        else if (!Directory.Exists(mountPath))
        {
            throw new InvalidOperationException($"Required storage volume '{mountPath}' does not exist.");
        }
    }

    public async Task<string> WriteImmutableAsync(
        string relativePath,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        var path = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
            return path;

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), ct);
        try
        {
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporaryPath);
        }

        return path;
    }

    public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            ResolvePath(storagePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        return Task.FromResult(stream);
    }
}
