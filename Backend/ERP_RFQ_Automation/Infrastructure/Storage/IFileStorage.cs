using System;
using System.IO;
using System.Security.Cryptography;
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

    /// <summary>
    /// Removes ONE stored file under the configured root. Returns true when a file was
    /// actually removed and false when nothing was there to remove — an absent file is
    /// success, not an error, because a retention purge is idempotent by construction and
    /// because ~15 production documents lost their bytes to ephemeral storage before the
    /// persistent disk existed.
    ///
    /// <para>
    /// Containment is the whole security property: the path goes through the same
    /// <see cref="ResolvePath"/> used by every read (full-path normalization, OS-correct
    /// root-prefix comparison, per-segment symlink rejection) and is additionally refused
    /// when it resolves to the storage root itself or to a directory. There is no
    /// recursive form and no glob form, so a crafted stored path can at worst delete one
    /// file that already lives inside evidence storage.
    /// </para>
    /// </summary>
    Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default);
}

/// <summary>
/// The configured storage volume is full. Thrown instead of a bare <see cref="IOException"/>
/// so the startup failure says what is wrong and what to do about it: a read-write service
/// on a full disk IS broken and must still refuse to boot, but the operator should not have
/// to guess that "No space left on device" during a probe write means the Render disk needs
/// resizing or a retention purge.
/// </summary>
public sealed class StorageCapacityExhaustedException : IOException
{
    public StorageCapacityExhaustedException(string message, Exception? inner = null)
        : base(message, inner) { }
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
            configuration["Storage:RequiredMountPath"],
            configuration.GetValue("Storage:EnforcePersistentMount", false))
    {
    }

    public LocalFileStorage(
        string? configuredRoot,
        string contentRoot,
        bool requireConfiguredRoot = false,
        string? requiredMountPath = null,
        bool enforceRequiredMount = false)
    {
        if (requireConfiguredRoot && string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException(
                "Storage:RootPath is required in Production. Refusing to use ephemeral container storage.");
        if (enforceRequiredMount && string.IsNullOrWhiteSpace(requiredMountPath))
            throw new InvalidOperationException(
                "Storage:RequiredMountPath is required when persistent-mount enforcement is enabled.");

        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(contentRoot, "Uploads")
            : configuredRoot;

        if (!Path.IsPathRooted(root))
            root = Path.Combine(contentRoot, root);

        RootPath = Path.GetFullPath(root);
        if (enforceRequiredMount)
            VerifyRequiredMount(RootPath, requiredMountPath!);
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

        // Fail at startup if the configured volume is present but not writable. A full
        // disk is reported as itself rather than as a bare IOException: boot must still
        // fail, but the log has to name the cause instead of leaving an operator to
        // decode errno 28 out of a probe write.
        var probe = Path.Combine(RootPath, ".nexora-storage-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probe, bufferSize: 1, FileOptions.DeleteOnClose)) { }
        }
        catch (IOException exception) when (IsOutOfSpace(exception))
        {
            throw new StorageCapacityExhaustedException(
                $"Storage volume '{RootPath}' is full: the startup write probe failed with "
                + "'No space left on device'. Nexora cannot serve uploads or evidence reads from a "
                + "full disk. Free space by resizing the volume or running an evidence retention "
                + "purge, then restart.",
                exception);
        }
    }

    /// <summary>
    /// ENOSPC on Unix (28) and ERROR_DISK_FULL/ERROR_HANDLE_DISK_FULL on Windows (112/39).
    /// Checked on the OS error code rather than the message so it survives localization.
    /// </summary>
    internal static bool IsOutOfSpace(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() ? code is 112 or 39 : code == 28;
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

        // Render guarantees that an attached disk is available at its configured
        // absolute mount path before the process starts. Do not depend on the host's
        // internal /proc mount representation, which is not part of Render's contract.
        if (!Directory.Exists(mountPath))
            throw new InvalidOperationException($"Required storage volume '{mountPath}' does not exist.");
    }

    public async Task<string> WriteImmutableAsync(
        string relativePath,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        var path = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            await VerifyExistingContentAsync(path, content, ct);
            return path;
        }

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

    private static async Task VerifyExistingContentAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length != expected.Length)
            throw new InvalidDataException("Existing immutable storage object has a conflicting length.");

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var actualHash = await SHA256.HashDataAsync(stream, ct);
        var expectedHash = SHA256.HashData(expected.Span);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("Existing immutable storage object has conflicting content.");
    }

    public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // ResolvePath is the containment boundary: traversal, absolute paths outside the
        // root and symlinked segments all throw UnauthorizedAccessException here and are
        // deliberately NOT swallowed — a purge that cannot prove where it is pointing must
        // fail loudly, never "succeed" by deleting something else.
        var path = ResolvePath(storagePath);

        if (path.Equals(RootPath, _pathComparison))
            throw new UnauthorizedAccessException("The storage root itself cannot be deleted.");
        if (Directory.Exists(path))
            throw new UnauthorizedAccessException("Only files can be deleted from evidence storage.");

        try
        {
            if (!File.Exists(path))
                return Task.FromResult(false);
            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(false);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(false);
        }
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
