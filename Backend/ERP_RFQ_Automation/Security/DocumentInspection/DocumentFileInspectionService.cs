using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Security.DocumentInspection;

public sealed class DocumentInspectionOptions
{
    public const long DefaultMaximumFileBytes = 25L * 1024 * 1024;

    public long MaximumFileBytes { get; init; } = DefaultMaximumFileBytes;
    public int MaximumArchiveEntries { get; init; } = 1_000;

    // Per-entry cap equals the package cap DELIBERATELY. Entries are streamed through a
    // rented 80KB buffer and never materialised, so the resource a hostile archive can
    // consume is bounded by MaximumArchiveExpandedBytes (enforced mid-stream), not by any
    // single entry's size. A tighter per-entry cap added no safety — but it rejected real
    // documents: a genuine Aramco RFP (4.4 MB on disk) carries a document.xml that
    // expands to 121 MB — thousands of materials-table rows at an honest ~28x ratio —
    // and production rejected exactly that file twice, first at 50 MB, then at 100 MB.
    // 256 MB covers it with headroom. Safe to hold at this size ONLY because the
    // downstream .docx reader streams (ProductionDocumentReader.ExtractTextFromDocx uses
    // OpenXmlReader, not the DOM) — if a DOM reader ever returns, this cap is what
    // stands between a large tender and a 512 MB instance OOM.
    public long MaximumArchiveEntryBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumArchiveExpandedBytes { get; init; } = 256L * 1024 * 1024;

    // 300, not 100: repetitive OOXML table markup legitimately compresses at 100-300x —
    // the same Aramco document.xml pattern — while classic zip bombs sit at 1000x and up.
    // The ratio is a tripwire, not the bound: even at exactly 300x an archive can only
    // reach MaximumArchiveExpandedBytes of actual work before the mid-stream total cap
    // stops it, and nested archives are never expanded here at all.
    public double MaximumArchiveExpansionRatio { get; init; } = 300;
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
            // DO NOT interpolate the extension into this sentence. It is caller-controlled
            // text from the uploaded FILENAME, checked before any allow-list, and rejection
            // reasons are now rendered verbatim as authoritative product copy in the intake
            // UI. A filename like "quote.pdf-is-not-supported-call-<phone>" would put an
            // attacker's words inside a Nexora sentence shown to every colleague viewing
            // the batch. The extension is unrecognised by definition, so it carries no
            // information a fixed sentence lacks — the UI already shows the filename
            // itself, safely, in its own column.
            return Rejected(null, 0, "The file's extension is not a type Nexora accepts.");
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
            // The reason is the ONLY truthful account of why this file stopped, and every caller
            // persists it (DocumentIngestionService writes it into the occurrence's
            // last_error_details). The error code travels with it so the UI can be specific about
            // causes it has a real remedy for instead of guessing.
            return Rejected(null, bytes.LongLength, exception.Message, exception.ErrorCode);
        }

        if (!detection.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Rejected(
                detection.ContentType,
                bytes.LongLength,
                MismatchReason(detection.ContentType, extension));
        }

        if (!DeclaredTypeMatches(request.DeclaredContentType, detection.ContentType))
        {
            return Rejected(
                detection.ContentType,
                bytes.LongLength,
                $"The declared content type '{request.DeclaredContentType}' does not match the detected type '{detection.ContentType}'.");
        }

        MalwareScanResult scan;
        var verdictReused = request.ReusableMalwareVerdict is not null;
        if (request.ReusableMalwareVerdict is { } reusable)
        {
            scan = MalwareScanResult.Clean(reusable.Engine, reusable.SignatureVersion);
        }
        else
        {
            await using var scanStream = new MemoryStream(bytes, writable: false);
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
                    MalwareScannerMessages.ScannerFailed,
                    $"The malware scanner {_malwareScanner.GetType().Name} failed with " +
                    $"{exception.GetType().Name}: {exception.Message}");
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
                scan.Signature)
            {
                MalwareStatus = scan.Status,
                ErrorCode = "security_scan_cleared",
                MalwareVerdictReused = verdictReused
            },
            MalwareScanStatus.Infected => new FileInspectionResult(
                FileInspectionStatus.Quarantined,
                detection.ContentType,
                bytes.LongLength,
                scan.Reason,
                scan.Engine,
                scan.Signature)
            {
                MalwareStatus = scan.Status,
                ErrorCode = "malware_detected"
            },
            _ => new FileInspectionResult(
                FileInspectionStatus.Quarantined,
                detection.ContentType,
                bytes.LongLength,
                scan.Reason,
                scan.Engine,
                scan.Signature)
            {
                MalwareStatus = scan.Status,
                IsRetryable = true,
                ErrorCode = "security_scanner_unavailable",
                OperatorDiagnostics = scan.Diagnostics
            }
        };
    }

    /// <summary>
    /// The signature/extension mismatch sentence. The extension is safe to quote back — it has
    /// already been constrained to the intake allow-list — and naming the format the bytes really
    /// ARE is the difference between a dead end and a one-step remedy.
    ///
    /// <para>
    /// The HTML case is the one that mattered in production: portal exports from SEC and Aramco,
    /// and every "export to Excel" button that emits an HTML table, arrive named <c>.xls</c>.
    /// Those files kept being rejected with "its contents are not in that format", which is true
    /// and useless. The MAGIC-BYTE CONTROL IS NOT WEAKENED to admit them — a file is still only
    /// ever treated as what its bytes say it is — but now that <c>.html</c> is itself an accepted
    /// format, the remedy is a rename, and the sentence says so.
    /// </para>
    /// </summary>
    private static string MismatchReason(string detectedContentType, string extension) =>
        detectedContentType switch
        {
            "text/html" =>
                $"This file is named '{extension}' but its contents are a web page (HTML). Nexora "
                + "accepts HTML directly: rename the file so it ends in .html and upload it again, "
                + "or open it and use Save As to store a real spreadsheet, then upload that.",
            "message/rfc822" =>
                $"This file is named '{extension}' but its contents are an email message. Rename it "
                + "so it ends in .eml and upload it again.",
            "application/vnd.ms-outlook" =>
                $"This file is named '{extension}' but its contents are an Outlook message. Rename "
                + "it so it ends in .msg and upload it again.",
            _ => $"The content signature does not match the '{extension}' extension."
        };

    private TypeDetection DetectType(byte[] bytes, string extension)
    {
        if (StartsWith(bytes, PdfSignature))
        {
            return new("application/pdf", [".pdf"]);
        }

        if (StartsWith(bytes, OleSignature))
        {
            return InspectOleCompound(bytes, extension);
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

        // HTML is typed from markup at the START of the document, never from a tag found
        // somewhere inside it: a substring match anywhere would let a base64 blob or a CSV that
        // merely mentions "<table>" be typed as HTML, and typing by "contains" is how signature
        // checks get bypassed. Validated as text first so a binary payload cannot be renamed
        // .html and carried past this gate on the strength of six leading ASCII characters.
        if (HtmlDocumentTextExtractor.HasHtmlSignature(bytes))
        {
            ValidateText(bytes, allowNonUtf8: true);
            return new("text/html", [".html", ".htm"]);
        }

        // An .eml has no magic number — RFC 5322 defines a header block, so that IS the
        // signature. Only the HEADER region is validated as strict text: a MIME body legitimately
        // carries 8-bit and binary transfer encodings, and rejecting those would refuse ordinary
        // mail. Every attachment inside is separately decoded and put through this same
        // inspection before anything reads it (EmailContainerReader), so the looser body check
        // here is not a hole — the bytes that matter are inspected as themselves.
        if (extension is ".eml" && HasRfc5322Headers(bytes))
        {
            return new("message/rfc822", [".eml"]);
        }

        if (extension is ".csv" or ".txt")
        {
            ValidateText(bytes);
            return extension == ".csv"
                ? new("text/csv", [".csv"])
                : new("text/plain", [".txt"]);
        }

        // `extension` is already constrained to the intake allow-list above, so quoting it back is
        // safe and is the single most useful fact we have: the name claims a format the bytes are
        // not in. Saying only "no recognized signature" left users with nothing to act on.
        throw new UnsafeArchiveException(
            $"The file is named '{extension}' but its contents are not in that format. " +
            "Open it in the application that produced it and use Save As to store a real " +
            $"{extension} file, then upload that.");
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
                    var normalizedName = entry.FullName.Replace('\\', '/');
                    if (!names.Add(normalizedName))
                        throw new UnsafeArchiveException("The OOXML package contains duplicate entry names.");

                    if (entry.Length > _options.MaximumArchiveEntryBytes)
                    {
                        // Sizes only — never the entry NAME, which is attacker-controlled
                        // text inside the archive, and rejection reasons render verbatim
                        // as product copy in the intake UI.
                        throw new UnsafeArchiveException(
                            $"A part of this document expands to {entry.Length / (1024 * 1024)} MB, above the {_options.MaximumArchiveEntryBytes / (1024 * 1024)} MB limit.");
                    }

                    totalCompressed = checked(totalCompressed + entry.CompressedLength);
                    totalExpanded = checked(totalExpanded + entry.Length);
                    if (totalExpanded > _options.MaximumArchiveExpandedBytes)
                    {
                        throw new UnsafeArchiveException(
                            $"This document expands to more than {_options.MaximumArchiveExpandedBytes / (1024 * 1024)} MB in total, which is above the limit.");
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
                    throw new UnsafeArchiveException(
                        MacroRejectionReason(isWordProcessing: true),
                        DocumentInspectionErrorCodes.MacroEnabledDocument);
                }

                return new(
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    [".docx"]);
            }

            if (names.Contains("xl/workbook.xml"))
            {
                var manifest = XDocument.Parse(contentTypes, LoadOptions.None);
                var workbookContentType = manifest.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Override"
                        && string.Equals((string?)element.Attribute("PartName"), "/xl/workbook.xml",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("ContentType")?.Value;
                var macroEnabled = workbookContentType?.Contains("macroEnabled",
                    StringComparison.OrdinalIgnoreCase) == true || names.Contains("xl/vbaProject.bin");
                if (macroEnabled)
                    throw new UnsafeArchiveException(
                        MacroRejectionReason(isWordProcessing: false),
                        DocumentInspectionErrorCodes.MacroEnabledDocument);
                return new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".xlsx"]);
            }

            throw new UnsafeArchiveException("The ZIP payload is not a supported Word or Excel OOXML package.");
        }
        catch (UnsafeArchiveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or DecoderFallbackException
            or OverflowException or XmlException)
        {
            throw new UnsafeArchiveException("The OOXML package is malformed or unreadable.");
        }
    }

    private TypeDetection InspectOleCompound(byte[] bytes, string extension)
    {
        var streamNames = ReadOleDirectoryNames(bytes);
        // Deliberately a FLAT scan of every directory entry, nested storages included: a macro
        // hidden inside an embedded object is still a macro. This stays exactly as strict as it
        // was — only the wording it produces changed.
        if (streamNames.Any(name => name.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase)
                || name.Equals("VBA", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Macros", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnsafeArchiveException(
                MacroRejectionReason(WordExtensions.Contains(extension)),
                DocumentInspectionErrorCodes.MacroEnabledDocument);
        }
        var isWord = streamNames.Contains("WordDocument");
        var isExcel = streamNames.Contains("Workbook") || streamNames.Contains("Book");
        // An Outlook .msg is the SAME OLE compound container as a legacy .doc/.xls, which is why
        // it reaches this method at all — and why it inherits, unchanged, the flat macro scan
        // above. A message carrying a VBA storage is refused exactly as a document would be,
        // BEFORE any property stream is read.
        var isOutlookMessage = OutlookMsgReader.LooksLikeOutlookMessage(streamNames);

        if (extension == ".doc" && isWord && !isExcel && !isOutlookMessage)
            return new("application/msword", [".doc"]);
        if (extension == ".xls" && isExcel && !isWord && !isOutlookMessage)
            return new("application/vnd.ms-excel", [".xls"]);
        if (isOutlookMessage && !isWord && !isExcel)
            return new("application/vnd.ms-outlook", [".msg"]);

        if (extension == ".msg")
        {
            throw new UnsafeArchiveException(
                "Nexora could not confirm this is an Outlook message: the file's internal structure "
                + "does not match its '.msg' name. Open it in Outlook and use Save As to store it "
                + "again, then upload that.");
        }

        // "could not confirm" is the honest claim, and it stays honest in the known false-positive
        // case too: this directory scan is flat, so a genuine workbook that EMBEDS a Word object
        // also lands here. The remedy offered works either way.
        throw new UnsafeArchiveException(WordExtensions.Contains(extension)
            ? "Nexora could not confirm this is a Word document: the file's internal structure " +
              $"does not match its '{extension}' name. Open it in Word and use Save As to store " +
              "it as .docx, then upload that."
            : "Nexora could not confirm this is an Excel workbook: the file's internal structure " +
              $"does not match its '{extension}' name. Open it in Excel and use Save As to store " +
              "it as .xlsx, then upload that.");
    }

    /// <summary>
    /// The macro rejection is a WORKING control — macros are a real malware vector and this file is
    /// never going to be opened. What was broken is what the user was told: the UI turned every
    /// rejection into "the file is damaged, re-export it or send it as a PDF", which is wrong twice
    /// over for a macro-enabled workbook (it is not damaged, and a PDF loses the line items we need).
    ///
    /// <para>
    /// So the reason names the actual cause AND the actual remedy, and is self-contained: it is
    /// persisted verbatim into the occurrence and read back by every surface, including ones that
    /// only ever show this one string.
    /// </para>
    /// </summary>
    private static string MacroRejectionReason(bool isWordProcessing) => isWordProcessing
        ? "This document contains macros (embedded VBA code), which Nexora does not accept. " +
          "Open it in Word, use Save As to keep a macro-free copy as .docx, and upload that, " +
          "or ask the sender for a macro-free version."
        : "This workbook contains macros (embedded VBA code), which Nexora does not accept. " +
          "Open it in Excel, use Save As to keep a macro-free copy as .xlsx, and upload that, " +
          "or ask the sender for a macro-free version.";

    private static readonly IReadOnlySet<string> WordExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx" };

    private HashSet<string> ReadOleDirectoryNames(byte[] bytes)
    {
        const uint EndOfChain = 0xFFFFFFFE;
        const uint FreeSector = 0xFFFFFFFF;
        if (bytes.Length < 512)
            throw new UnsafeArchiveException("The legacy Office document is truncated.");

        var sectorShift = ReadUInt16(bytes, 30);
        var sectorSize = sectorShift switch
        {
            9 => 512,
            12 => 4096,
            _ => throw new UnsafeArchiveException("The legacy Office document has an invalid sector size.")
        };
        if (bytes.Length < sectorSize || bytes.Length % sectorSize != 0)
            throw new UnsafeArchiveException("The legacy Office document has an invalid compound-file length.");

        var sectorCount = (bytes.Length / sectorSize) - 1;
        var fatSectorCount = ReadUInt32(bytes, 44);
        var firstDirectorySector = ReadUInt32(bytes, 48);
        var firstDifatSector = ReadUInt32(bytes, 68);
        var difatSectorCount = ReadUInt32(bytes, 72);
        if (fatSectorCount == 0 || fatSectorCount > sectorCount || difatSectorCount > sectorCount)
            throw new UnsafeArchiveException("The legacy Office document has an invalid allocation table.");

        var fatSectorIds = new List<uint>();
        for (var index = 0; index < 109 && fatSectorIds.Count < fatSectorCount; index++)
        {
            var sector = ReadUInt32(bytes, 76 + index * 4);
            if (sector != FreeSector)
                AddOleSector(fatSectorIds, sector, sectorCount);
        }

        var visitedDifat = new HashSet<uint>();
        var difatSector = firstDifatSector;
        var difatEntries = sectorSize / 4 - 1;
        for (var index = 0u; index < difatSectorCount; index++)
        {
            ValidateOleSector(difatSector, sectorCount, visitedDifat, "DIFAT");
            var offset = OleSectorOffset(difatSector, sectorSize);
            for (var entry = 0; entry < difatEntries && fatSectorIds.Count < fatSectorCount; entry++)
            {
                var sector = ReadUInt32(bytes, offset + entry * 4);
                if (sector != FreeSector)
                    AddOleSector(fatSectorIds, sector, sectorCount);
            }
            difatSector = ReadUInt32(bytes, offset + difatEntries * 4);
        }

        if (fatSectorIds.Count != fatSectorCount)
            throw new UnsafeArchiveException("The legacy Office document allocation table is incomplete.");

        var fat = new List<uint>(fatSectorIds.Count * (sectorSize / 4));
        foreach (var fatSector in fatSectorIds)
        {
            var offset = OleSectorOffset(fatSector, sectorSize);
            for (var entry = 0; entry < sectorSize / 4; entry++)
                fat.Add(ReadUInt32(bytes, offset + entry * 4));
        }

        var directoryBytes = new MemoryStream();
        var visitedDirectory = new HashSet<uint>();
        var directorySector = firstDirectorySector;
        var maximumDirectorySectors = Math.Max(1,
            (int)Math.Ceiling(_options.MaximumArchiveEntries * 128d / sectorSize));
        while (directorySector != EndOfChain)
        {
            if (visitedDirectory.Count >= maximumDirectorySectors)
                throw new UnsafeArchiveException("The legacy Office document contains too many directory entries.");
            ValidateOleSector(directorySector, sectorCount, visitedDirectory, "directory");
            directoryBytes.Write(bytes, OleSectorOffset(directorySector, sectorSize), sectorSize);
            if (directorySector >= fat.Count)
                throw new UnsafeArchiveException("The legacy Office directory chain is invalid.");
            directorySector = fat[(int)directorySector];
        }

        var directory = directoryBytes.ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var nameLength = ReadUInt16(directory, offset + 64);
            var objectType = directory[offset + 66];
            if (objectType is not (1 or 2 or 5) || nameLength is < 2 or > 64 || nameLength % 2 != 0)
                continue;
            var name = Encoding.Unicode.GetString(directory, offset, nameLength - 2);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    private static void AddOleSector(List<uint> sectors, uint sector, int sectorCount)
    {
        if (sector >= sectorCount || sectors.Contains(sector))
            throw new UnsafeArchiveException("The legacy Office allocation table contains an invalid sector.");
        sectors.Add(sector);
    }

    private static void ValidateOleSector(uint sector, int sectorCount, HashSet<uint> visited, string chain)
    {
        if (sector >= sectorCount || !visited.Add(sector))
            throw new UnsafeArchiveException($"The legacy Office {chain} chain is invalid.");
    }

    private static int OleSectorOffset(uint sector, int sectorSize) =>
        checked((int)((sector + 1) * (uint)sectorSize));

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
            throw new UnsafeArchiveException("The legacy Office document is truncated.");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
            throw new UnsafeArchiveException("The legacy Office document is truncated.");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
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

    /// <param name="allowNonUtf8">
    /// HTML only. A portal export is routinely windows-1252 with no charset declaration;
    /// rejecting it for its code page would refuse real RFQs. The property this check exists to
    /// enforce — no NUL, no unsafe control bytes, i.e. "this is text, not a renamed binary" — is
    /// still enforced, byte-wise, on that path. <c>.csv</c>/<c>.txt</c> keep strict UTF-8 exactly
    /// as before.
    /// </param>
    private static void ValidateText(byte[] bytes, bool allowNonUtf8 = false)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            if (!allowNonUtf8)
                throw new UnsafeArchiveException("The text file is not valid UTF-8.");
            if (ContainsBinaryBytes(bytes))
                throw new UnsafeArchiveException("The text file contains binary or unsafe control characters.");
            return;
        }

        if (text.IndexOf('\0') >= 0 || text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f'))
        {
            throw new UnsafeArchiveException("The text file contains binary or unsafe control characters.");
        }
    }

    private static bool ContainsBinaryBytes(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value == 0) return true;
            if (value < 0x20 && value is not 0x09 and not 0x0A and not 0x0C and not 0x0D) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the file opens with an RFC 5322 header block. An <c>.eml</c> has no magic
    /// number, so this IS its signature: at least two well-formed <c>Name: value</c> header lines
    /// before the first blank line, one of which must be a header a real message always carries.
    /// A renamed executable, image or archive fails on the very first line.
    /// </summary>
    private static bool HasRfc5322Headers(byte[] bytes)
    {
        const int ProbeBytes = 8192;
        var probe = bytes.AsSpan(0, Math.Min(bytes.Length, ProbeBytes));
        if (probe.Length >= 3 && probe[0] == 0xEF && probe[1] == 0xBB && probe[2] == 0xBF) probe = probe[3..];

        var header = Encoding.ASCII.GetString(probe).Replace("\r\n", "\n");
        var headerCount = 0;
        var hasRequiredHeader = false;

        foreach (var line in header.Split('\n'))
        {
            if (line.Length == 0) break;              // end of the header block
            if (line[0] is ' ' or '\t') continue;     // folded continuation of the previous header

            var colon = line.IndexOf(':');
            if (colon <= 0) return false;             // not a header line: this is not a message

            var name = line[..colon];
            foreach (var character in name)
            {
                // RFC 5322 field names are printable US-ASCII excluding ':' and space.
                if (character is < '!' or > '~') return false;
            }

            headerCount++;
            if (RequiredEmailHeaders.Contains(name)) hasRequiredHeader = true;
            if (headerCount >= 2 && hasRequiredHeader) return true;
        }

        return headerCount >= 2 && hasRequiredHeader;
    }

    private static readonly IReadOnlySet<string> RequiredEmailHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Received", "From", "Message-ID", "MIME-Version", "Date", "Subject", "Return-Path", "To"
        };

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

    private FileInspectionResult Rejected(
        string? detectedType,
        long length,
        string reason,
        string? errorCode = null)
    {
        var rejection = new FileInspectionResult(
            FileInspectionStatus.Rejected, detectedType, length, reason, "not-run", null);
        return errorCode is null ? rejection : rejection with { ErrorCode = errorCode };
    }

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

    // Single source of truth shared with every intake door (email, manual upload,
    // watched folders) so the intake filters and this inspection gate cannot drift.
    private static readonly IReadOnlySet<string> SupportedExtensions = DocumentIntakeAllowList.Extensions;

    private static readonly IReadOnlyDictionary<string, HashSet<string>> ContentTypeAliases =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = new(StringComparer.OrdinalIgnoreCase) { "application/pdf" },
            ["application/msword"] = new(StringComparer.OrdinalIgnoreCase) { "application/msword", "application/x-msword" },
            ["application/vnd.ms-excel"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.ms-excel", "application/x-msexcel", "application/octet-stream" },
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            ["text/csv"] = new(StringComparer.OrdinalIgnoreCase) { "text/csv", "application/csv", "text/plain" },
            ["text/plain"] = new(StringComparer.OrdinalIgnoreCase) { "text/plain" },
            ["image/png"] = new(StringComparer.OrdinalIgnoreCase) { "image/png" },
            ["image/jpeg"] = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" },
            ["image/gif"] = new(StringComparer.OrdinalIgnoreCase) { "image/gif" },
            ["image/bmp"] = new(StringComparer.OrdinalIgnoreCase) { "image/bmp", "image/x-ms-bmp" },
            ["image/tiff"] = new(StringComparer.OrdinalIgnoreCase) { "image/tiff" },
            ["image/webp"] = new(StringComparer.OrdinalIgnoreCase) { "image/webp" },
            ["text/html"] = new(StringComparer.OrdinalIgnoreCase) { "text/html", "application/xhtml+xml", "text/plain" },
            ["message/rfc822"] = new(StringComparer.OrdinalIgnoreCase) { "message/rfc822", "text/plain" },
            ["application/vnd.ms-outlook"] = new(StringComparer.OrdinalIgnoreCase)
                { "application/vnd.ms-outlook", "application/vnd.ms-office", "application/x-msg", "application/msoutlook" }
        };

    private sealed record TypeDetection(string ContentType, IReadOnlyCollection<string> AllowedExtensions);

    private sealed class UnsafeArchiveException(string message, string? errorCode = null) : Exception(message)
    {
        /// <summary>Null means "no distinct cause" — the caller falls back to document_rejected.</summary>
        public string? ErrorCode { get; } = errorCode;
    }

    private sealed class FileTooLargeException(long observedLength) : Exception
    {
        public long ObservedLength { get; } = observedLength;
    }
}
