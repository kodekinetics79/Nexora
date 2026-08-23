using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using MimeKit;
using MimeKit.Utils;
using OfficeOpenXml;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Builds the byte content of every file in <c>Corpus/</c>. The files are generated ONCE and
/// COMMITTED — tests read the bytes from disk, never from these builders — so the corpus a test
/// run sees is the corpus a reviewer can open in a mail client, Excel or a PDF viewer. This
/// class exists so the corpus is reproducible: see <c>Corpus/README.md</c> and
/// <see cref="ERP_RFQ_Automation.Tests.CorpusRegenerationTests"/> for the regeneration ritual.
///
/// Every value planted here (references, part numbers, quantities) is mirrored in
/// <c>Corpus/corpus-manifest.json</c>, which is the ground truth the acceptance tests assert
/// against. Change a value in one place and the corpus tests will tell you about the other.
/// </summary>
internal static class CorpusGenerator
{
    // Fixed identities: the corpus must be byte-stable in the fields the ingest keys on.
    public const string CustomerAddress = "ahmed@alnoortrading.ae";
    public const string CustomerName = "Ahmed Al Farsi";
    public const string IntakeAddress = "intake@tenant.example";
    public static readonly DateTimeOffset CorpusDate = new(2026, 8, 10, 8, 0, 0, TimeSpan.FromHours(4));

    static CorpusGenerator()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>All corpus files, in one deterministic pass. Returns (fileName, bytes).</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> BuildAll()
    {
        var nativePdf = NativeRfqPdf();
        var goodSimplePdf = GoodSimplePdf();
        var conflictingPdf = ConflictingQuantityPdf();
        var multiSheet = MultiSheetWorkbook();
        var docxTable = RfqTableDocx();
        var legacyXls = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "recognized-layout-rfq.xls"));

        return new List<(string, byte[])>
        {
            ("email-simple-english.eml", SimpleEnglishEmail()),
            ("email-simple-arabic.eml", SimpleArabicEmail()),
            ("email-with-pdf.eml", WithPdfEmail(nativePdf)),
            ("email-duplicate.eml", DuplicateEmail()),
            ("email-revised-rfq.eml", RevisedRfqEmail()),
            ("email-conflicting-quantities.eml", ConflictingQuantitiesEmail(conflictingPdf)),
            ("email-missing-part-numbers.eml", MissingPartNumbersEmail()),
            ("email-forwarded-inner.eml", ForwardedInnerEmail()),
            ("email-multi-attachment.eml", MultiAttachmentEmail(multiSheet, docxTable, legacyXls)),
            ("email-mixed-protected.eml", MixedProtectedEmail(EncryptedPdf(), goodSimplePdf)),
            ("email-noise-newsletter.eml", NoiseNewsletterEmail()),
            ("email-noise-auto-reply.eml", NoiseAutoReplyEmail()),
            ("email-noise-no-reply.eml", NoiseNoReplyEmail()),
            ("doc-native-rfq.pdf", nativePdf),
            ("doc-good-simple.pdf", goodSimplePdf),
            ("doc-conflicting-qty.pdf", conflictingPdf),
            ("doc-scanned-rfq.pdf", ScannedRfqPdf()),
            ("doc-multisheet-rfq.xlsx", multiSheet),
            ("doc-large-rfq.xlsx", LargeWorkbook()),
            ("doc-legacy-rfq.xls", legacyXls),
            ("doc-rfq-table.docx", docxTable),
            ("doc-outlook-rfq.msg", OutlookMsg()),
            ("doc-password-protected.pdf", EncryptedPdf()),
            ("doc-corrupted.docx", CorruptedDocx(docxTable)),
            ("doc-unsupported.pptx", UnsupportedPptx(docxTable)),
        };
    }

    // ------------------------------------------------------------------------------ emails

    private static MimeMessage NewMessage(string subject, string messageId,
        string fromName = CustomerName, string fromAddress = CustomerAddress)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(new MailboxAddress("Intake", IntakeAddress));
        message.Subject = subject;
        message.Date = CorpusDate;
        message.MessageId = messageId;
        return message;
    }

    private static byte[] Bytes(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private static byte[] SimpleEnglishEmail()
    {
        var message = NewMessage("RFQ RFQ-CORPUS-001 - cable tray", "corpus-simple-en@corpus.nexora.example");
        message.Body = new TextPart("plain")
        {
            Text = "Dear team,\r\n\r\nPlease quote 40 nos cable tray 300mm hot dip galvanized "
                 + "(part CT-300-HDG), delivery Jebel Ali by 15 September 2026.\r\n\r\n"
                 + "Reference: RFQ-CORPUS-001\r\n\r\nRegards,\r\nAhmed Al Farsi\r\nAl Noor Trading LLC\r\n"
        };
        return Bytes(message);
    }

    private static byte[] SimpleArabicEmail()
    {
        var message = NewMessage(
            "RFQ RFQ-CORPUS-002 - طلب تسعير كابلات",
            "corpus-simple-ar@corpus.nexora.example");
        message.Body = new TextPart("plain")
        {
            Text = "السادة المحترمون،\r\n"
                 + "الرجاء تقديم أفضل "
                 + "سعر للمواد التالية:\r\n"
                 + "Please quote 250 mtrs power cable 3x2.5mm (part CBL-3C-25) "
                 + "مع التسليم إلى جبل علي.\r\n"
                 + "Reference: RFQ-CORPUS-002\r\nشكراً\r\n"
        };
        return Bytes(message);
    }

    private static byte[] WithPdfEmail(byte[] nativePdf)
    {
        var message = NewMessage("RFQ RFQ-CORPUS-003 - pumps and valves", "corpus-with-pdf@corpus.nexora.example");
        var builder = new BodyBuilder { TextBody = "" }; // attachment-only: the document is the enquiry
        builder.Attachments.Add("rfq-corpus-003.pdf", nativePdf, ContentType.Parse("application/pdf"));
        message.Body = builder.ToMessageBody();
        return Bytes(message);
    }

    private static byte[] DuplicateEmail()
    {
        var message = NewMessage("RFQ RFQ-CORPUS-004 - gaskets", "corpus-duplicate@corpus.nexora.example");
        message.Body = new TextPart("plain")
        {
            Text = "Please quote 100 pcs spiral wound gasket DN80 PN16 (part GSK-DN80).\r\n"
                 + "Reference: RFQ-CORPUS-004\r\n"
        };
        return Bytes(message);
    }

    private static byte[] RevisedRfqEmail()
    {
        var message = NewMessage("RE: RFQ RFQ-CORPUS-001 - cable tray (Revision B)",
            "corpus-revised@corpus.nexora.example");
        message.Date = CorpusDate.AddHours(6);
        message.InReplyTo = "corpus-simple-en@corpus.nexora.example";
        message.References.Add("corpus-simple-en@corpus.nexora.example");
        message.Body = new TextPart("plain")
        {
            Text = "Dear team,\r\n\r\nRevision B of our enquiry RFQ-CORPUS-001: please quote "
                 + "60 nos cable tray 300mm hot dip galvanized (part CT-300-HDG) - the quantity "
                 + "changed from 40 to 60. Everything else is unchanged.\r\n\r\n"
                 + "Reference: RFQ-CORPUS-001\r\nRegards,\r\nAhmed Al Farsi\r\n"
        };
        return Bytes(message);
    }

    private static byte[] ConflictingQuantitiesEmail(byte[] conflictingPdf)
    {
        var message = NewMessage("RFQ RFQ-CORPUS-005 - gate valves",
            "corpus-conflicting@corpus.nexora.example");
        var builder = new BodyBuilder
        {
            TextBody = "Please quote 40 nos gate valve DN100 (part GV-DN100) as per the attached "
                     + "sheet.\r\nReference: RFQ-CORPUS-005\r\n"
        };
        builder.Attachments.Add("rfq-corpus-005.pdf", conflictingPdf, ContentType.Parse("application/pdf"));
        message.Body = builder.ToMessageBody();
        return Bytes(message);
    }

    private static byte[] MissingPartNumbersEmail()
    {
        var message = NewMessage("RFQ RFQ-CORPUS-006 - piping fittings",
            "corpus-missing-parts@corpus.nexora.example");
        message.Body = new TextPart("plain")
        {
            Text = "Please quote the following (no part numbers available, description only):\r\n"
                 + "1) 10 pcs weld neck flange DN80 PN16 carbon steel\r\n"
                 + "2) 5 pcs elbow 90 degree DN80 seamless\r\n"
                 + "Reference: RFQ-CORPUS-006\r\n"
        };
        return Bytes(message);
    }

    private static byte[] ForwardedInnerEmail()
    {
        var inner = new MimeMessage();
        inner.From.Add(new MailboxAddress("Site Engineer", "site@gulfprojects.example"));
        inner.To.Add(new MailboxAddress("Procurement", "procurement@gulfprojects.example"));
        inner.Subject = "RFQ RFQ-CORPUS-007 - chemicals";
        inner.Date = CorpusDate.AddDays(-1);
        inner.MessageId = "corpus-inner@corpus.nexora.example";
        inner.Body = new TextPart("plain")
        {
            Text = "Please quote 15 drums caustic soda flakes 25kg (part CHM-CS-25).\r\n"
                 + "Reference: RFQ-CORPUS-007\r\n"
        };

        var outer = NewMessage("FW: RFQ RFQ-CORPUS-007 - chemicals",
            "corpus-forwarded@corpus.nexora.example",
            fromName: "Procurement", fromAddress: "procurement@gulfprojects.example");
        var mixed = new Multipart("mixed")
        {
            new TextPart("plain")
            {
                Text = "Forwarding the site enquiry below - please quote 15 drums caustic soda "
                     + "flakes 25kg (part CHM-CS-25). Reference: RFQ-CORPUS-007\r\n"
            },
            new MessagePart
            {
                Message = inner,
                // Real clients forward-as-attachment with an explicit disposition; without it
                // the message part is inline and never reaches the attachment fan-out at all.
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
                {
                    FileName = "original-enquiry.eml"
                }
            }
        };
        outer.Body = mixed;
        return Bytes(outer);
    }

    private static byte[] MultiAttachmentEmail(byte[] xlsx, byte[] docx, byte[] xls)
    {
        var message = NewMessage("RFQ RFQ-CORPUS-008 - spares package",
            "corpus-multi-attachment@corpus.nexora.example");
        var builder = new BodyBuilder { TextBody = "" };
        builder.Attachments.Add("rfq-corpus-multisheet.xlsx", xlsx, ContentType.Parse(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        builder.Attachments.Add("rfq-corpus-table.docx", docx, ContentType.Parse(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        builder.Attachments.Add("rfq-corpus-legacy.xls", xls, ContentType.Parse("application/vnd.ms-excel"));
        message.Body = builder.ToMessageBody();
        return Bytes(message);
    }

    private static byte[] MixedProtectedEmail(byte[] encryptedPdf, byte[] goodPdf)
    {
        var message = NewMessage("RFQ RFQ-CORPUS-009 - protected documents",
            "corpus-mixed-protected@corpus.nexora.example");
        var builder = new BodyBuilder { TextBody = "" };
        builder.Attachments.Add("rfq-corpus-protected.pdf", encryptedPdf, ContentType.Parse("application/pdf"));
        builder.Attachments.Add("rfq-corpus-009.pdf", goodPdf, ContentType.Parse("application/pdf"));
        message.Body = builder.ToMessageBody();
        return Bytes(message);
    }

    private static byte[] NoiseNewsletterEmail()
    {
        var message = NewMessage("Weekly steel market digest - prices are moving",
            "corpus-noise-newsletter@corpus.nexora.example",
            fromName: "Steel Digest", fromAddress: "digest@steelnews.example");
        message.Headers.Add("List-Id", "Steel Digest <digest.steelnews.example>");
        message.Headers.Add("List-Unsubscribe", "<mailto:unsubscribe@steelnews.example>");
        message.Headers.Add("Precedence", "bulk");
        message.Body = new TextPart("plain")
        {
            Text = "This week: rebar up 3%, HRC flat. Please quote us your feedback!\r\n"
        };
        return Bytes(message);
    }

    private static byte[] NoiseAutoReplyEmail()
    {
        var message = NewMessage("Automatic reply: RFQ RFQ-CORPUS-001 - cable tray",
            "corpus-noise-autoreply@corpus.nexora.example");
        message.Headers.Add("Auto-Submitted", "auto-replied");
        message.Body = new TextPart("plain")
        {
            Text = "I am out of the office until 20 August with limited access to email.\r\n"
        };
        return Bytes(message);
    }

    private static byte[] NoiseNoReplyEmail()
    {
        var message = NewMessage("Your order confirmation #88231",
            "corpus-noise-noreply@corpus.nexora.example",
            fromName: "Web Shop", fromAddress: "no-reply@shop.example");
        message.Body = new TextPart("plain")
        {
            Text = "Thank you for your order. This mailbox is not monitored.\r\n"
        };
        return Bytes(message);
    }

    // --------------------------------------------------------------------------- documents

    private static byte[] NativeRfqPdf() => TextPdf(
        "REQUEST FOR QUOTATION  RFQ-CORPUS-003",
        "Buyer: Gulf Projects Co",
        "Bid closing: 15 September 2026",
        "",
        "Item 1: Part PMP-15KW  Centrifugal pump 15kW  Qty 3 EA",
        "Item 2: Part VLV-DN50  Ball valve DN50 PN16  Qty 12 EA");

    private static byte[] GoodSimplePdf() => TextPdf(
        "REQUEST FOR QUOTATION  RFQ-CORPUS-009",
        "Buyer: Al Noor Trading LLC",
        "",
        "Item 1: Part INS-PT-1000  Pressure transmitter 0-10 bar  Qty 2 EA");

    private static byte[] ConflictingQuantityPdf() => TextPdf(
        "REQUEST FOR QUOTATION  RFQ-CORPUS-005",
        "Buyer: Al Noor Trading LLC",
        "",
        "Item 1: Part GV-DN100  Gate valve DN100 PN16  Qty 45 EA",
        "(This sheet supersedes the covering email.)");

    private static byte[] TextPdf(params string[] lines)
        => QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(40);
            page.Content().Column(column =>
            {
                foreach (var line in lines)
                    column.Item().Text(line).FontSize(14);
            });
        })).GeneratePdf();

    /// <summary>A PDF whose only content is a raster image of RFQ text — OCR is the sole way in.</summary>
    private static byte[] ScannedRfqPdf()
    {
        var textPdf = QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(30);
            page.Content().Text("RFQ CORPUS-OCR-01 PART OCR-100 QTY 25").FontSize(20);
        })).GeneratePdf();
        var image = RasterizeFirstPageToPng(textPdf);
        return QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(30);
            page.Content().Image(image).FitArea();
        })).GeneratePdf();
    }

    private static byte[] MultiSheetWorkbook()
    {
        using var package = new ExcelPackage();
        AddRecognizedSheet(package, "Inquiry A", ("RFQ-CORPUS-010A", "SHT-100", "Cable gland M20 brass", "4", "EA"));
        AddRecognizedSheet(package, "Inquiry B", ("RFQ-CORPUS-010B", "SHT-200", "Junction box IP66", "8", "EA"));
        return package.GetAsByteArray();
    }

    private static byte[] LargeWorkbook()
    {
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("BOQ");
        sheet.Cells[1, 1].Value = "RFQ No";
        sheet.Cells[1, 2].Value = "Part Number";
        sheet.Cells[1, 3].Value = "Product Name";
        sheet.Cells[1, 4].Value = "Quantity";
        sheet.Cells[1, 5].Value = "UOM";
        for (var i = 1; i <= 300; i++)
        {
            sheet.Cells[i + 1, 1].Value = "RFQ-CORPUS-013";
            sheet.Cells[i + 1, 2].Value = $"LG-{i:D4}";
            sheet.Cells[i + 1, 3].Value = $"Bulk line item {i}";
            sheet.Cells[i + 1, 4].Value = i;
            sheet.Cells[i + 1, 5].Value = "EA";
        }
        return package.GetAsByteArray();
    }

    private static void AddRecognizedSheet(
        ExcelPackage package, string name, (string Rfq, string Part, string Product, string Qty, string Uom) row)
    {
        var sheet = package.Workbook.Worksheets.Add(name);
        sheet.Cells[1, 1].Value = "RFQ No";
        sheet.Cells[1, 2].Value = "Part Number";
        sheet.Cells[1, 3].Value = "Product Name";
        sheet.Cells[1, 4].Value = "Quantity";
        sheet.Cells[1, 5].Value = "UOM";
        sheet.Cells[2, 1].Value = row.Rfq;
        sheet.Cells[2, 2].Value = row.Part;
        sheet.Cells[2, 3].Value = row.Product;
        sheet.Cells[2, 4].Value = row.Qty;
        sheet.Cells[2, 5].Value = row.Uom;
    }

    private static byte[] RfqTableDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            var table = new Table();
            table.AppendChild(TableRowOf("RFQ No", "Part Number", "Description", "Qty", "UOM"));
            table.AppendChild(TableRowOf("RFQ-CORPUS-011", "TBL-100", "Gate valve DN80 PN16", "6", "EA"));
            table.AppendChild(TableRowOf("RFQ-CORPUS-011", "TBL-200", "Check valve DN80 PN16", "4", "EA"));
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body(
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(
                    "Request for Quotation RFQ-CORPUS-011"))),
                table));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static TableRow TableRowOf(params string[] cells)
    {
        var row = new TableRow();
        foreach (var cell in cells)
            row.AppendChild(new TableCell(new Paragraph(new Run(
                new DocumentFormat.OpenXml.Wordprocessing.Text(cell)))));
        return row;
    }

    /// <summary>
    /// A REAL Outlook compound file (CFB) with the MAPI property streams the production
    /// <c>OutlookMsgReader</c> reads: subject (0037), body (1000), sender SMTP (5D01) and
    /// display-to (0E04), all as Unicode (001F) streams, plus the __properties marker that
    /// makes <c>LooksLikeOutlookMessage</c> true.
    /// </summary>
    private static byte[] OutlookMsg() => new CompoundFileBuilder()
        .AddStream("__properties_version1.0", new byte[64])
        .AddStream("__substg1.0_0037001F", "RFQ RFQ-CORPUS-012 - instrumentation")
        .AddStream("__substg1.0_1000001F",
            "Please quote 2 pcs pressure transmitter 0-10 bar (part INS-PT-1000).\r\n"
            + "Reference: RFQ-CORPUS-012\r\n")
        .AddStream("__substg1.0_0C1A001F", CustomerName)
        .AddStream("__substg1.0_5D01001F", CustomerAddress)
        .AddStream("__substg1.0_0E04001F", IntakeAddress)
        .Build();

    private static byte[] CorruptedDocx(byte[] validDocx)
    {
        // Truncated mid-archive: the zip local header signature survives, the central
        // directory does not — the shape a dropped connection actually produces.
        var truncated = new byte[Math.Min(512, validDocx.Length / 4)];
        Array.Copy(validDocx, truncated, truncated.Length);
        return truncated;
    }

    private static byte[] UnsupportedPptx(byte[] anyZipBytes)
    {
        // Valid zip bytes under an extension the intake allow-list refuses: the rejection
        // must be for the EXTENSION, before any content sniffing matters.
        return anyZipBytes;
    }

    // --------------------------------------------------------- encrypted PDF (RC4-40, R2)

    /// <summary>
    /// A genuinely password-protected PDF, produced without any external tool. QuestPDF
    /// (SkiaSharp) cannot write encrypted PDFs and PdfPig's builder does not either, so this
    /// hand-writes a minimal 1-page PDF and applies the PDF 1.7 Standard security handler,
    /// revision 2 (RC4, 40-bit) — the algorithm every conforming reader (including PdfPig's
    /// decryption detector, which is what <c>ProductionDocumentReader</c> relies on) treats as
    /// an encrypted document. User and owner password are both "nexora".
    /// </summary>
    internal static byte[] EncryptedPdf()
    {
        var password = "nexora";
        byte[] pad =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56,
            0xFF, 0xFA, 0x01, 0x08, 0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
            0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
        };
        static byte[] PadPassword(string pwd, byte[] pad)
        {
            var raw = Encoding.ASCII.GetBytes(pwd);
            var padded = new byte[32];
            var n = Math.Min(raw.Length, 32);
            Array.Copy(raw, padded, n);
            Array.Copy(pad, 0, padded, n, 32 - n);
            return padded;
        }

        // Deterministic document ID (the corpus must not churn on regeneration).
        var id = MD5.HashData(Encoding.ASCII.GetBytes("nexora-corpus-protected-pdf"));
        const int permissions = -44; // print + modify forbidden bits pattern used by common tools

        // Algorithm 3 (R2): O = RC4(first 5 bytes of MD5(padded owner pwd), padded user pwd).
        var ownerKey = MD5.HashData(PadPassword(password, pad)).AsSpan(0, 5).ToArray();
        var oValue = Rc4(ownerKey, PadPassword(password, pad));

        // Algorithm 2: file key = first 5 bytes of MD5(padded user pwd || O || P(LE int32) || ID).
        var md5Input = new MemoryStream();
        md5Input.Write(PadPassword(password, pad));
        md5Input.Write(oValue);
        md5Input.Write(BitConverter.GetBytes(permissions)); // little-endian
        md5Input.Write(id);
        var fileKey = MD5.HashData(md5Input.ToArray()).AsSpan(0, 5).ToArray();

        // Algorithm 4 (R2): U = RC4(file key, padding constant).
        var uValue = Rc4(fileKey, pad);

        static byte[] ObjectKey(byte[] fileKey, int objectNumber, int generation)
        {
            var input = new byte[fileKey.Length + 5];
            fileKey.CopyTo(input, 0);
            input[fileKey.Length + 0] = (byte)(objectNumber & 0xFF);
            input[fileKey.Length + 1] = (byte)((objectNumber >> 8) & 0xFF);
            input[fileKey.Length + 2] = (byte)((objectNumber >> 16) & 0xFF);
            input[fileKey.Length + 3] = (byte)(generation & 0xFF);
            input[fileKey.Length + 4] = (byte)((generation >> 8) & 0xFF);
            var digest = MD5.HashData(input);
            var keyLength = Math.Min(fileKey.Length + 5, 16);
            return digest.AsSpan(0, keyLength).ToArray();
        }

        var content = Encoding.ASCII.GetBytes(
            "BT /F1 16 Tf 72 720 Td (RFQ RFQ-CORPUS-PROTECTED - open with password nexora) Tj ET");
        var encryptedContent = Rc4(ObjectKey(fileKey, 4, 0), content);

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        var objects = new (int Number, byte[] Body)[]
        {
            (1, Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>")),
            (2, Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
            (3, Encoding.ASCII.GetBytes(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>")),
            (4, Encoding.ASCII.GetBytes($"<< /Length {encryptedContent.Length} >>\nstream\n")
                .Concat(encryptedContent).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray()),
            (5, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")),
            (6, Encoding.ASCII.GetBytes(
                $"<< /Filter /Standard /V 1 /R 2 /O <{Hex(oValue)}> /U <{Hex(uValue)}> /P {permissions} >>")),
        };

        var pdf = new MemoryStream();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Length + 1];
        foreach (var (number, body) in objects)
        {
            offsets[number] = pdf.Position;
            Write($"{number} 0 obj\n");
            pdf.Write(body);
            Write("\nendobj\n");
        }
        var xrefOffset = pdf.Position;
        Write($"xref\n0 {objects.Length + 1}\n");
        Write("0000000000 65535 f \n");
        for (var number = 1; number <= objects.Length; number++)
            Write($"{offsets[number]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 6 0 R "
            + $"/ID [<{Hex(id)}> <{Hex(id)}>] >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        int x = 0, y = 0;
        for (var index = 0; index < data.Length; index++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[index] = (byte)(data[index] ^ s[(s[x] + s[y]) & 0xFF]);
        }
        return output;
    }

    // ------------------------------------------------------------------- PDF rasterization

    /// <summary>Rasterizes page 1 to an 8-bit grayscale PNG (same approach as
    /// <c>RealDocumentBenchmarkTests</c> — no imaging library needed).</summary>
    private static byte[] RasterizeFirstPageToPng(byte[] pdf)
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
        using (var zlib = new ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, true))
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
}
