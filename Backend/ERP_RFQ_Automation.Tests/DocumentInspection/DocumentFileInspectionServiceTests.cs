using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ERP_RFQ_Automation.Security.DocumentInspection;

namespace ERP_RFQ_Automation.Tests.DocumentInspection;

public sealed class DocumentFileInspectionServiceTests
{
    [Theory]
    [InlineData("quote.pdf", "application/pdf", "255044462D312E370A")]
    [InlineData("photo.png", "image/png", "89504E470D0A1A0A0000")]
    [InlineData("photo.jpg", "image/jpeg", "FFD8FFE00000")]
    [InlineData("photo.gif", "image/gif", "474946383961")]
    [InlineData("photo.bmp", "image/bmp", "424D0000")]
    [InlineData("photo.tif", "image/tiff", "49492A000000")]
    [InlineData("photo.webp", "image/webp", "524946460000000057454250")]
    public async Task InspectAsync_ClearsSupportedBinarySignatures(
        string fileName,
        string contentType,
        string hex)
    {
        var result = await InspectAsync(Convert.FromHexString(hex), fileName, contentType);

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
        Assert.Equal(contentType, result.DetectedContentType);
        Assert.Equal(EicarMalwareScanner.EngineName, result.ScannerEngine);
    }

    [Theory]
    [InlineData("legacy.doc", "application/msword", "WordDocument")]
    [InlineData("legacy.xls", "application/vnd.ms-excel", "Workbook")]
    public async Task InspectAsync_ClearsLegacyOfficeOnlyWhenDirectoryMatchesExtension(
        string fileName,
        string contentType,
        string principalStream)
    {
        var result = await InspectAsync(CreateOleCompound(principalStream), fileName, contentType);

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
        Assert.Equal(contentType, result.DetectedContentType);
    }

    [Fact]
    public async Task InspectAsync_RejectsTruncatedOrMislabeledOleDocuments()
    {
        var truncated = await InspectAsync(Convert.FromHexString("D0CF11E0A1B11AE10000"), "legacy.doc");
        var mislabeled = await InspectAsync(CreateOleCompound("Workbook"), "legacy.doc");

        Assert.Equal(FileInspectionStatus.Rejected, truncated.Status);
        Assert.Equal(FileInspectionStatus.Rejected, mislabeled.Status);
    }

    [Fact]
    public async Task InspectAsync_RejectsMacroEnabledLegacyExcel()
    {
        var result = await InspectAsync(
            CreateOleCompound("Workbook", "_VBA_PROJECT_CUR"),
            "legacy.xls",
            "application/vnd.ms-excel");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        // Blocking is the control; naming the cause and the remedy is the message. Both are pinned
        // in detail by MacroPolicyAndRejectionTruthTests.
        Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, result.ErrorCode);
        Assert.Contains("macros", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rows.csv", "text/csv", "sku,qty\nABC,10\n")]
    [InlineData("request.txt", "text/plain", "Please quote ten units.")]
    public async Task InspectAsync_ClearsValidUtf8Text(
        string fileName,
        string contentType,
        string value)
    {
        var result = await InspectAsync(Encoding.UTF8.GetBytes(value), fileName, contentType);

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
        Assert.Equal(contentType, result.DetectedContentType);
    }

    [Fact]
    public async Task InspectAsync_ClearsDocxPackage()
    {
        var package = CreateOpenXmlPackage(
            "word/document.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        var result = await InspectAsync(
            package,
            "rfq.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            result.DetectedContentType);
    }

    [Fact]
    public async Task InspectAsync_ClearsXlsxAndRejectsMacroEnabledWorkbook()
    {
        var xlsx = CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        var xlsm = CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.ms-excel.sheet.macroEnabled.main+xml",
            ("xl/vbaProject.bin", new byte[] { 1, 2, 3 }));

        var xlsxResult = await InspectAsync(xlsx, "rfq.xlsx");
        var xlsmResult = await InspectAsync(xlsm, "rfq.xlsm");

        Assert.Equal(FileInspectionStatus.Cleared, xlsxResult.Status);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            xlsxResult.DetectedContentType);
        Assert.Equal(FileInspectionStatus.Rejected, xlsmResult.Status);
        Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, xlsmResult.ErrorCode);
        Assert.Contains("macros", xlsmResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_DoesNotTreatUnusedMacroEnabledBinDefaultAsXlsm()
    {
        var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                           "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                           "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-excel.sheet.binary.macroEnabled.main\"/>" +
                           "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                           "</Types>";
        var package = CreateZip(
            ("[Content_Types].xml", Encoding.UTF8.GetBytes(contentTypes)),
            ("xl/workbook.xml", "<root/>"u8.ToArray()));

        var result = await InspectAsync(package, "rfq.xlsx");

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.DetectedContentType);
    }

    [Fact]
    public async Task InspectAsync_RejectsSignatureExtensionMismatchBeforeScanning()
    {
        var scanner = new RecordingScanner();
        var service = new DocumentFileInspectionService(scanner);
        await using var stream = new MemoryStream("%PDF-1.7\n"u8.ToArray());

        var result = await service.InspectAsync(new FileInspectionRequest(stream, "renamed.docx"));

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Equal("application/pdf", result.DetectedContentType);
        Assert.Contains("does not match", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(scanner.WasCalled);
    }

    [Fact]
    public async Task InspectAsync_RejectsDeclaredMimeMismatch()
    {
        var result = await InspectAsync(
            "%PDF-1.7\n"u8.ToArray(),
            "quote.pdf",
            "image/png");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("declared content type", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_RejectsUnsupportedAndBinaryText()
    {
        var unsupported = await InspectAsync("hello"u8.ToArray(), "payload.exe");
        var binaryText = await InspectAsync([0x41, 0x00, 0x42], "payload.txt");

        Assert.Equal(FileInspectionStatus.Rejected, unsupported.Status);
        Assert.Equal(FileInspectionStatus.Rejected, binaryText.Status);
        Assert.Contains("control", binaryText.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_RejectsDeclaredOversizeWithoutReading()
    {
        var stream = new ThrowOnReadStream();
        var service = new DocumentFileInspectionService(new EicarMalwareScanner());

        var result = await service.InspectAsync(new FileInspectionRequest(
            stream,
            "large.pdf",
            "application/pdf",
            DocumentInspectionOptions.DefaultMaximumFileBytes + 1));

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("25 MB", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsync_RejectsActualOversizeForNonSeekableStream()
    {
        var options = new DocumentInspectionOptions { MaximumFileBytes = 8 };
        var service = new DocumentFileInspectionService(new EicarMalwareScanner(), options);
        await using var stream = new NonSeekableStream("%PDF-1234"u8.ToArray());

        var result = await service.InspectAsync(new FileInspectionRequest(stream, "large.pdf"));

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.True(result.InspectedLength > options.MaximumFileBytes);
    }

    [Fact]
    public async Task InspectAsync_QuarantinesEicarAndCarriesSignature()
    {
        var eicar = Encoding.ASCII.GetBytes(
            "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

        var result = await InspectAsync(eicar, "eicar.txt", "text/plain");

        Assert.Equal(FileInspectionStatus.Quarantined, result.Status);
        Assert.Equal(EicarMalwareScanner.EngineName, result.ScannerEngine);
        Assert.Equal(EicarMalwareScanner.SignatureName, result.ScannerSignature);
        Assert.Equal(MalwareScanStatus.Infected, result.MalwareStatus);
        Assert.False(result.IsRetryable);
        Assert.Equal("malware_detected", result.ErrorCode);
    }

    [Fact]
    public async Task InspectAsync_QuarantinesWhenScannerThrows()
    {
        var service = new DocumentFileInspectionService(new ThrowingScanner());
        await using var stream = new MemoryStream("sku,qty\nA,1"u8.ToArray());

        var result = await service.InspectAsync(new FileInspectionRequest(stream, "rfq.csv"));

        Assert.Equal(FileInspectionStatus.Quarantined, result.Status);
        Assert.Contains("failed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MalwareScanStatus.Error, result.MalwareStatus);
        Assert.True(result.IsRetryable);
        Assert.Equal("security_scanner_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task InspectAsync_RejectsMalformedZip()
    {
        var result = await InspectAsync([0x50, 0x4B, 0x03, 0x04, 0x01], "rfq.xlsx");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("malformed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_RejectsZipTraversal()
    {
        var package = CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            ("../escape.xml", "bad"u8.ToArray()));

        var result = await InspectAsync(package, "rfq.xlsx");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("traversal", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_RejectsExcessiveZipExpansion()
    {
        var package = CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            ("xl/sharedStrings.xml", new byte[32_000]));
        var options = new DocumentInspectionOptions { MaximumArchiveExpansionRatio = 2 };

        var result = await InspectAsync(package, "rfq.xlsx", options: options);

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("expansion ratio", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_RejectsExcessiveArchiveEntryCount()
    {
        var package = CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            ("xl/a.xml", "a"u8.ToArray()),
            ("xl/b.xml", "b"u8.ToArray()));
        var options = new DocumentInspectionOptions { MaximumArchiveEntries = 3 };

        var result = await InspectAsync(package, "rfq.xlsx", options: options);

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("too many entries", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_RejectsZipThatIsNotOoxml()
    {
        var package = CreateZip(("payload.txt", "hello"u8.ToArray()));

        var result = await InspectAsync(package, "rfq.xlsx");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("content-type manifest", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EicarScanner_DetectsSignatureAcrossReadBoundaries()
    {
        var prefix = new string('A', 7);
        var content = Encoding.ASCII.GetBytes(
            prefix + "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        await using var stream = new ChunkedReadStream(content, 11);

        var result = await new EicarMalwareScanner().ScanAsync(stream);

        Assert.Equal(MalwareScanStatus.Infected, result.Status);
        Assert.Equal(EicarMalwareScanner.SignatureName, result.Signature);
    }

    [Fact]
    public async Task ClamAvScanner_ParsesCleanAndInfectedResponses()
    {
        var clean = await ScanWithClamServerAsync("stream: OK\0");
        var infected = await ScanWithClamServerAsync("stream: Win.Test.Signature FOUND\0");

        Assert.Equal(MalwareScanStatus.Clean, clean.Status);
        Assert.Equal("test-db-1", clean.Signature);
        Assert.Equal(MalwareScanStatus.Infected, infected.Status);
        Assert.Equal("Win.Test.Signature", infected.Signature);
    }

    [Fact]
    public async Task ClamAvScanner_FailsClosedOnTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[256];
            try
            {
                while (await stream.ReadAsync(buffer) > 0)
                {
                    // Deliberately never reply; the client deadline must close the connection.
                }
            }
            catch (IOException) { /* The timed-out client closes or resets the socket. */ }
        });
        var scanner = new ClamAvInstreamMalwareScanner(new ClamAvScannerOptions
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Timeout = TimeSpan.FromMilliseconds(100)
        });
        await using var content = new MemoryStream("safe"u8.ToArray());

        var result = await scanner.ScanAsync(content);

        Assert.Equal(MalwareScanStatus.Unavailable, result.Status);
        Assert.Contains("timed out", result.Reason, StringComparison.OrdinalIgnoreCase);
        await server.WaitAsync(TestWaits.Liveness);
    }

    [Fact]
    public async Task ClamAvScanner_FailsClosedWhenUnavailable()
    {
        var port = ReserveUnusedPort();
        var scanner = new ClamAvInstreamMalwareScanner(new ClamAvScannerOptions
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Timeout = TimeSpan.FromMilliseconds(250)
        });
        await using var content = new MemoryStream("safe"u8.ToArray());

        var result = await scanner.ScanAsync(content);

        Assert.Equal(MalwareScanStatus.Unavailable, result.Status);
        Assert.Equal("ClamAV", result.Engine);
    }

    [Fact]
    public async Task FreshReusableVerdict_StillValidatesEnvelopeWithoutCallingScanner()
    {
        var scanner = new RecordingScanner();
        var service = new DocumentFileInspectionService(scanner);
        var bytes = Encoding.UTF8.GetBytes("RFQ No,Part Number,Quantity\nRFQ-1,ABC-100,5\n");
        await using var stream = new MemoryStream(bytes, writable: false);

        var result = await service.InspectAsync(new FileInspectionRequest(
            stream, "rfq.csv", "text/csv", bytes.Length,
            new ReusableMalwareVerdict("ClamAV", "daily-123", DateTimeOffset.UtcNow)));

        Assert.True(result.IsCleared);
        Assert.True(result.MalwareVerdictReused);
        Assert.False(scanner.WasCalled);
        Assert.Equal("text/csv", result.DetectedContentType);
    }

    private static async Task<FileInspectionResult> InspectAsync(
        byte[] bytes,
        string fileName,
        string? declaredContentType = null,
        DocumentInspectionOptions? options = null)
    {
        var service = new DocumentFileInspectionService(new EicarMalwareScanner(), options);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await service.InspectAsync(new FileInspectionRequest(
            stream,
            fileName,
            declaredContentType,
            bytes.LongLength));
    }

    private static byte[] CreateOpenXmlPackage(
        string principalPart,
        string principalContentType,
        params (string Name, byte[] Content)[] additionalEntries)
    {
        var contentTypes = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                           $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                           $"<Override PartName=\"/{principalPart}\" ContentType=\"{principalContentType}\"/>" +
                           "</Types>";
        return CreateZip(
            [("[Content_Types].xml", Encoding.UTF8.GetBytes(contentTypes)),
             (principalPart, "<root/>"u8.ToArray()),
             .. additionalEntries]);
    }

    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var target = new MemoryStream();
        using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return target.ToArray();
    }

    private static byte[] CreateOleCompound(string principalStream, params string[] additionalEntries)
    {
        const int sectorSize = 512;
        const uint freeSector = 0xFFFFFFFF;
        const uint endOfChain = 0xFFFFFFFE;
        const uint fatSector = 0xFFFFFFFD;
        var bytes = new byte[sectorSize * 3];
        Convert.FromHexString("D0CF11E0A1B11AE1").CopyTo(bytes, 0);
        WriteUInt16(bytes, 24, 0x003E);
        WriteUInt16(bytes, 26, 0x0003);
        WriteUInt16(bytes, 28, 0xFFFE);
        WriteUInt16(bytes, 30, 9);
        WriteUInt16(bytes, 32, 6);
        WriteUInt32(bytes, 44, 1);
        WriteUInt32(bytes, 48, 1);
        WriteUInt32(bytes, 56, 4096);
        WriteUInt32(bytes, 60, endOfChain);
        WriteUInt32(bytes, 68, endOfChain);
        WriteUInt32(bytes, 76, 0);
        for (var index = 1; index < 109; index++) WriteUInt32(bytes, 76 + index * 4, freeSector);

        WriteUInt32(bytes, sectorSize, fatSector);
        WriteUInt32(bytes, sectorSize + 4, endOfChain);
        for (var index = 2; index < sectorSize / 4; index++) WriteUInt32(bytes, sectorSize + index * 4, freeSector);

        WriteDirectoryEntry(bytes, sectorSize * 2, "Root Entry", 5);
        WriteDirectoryEntry(bytes, sectorSize * 2 + 128, principalStream, 2);
        for (var index = 0; index < additionalEntries.Length && index < 2; index++)
            WriteDirectoryEntry(bytes, sectorSize * 2 + (index + 2) * 128, additionalEntries[index], 1);
        return bytes;
    }

    private static void WriteDirectoryEntry(byte[] bytes, int offset, string name, byte objectType)
    {
        var encoded = Encoding.Unicode.GetBytes(name + '\0');
        encoded.CopyTo(bytes, offset);
        WriteUInt16(bytes, offset + 64, (ushort)encoded.Length);
        bytes[offset + 66] = objectType;
        WriteUInt32(bytes, offset + 116, 0xFFFFFFFE);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static async Task<MalwareScanResult> ScanWithClamServerAsync(string response)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[512];
            var zeroFrameSeen = false;
            while (!zeroFrameSeen)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                zeroFrameSeen = read >= 4 &&
                                buffer.AsSpan(0, read).IndexOf(new byte[] { 0, 0, 0, 0 }) >= 0;
            }

            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
        });
        var scanner = new ClamAvInstreamMalwareScanner(new ClamAvScannerOptions
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Timeout = TimeSpan.FromSeconds(2),
            SignatureVersion = "test-db-1"
        });
        await using var content = new MemoryStream("safe content"u8.ToArray());

        var result = await scanner.ScanAsync(content);
        await server.WaitAsync(TestWaits.Liveness);
        return result;
    }

    private static int ReserveUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingScanner : IMalwareScanner
    {
        public bool WasCalled { get; private set; }

        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(MalwareScanResult.Clean("recording"));
        }
    }

    private sealed class ThrowingScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            throw new IOException("scanner offline");
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Must not read.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class NonSeekableStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class ChunkedReadStream : NonSeekableStream
    {
        private readonly int _chunkBytes;

        public ChunkedReadStream(byte[] content, int chunkBytes)
            : base(content)
        {
            _chunkBytes = chunkBytes;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkBytes)], cancellationToken);
    }
}
