using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Xunit.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class RealDocumentBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public RealDocumentBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AuthorizedFixtures_ExerciseLocalProcessingPathsAndPreserveRevisionEvidence()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        QuestPDF.Settings.License = LicenseType.Community;

        var image = RasterizeFirstPage(CreateNativePdf("RFQ 2026 PART ABC123 QTY 100"));
        var original = Encoding.UTF8.GetBytes("RFQ RFQ-DUP-01\nPART ABC123\nQTY 100");
        var revision = Encoding.UTF8.GetBytes("RFQ RFQ-DUP-01\nPART ABC123\nQTY 125");
        var fixtures = new[]
        {
            Fixture.Text("email-body.txt", "RFQ RFQ-EMAIL-01\nBuyer Example Industries\nPART EMAIL-100 QTY 5", "RFQ-EMAIL-01"),
            Fixture.Bytes("customer-rfq.csv", "csv", CreateCsv("RFQ-CSV-01", "CSV-100", "7"), "CSV-100", 1),
            Fixture.Bytes("multi-sheet-rfq.xlsx", "xlsx", CreateWorkbook(), "SHEET-200", 2),
            Fixture.Bytes("native-rfq.pdf", "pdf", CreateNativePdf("RFQ-PDF-01 PART PDF-100 QTY 9"), "RFQ-PDF-01"),
            Fixture.Bytes("scanned-rfq.pdf", "pdf", CreateImagePdf(image), string.Empty, expectOcrPath: true),
            Fixture.Bytes("customer-rfq.docx", "docx", CreateDocx("RFQ RFQ-DOCX-01 PART DOCX-100 QTY 11"), "RFQ-DOCX-01"),
            Fixture.Bytes("rfq-image.png", "png", image, string.Empty, expectOcrPath: true),
            Fixture.Text("supplier-quote.txt", "SUPPLIER QUOTE SQ-01\nPART SUP-100\nPRICE 12.50 USD", "SUP-100"),
            Fixture.Bytes("client-po.csv", "csv", CreateCsv("PO-CLIENT-01", "PO-100", "3"), "PO-100", 1),
            Fixture.Bytes("duplicate-original.txt", "txt", original, "RFQ-DUP-01"),
            Fixture.Bytes("duplicate-forwarded.txt", "txt", original, "RFQ-DUP-01"),
            Fixture.Bytes("revision-02.txt", "txt", revision, "125")
        };

        var storage = new FixtureStorage(fixtures);
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(),
            storage);
        var elapsed = new List<double>();
        var inputs = new Dictionary<string, DocumentExtractionInput>(StringComparer.Ordinal);
        var reviewCount = 0;

        for (var index = 0; index < fixtures.Length; index++)
        {
            var fixture = fixtures[index];
            var timer = Stopwatch.StartNew();
            var input = await reader.ReadAsync(new ExtractionJob
            {
                Id = index + 1,
                BusinessUnitId = 41,
                StoragePath = fixture.StoragePath,
                ContentHash = fixture.Hash,
                FileName = fixture.Name,
                FileType = fixture.Extension
            });
            timer.Stop();
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
            inputs[fixture.Name] = input;

            Assert.Equal($"job:{index + 1}", input.SourceId);
            Assert.NotEqual(ExtractionProcessingPath.ExternalFallback, input.ProcessingPath);
            var searchable = SearchableText(input);
            Assert.True(
                searchable.Contains(fixture.ExpectedToken, StringComparison.OrdinalIgnoreCase),
                $"{fixture.Name} did not contain {fixture.ExpectedToken}; path={input.ProcessingPath}; ocr={input.OcrStatus}; text={searchable}");
            if (fixture.ExpectedRows is { } rows)
            {
                Assert.True(input.IsStructured);
                Assert.Equal(rows, input.StructuredRows!.Count);
                Assert.Equal(ExtractionProcessingPath.DeterministicRules, input.ProcessingPath);
            }
            if (fixture.ExpectOcrPath)
            {
                Assert.Equal(ExtractionProcessingPath.LocalOcr, input.ProcessingPath);
                if (fixture.ExpectOcrSuccess)
                {
                    Assert.Equal(ExtractionOcrStatus.Completed, input.OcrStatus);
                    Assert.True(input.OcrPageCount > 0);
                }
                else
                {
                    Assert.Equal(ExtractionOcrStatus.Failed, input.OcrStatus);
                    reviewCount++;
                }
            }
        }

        Assert.Equal(
            SearchableText(inputs["duplicate-original.txt"]),
            SearchableText(inputs["duplicate-forwarded.txt"]));
        Assert.NotEqual(
            SearchableText(inputs["duplicate-original.txt"]),
            SearchableText(inputs["revision-02.txt"]));

        var ordered = elapsed.OrderBy(value => value).ToArray();
        _output.WriteLine(
            "fixtures={0}; localRate=100%; externalRate=0%; humanReviewRate={1:P1}; p50Ms={2:F1}; p95Ms={3:F1}; governedOcrFailures={4}",
            fixtures.Length,
            (double)reviewCount / fixtures.Length,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            reviewCount);
    }

    private static string SearchableText(DocumentExtractionInput input)
        => input.IsStructured
            ? string.Join(' ', input.StructuredRows!.SelectMany(row => new[]
            {
                row.RfqNo, row.ProductName, row.ManufacturerPartNumber, row.Quantity
            }).Where(value => value != null))
            : input.HeaderText + "\n" + string.Join('\n', input.LineItemRegions);

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
        => ordered[(int)Math.Ceiling(percentile * ordered.Count) - 1];

    private static byte[] CreateCsv(string rfq, string part, string quantity)
        => Encoding.UTF8.GetBytes($"RFQ No,Part Number,Product Name,Quantity,Currency\n{rfq},{part},Test component,{quantity},USD\n");

    private static byte[] CreateWorkbook()
    {
        using var package = new ExcelPackage();
        AddSheet(package, "Inquiry A", "RFQ-SHEET-01", "SHEET-100", "4");
        AddSheet(package, "Inquiry B", "RFQ-SHEET-02", "SHEET-200", "8");
        return package.GetAsByteArray();
    }

    private static void AddSheet(ExcelPackage package, string name, string rfq, string part, string quantity)
    {
        var sheet = package.Workbook.Worksheets.Add(name);
        sheet.Cells[1, 1].Value = "RFQ No";
        sheet.Cells[1, 2].Value = "Part Number";
        sheet.Cells[1, 3].Value = "Product Name";
        sheet.Cells[1, 4].Value = "Quantity";
        sheet.Cells[2, 1].Value = rfq;
        sheet.Cells[2, 2].Value = part;
        sheet.Cells[2, 3].Value = "Test component";
        sheet.Cells[2, 4].Value = quantity;
    }

    private static byte[] CreateDocx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new Body(new Paragraph(new Run(new Text(text)))));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateNativePdf(string text)
        => QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(30);
            page.Content().Text(text).FontSize(18);
        })).GeneratePdf();

    private static byte[] CreateImagePdf(byte[] image)
        => QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(30);
            page.Content().Image(image).FitArea();
        })).GeneratePdf();

    private static byte[] RasterizeFirstPage(byte[] pdf)
    {
        using var document = DocLib.Instance.GetDocReader(pdf, new PageDimensions(2.0));
        using var page = document.GetPageReader(0);
        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var bgra = page.GetImage(new NaiveTransparencyRemover());
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (var index = 0; index < pixels.Length; index++)
        {
            var source = index * 4;
            pixels[index] = (byte)((bgra[source] + bgra[source + 1] + bgra[source + 2]) / 3);
        }

        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(pixels, y * width, width);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed,
                   System.IO.Compression.CompressionLevel.SmallestSize,
                   true))
            zlib.Write(raw.ToArray());

        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        using var header = new MemoryStream();
        WriteBigEndian(header, width);
        WriteBigEndian(header, height);
        header.Write(new byte[] { 8, 0, 0, 0, 0 });
        WritePngChunk(png, "IHDR", header.ToArray());
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        WriteBigEndian(stream, data.Length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        WriteBigEndian(stream, unchecked((int)Crc32(typeBytes.Concat(data).ToArray())));
    }

    private static void WriteBigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static uint Crc32(IEnumerable<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ (crc >> 1);
        }
        return crc ^ 0xffffffffu;
    }

    private sealed record Fixture(
        string Name,
        string Extension,
        byte[] Content,
        string ExpectedToken,
        int? ExpectedRows,
        bool ExpectOcrPath,
        bool ExpectOcrSuccess)
    {
        public string StoragePath => "fixture://" + Name;
        public string Hash => Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();

        public static Fixture Text(string name, string content, string expectedToken)
            => Bytes(name, "txt", Encoding.UTF8.GetBytes(content), expectedToken);

        public static Fixture Bytes(
            string name,
            string extension,
            byte[] content,
            string expectedToken,
            int? expectedRows = null,
            bool expectOcrPath = false,
            bool expectOcrSuccess = false)
            => new(name, extension, content, expectedToken, expectedRows, expectOcrPath, expectOcrSuccess);
    }

    private sealed class FixtureStorage : IEvidenceObjectStorage
    {
        private readonly IReadOnlyDictionary<string, byte[]> _fixtures;

        public FixtureStorage(IEnumerable<Fixture> fixtures)
        {
            _fixtures = fixtures.ToDictionary(fixture => fixture.StoragePath, fixture => fixture.Content);
        }

        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(_fixtures[storageUri], writable: false));
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
