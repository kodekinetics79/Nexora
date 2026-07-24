using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace ERP_RFQ_Automation.Security.DocumentInspection;

public sealed class DocumentInspectionOptions
{
    public const long DefaultMaximumFileBytes = 25L * 1024 * 1024;

    public long MaximumFileBytes { get; init; } = DefaultMaximumFileBytes;
    public int MaximumArchiveEntries { get; init; } = 1_000;
    public long MaximumArchiveEntryBytes { get; init; } = 50L * 1024 * 1024;
    public long MaximumArchiveExpandedBytes { get; init; } = 100L * 1024 * 1024;
    public double MaximumArchiveExpansionRatio { get; init; } = 100;
}
public sealed class DocumentFileInspectionService : IFileInspectionService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ZipSignature = [0x50, 0x4B];

    private readonly IMalwareScanner _malwareScanner;
    private readonly DocumentInspectionOptions _options;

    public DocumentFileInspectionService(
        IMalwareScanner malwareScanner,
        DocumentInspectionOptions? options = null)
    {
        _malwareScanner = malwareScanner ?? throw new ArgumentNullException(nameof(malwareScanner));
        _options = options ?? new DocumentInspectionOptions();
        ValidateOptions(_options);
    }

    public async Task<FileInspectionResult> InspectAsync(
        FileInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);

        var extension = Path.GetExtension(request.FileName ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            return Rejected(null, 0, $"The file extension '{extension}' is not supported.");
        }

        if (request.DeclaredLength is < 0)
        {
            return Rejected(null, 0, "The declared file length is invalid.");
        }

        if (request.DeclaredLength > _options.MaximumFileBytes)
        {
            return Rejected(null, request.DeclaredLength.Value, "The file exceeds the 25 MB inspection limit.");
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(request.Content, _options.MaximumFileBytes, cancellationToken);
        }
        catch (FileTooLargeException exception)
        {
            return Rejected(null, exception.ObservedLength, "The file exceeds the 25 MB inspection limit.");
        }

        if (bytes.Length == 0)
        {
            return Rejected(null, 0, "The file is empty.");
        }

        TypeDetection detection;
        try
        {
            detection = DetectType(bytes, extension);
        }
        catch (UnsafeArchiveException exception)
        {
            return Rejected(null, bytes.LongLength, exception.Message);
        }

        if (!detection.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Rejected(
                detection.ContentType,
                bytes.LongLength,
                $"The content signature does not match the '{extension}' extension.");
        }

        if (!DeclaredTypeMatches(request.DeclaredContentType, detection.ContentType))
        {
            return Rejected(
                detection.ContentType,
                bytes.LongLength,
                $"The declared content type '{request.DeclaredContentType}' does not match the detected type '{detection.ContentType}'.");
        }

        MalwareScanResult scan;
        await using (var scanStream = new MemoryStream(bytes, writable: false))
        {
            try
            {
                scan = await _malwareScanner.ScanAsync(scanStream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                scan = MalwareScanResult.Error(
                    _malwareScanner.GetType().Name,
                    $"The malware scanner failed: {exception.GetType().Name}.");
            }
        }

        return scan.Status switch
        {
            MalwareScanStatus.Clean => new FileInspectionResult(
                FileInspectionStatus.Cleared,
                detection.ContentType,
                bytes.LongLength,
                "File signature, archive safety, and malware checks passed.",
                scan.Engine,
                scan.Signature),
            MalwareScanStatus.Infected => new FileInspectionResult(
                FileInspectionStatus.Quarantined,
                detection.ContentType,
                bytes.LongLength,
                scan.Reason,
                scan.Engine,
                scan.Signature),
            _ => new FileInspectionResult(
                FileInspectionStatus.Quarantined,
                detection.ContentType,
                bytes.LongLength,
                scan.Reason,
                scan.Engine,
                scan.Signature)
        };
    }

    private TypeDetection DetectType(byte[] bytes, string extension)
    {
        if (StartsWith(bytes, PdfSignature))
        {
            return new("application/pdf", [".pdf"]);
        }

        if (StartsWith(bytes, OleSignature))
        {
            return new("application/msword", [".doc"]);
        }

        if (StartsWith(bytes, ZipSignature))
        {
            return InspectOpenXml(bytes);
        }

        if (StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return new("image/png", [".png"]);
        }

        if (StartsWith(bytes, [0xFF, 0xD8, 0xFF]))
        {
            return new("image/jpeg", [".jpg", ".jpeg"]);
        }

        if (StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8))
        {
            return new("image/gif", [".gif"]);
        }

        if (StartsWith(bytes, "BM"u8))
        {
            return new("image/bmp", [".bmp"]);
        }

        if (StartsWith(bytes, [0x49, 0x49, 0x2A, 0x00]) ||
            StartsWith(bytes, [0x4D, 0x4D, 0x00, 0x2A]))
        {
            return new("image/tiff", [".tif", ".tiff"]);
        }

        if (bytes.Length >= 12 &&
            bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return new("image/webp", [".webp"]);
        }

        if (extension is ".csv" or ".txt")
        {
            ValidateText(bytes);
            return extension == ".csv"
                ? new("text/csv", [".csv"])
                : new("text/plain", [".txt"]);
        }

        throw new UnsafeArchiveException("The file has no recognized signature for its extension.");
    }

    private TypeDetection InspectOpenXml(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0)
            {
                throw new UnsafeArchiveException("The OOXML package is empty.");
            }

            if (archive.Entries.Count > _options.MaximumArchiveEntries)
            {
                throw new UnsafeArchiveException("The OOXML package contains too many entries.");
            }

            long totalCompressed = 0;
            long totalExpanded = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? contentTypes = null;
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                foreach (var entry in archive.Entries)
                {
                    ValidateArchivePath(entry.FullName);
                    names.Add(entry.FullName.Replace('\\', '/'));

                    if (entry.Length > _options.MaximumArchiveEntryBytes)
                    {
                        throw new UnsafeArchiveException("An OOXML entry exceeds the expanded-size limit.");
                    }

                    totalCompressed = checked(totalCompressed + entry.CompressedLength);
                    totalExpanded = checked(totalExpanded + entry.Length);
                    if (totalExpanded > _options.MaximumArchiveExpandedBytes)
                    {
                        throw new UnsafeArchiveException("The OOXML package exceeds the expanded-size limit.");
                    }

                    EnsureSafeRatio(entry.Length, entry.CompressedLength);

                    using var entryStream = entry.Open();
                    using var captured = entry.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
                        ? new MemoryStream()
                        : null;
                    long actualLength = 0;
                    int read;
                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        actualLength = checked(actualLength + read);
                        if (actualLength > _options.MaximumArchiveEntryBytes ||
                            actualLength > entry.Length)
                        {
                            throw new UnsafeArchiveException("An OOXML entry expanded beyond its declared safe length.");
                        }

                        captured?.Write(buffer, 0, read);
                    }

                    if (actualLength != entry.Length)
                    {
                        throw new UnsafeArchiveException("An OOXML entry is truncated or malformed.");
                    }

                    if (captured is not null)
                    {
                        contentTypes = StrictUtf8.GetString(captured.ToArray());
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            EnsureSafeRatio(totalExpanded, totalCompressed);
            if (string.IsNullOrWhiteSpace(contentTypes))
            {
                throw new UnsafeArchiveException("The OOXML package has no content-type manifest.");
            }

            if (names.Contains("word/document.xml"))
            {
                if (contentTypes.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnsafeArchiveException("Macro-enabled Word documents are not supported.");
                }

                return new(
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    [".docx"]);
            }

            if (names.Contains("xl/workbook.xml"))
            {
                var macroEnabled = contentTypes.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) ||
                                   names.Contains("xl/vbaProject.bin");
                return macroEnabled
                    ? new("application/vnd.ms-excel.sheet.macroenabled.12", [".xlsm"])
                    : new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".xlsx"]);
            }

            throw new UnsafeArchiveException("The ZIP payload is not a supported Word or Excel OOXML package.");
        }
        catch (UnsafeArchiveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or DecoderFallbackException or OverflowException)
        {
            throw new UnsafeArchiveException("The OOXML package is malformed or unreadable.");
        }
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
        {
            throw new UnsafeArchiveException("The OOXML package contains an unsafe entry path.");
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..") || path.Contains(':'))
        {
            throw new UnsafeArchiveException("The OOXML package contains a traversal entry.");
        }
    }

    private void EnsureSafeRatio(long expanded, long compressed)
    {
        if (expanded == 0)
        {
            return;
        }

        if (compressed <= 0 || (double)expanded / compressed > _options.MaximumArchiveExpansionRatio)
        {
            throw new UnsafeArchiveException("The OOXML package exceeds the allowed expansion ratio.");
        }
    }

    private static void ValidateText(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new UnsafeArchiveException("The text file is not valid UTF-8.");
        }

        if (text.IndexOf('\0') >= 0 || text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f'))
        {
            throw new UnsafeArchiveException("The text file contains binary or unsafe control characters.");
        }
    }

    private static bool DeclaredTypeMatches(string? declared, string detected)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return true;
        }

        var normalized = declared.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (normalized == "application/octet-stream")
        {
            return true;
        }

        return ContentTypeAliases.TryGetValue(detected, out var aliases) && aliases.Contains(normalized);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maximumBytes, CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length - source.Position > maximumBytes)
        {
            throw new FileTooLargeException(source.Length - source.Position);
        }

        await using var target = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumBytes)
                {
                    throw new FileTooLargeException(total);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return target.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private FileInspectionResult Rejected(string? detectedType, long length, string reason) =>
        new(FileInspectionStatus.Rejected, detectedType, length, reason, "not-run", null);

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> signature) =>
        bytes.AsSpan().StartsWith(signature);

    private static void ValidateOptions(DocumentInspectionOptions options)
    {
        if (options.MaximumFileBytes <= 0 || options.MaximumArchiveEntries <= 0 ||
            options.MaximumArchiveEntryBytes <= 0 || options.MaximumArchiveExpandedBytes <= 0 ||
            options.MaximumArchiveExpansionRatio <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inspection limits must be positive and the expansion ratio must exceed one.");
        }
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xlsx", ".xlsm", ".csv", ".txt",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"
    };

    private static readonly IReadOnlyDictionary<string, HashSet<string>> ContentTypeAliases =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = new(StringComparer.OrdinalIgnoreCase) { "application/pdf" },
            ["application/msword"] = new(StringComparer.OrdinalIgnoreCase) { "application/msword", "application/x-msword" },
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            ["application/vnd.ms-excel.sheet.macroenabled.12"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.ms-excel.sheet.macroenabled.12", "application/vnd.ms-excel" },
            ["text/csv"] = new(StringComparer.OrdinalIgnoreCase) { "text/csv", "application/csv", "text/plain" },
            ["text/plain"] = new(StringComparer.OrdinalIgnoreCase) { "text/plain" },
            ["image/png"] = new(StringComparer.OrdinalIgnoreCase) { "image/png" },
            ["image/jpeg"] = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" },
            ["image/gif"] = new(StringComparer.OrdinalIgnoreCase) { "image/gif" },
            ["image/bmp"] = new(StringComparer.OrdinalIgnoreCase) { "image/bmp", "image/x-ms-bmp" },
            ["image/tiff"] = new(StringComparer.OrdinalIgnoreCase) { "image/tiff" },
            ["image/webp"] = new(StringComparer.OrdinalIgnoreCase) { "image/webp" }
        };

    private sealed record TypeDetection(string ContentType, IReadOnlyCollection<string> AllowedExtensions);

    private sealed class UnsafeArchiveException(string message) : Exception(message);

    private sealed class FileTooLargeException(long observedLength) : Exception
    {
        public long ObservedLength { get; } = observedLength;
    }
}
