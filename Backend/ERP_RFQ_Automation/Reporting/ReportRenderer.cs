using System;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ERP_RFQ_Automation.Reporting;

public sealed record RenderedReport(string FileName, byte[] Content, string ContentType);

public interface IReportRenderer
{
    RenderedReport Render(ReportDocument document, string format);
}

/// <summary>
/// Renders a <see cref="ReportDocument"/> to PDF (QuestPDF) or Excel (EPPlus), both already
/// referenced and licence-configured by this application.
///
/// <para>The renderer never formats a number and never chooses a currency — it receives cells that
/// were formatted where the record was, and lays them out. That is what keeps a report's figures
/// identical to the screen's, and what stops a symbol being invented at the last hop.</para>
///
/// <para>R6 defers Arabic and RTL beyond this release, so these documents are English-only. That is
/// a recorded deviation from FR-DSH-06's "bilingual where required", not an oversight.</para>
/// </summary>
public sealed class ReportRenderer : IReportRenderer
{
    public RenderedReport Render(ReportDocument document, string format) => format switch
    {
        ReportFormats.Pdf => RenderPdf(document),
        ReportFormats.Xlsx => RenderXlsx(document),
        _ => throw new ArgumentException($"'{format}' is not a supported report format.", nameof(format))
    };

    private static string BaseFileName(ReportDocument document) =>
        $"{Slug(document.Title)}-{document.PeriodFrom:yyyyMMdd}-{document.PeriodTo:yyyyMMdd}";

    private static string Slug(string value) =>
        new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');

    private static RenderedReport RenderPdf(ReportDocument document)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily(Fonts.Calibri));

                page.Header().Column(header =>
                {
                    header.Item().Text(document.Title).FontSize(15).SemiBold();
                    header.Item().Text(document.TenantLabel).FontSize(9).FontColor(Colors.Grey.Darken2);
                    header.Item().Text(document.PeriodLabel).FontSize(9).FontColor(Colors.Grey.Darken2);
                    header.Item().Text($"Generated {document.GeneratedAt:dd MMM yyyy HH:mm} UTC")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                    header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                });

                page.Content().PaddingVertical(8).Column(content =>
                {
                    foreach (var section in document.Sections)
                    {
                        content.Item().PaddingTop(10).Text(section.Heading).FontSize(11).SemiBold();

                        if (section.Rows.Count == 0)
                        {
                            content.Item().PaddingTop(2).Text(
                                    section.EmptyMessage ?? "No rows.")
                                .Italic().FontColor(Colors.Grey.Darken2);
                        }
                        else
                        {
                            content.Item().PaddingTop(4).Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    foreach (var _ in section.Columns) columns.RelativeColumn();
                                });

                                table.Header(head =>
                                {
                                    foreach (var column in section.Columns)
                                    {
                                        head.Cell().Background(Colors.Grey.Lighten3).Padding(3)
                                            .Text(column).SemiBold();
                                    }
                                });

                                foreach (var row in section.Rows)
                                {
                                    for (var i = 0; i < section.Columns.Count; i++)
                                    {
                                        var value = i < row.Length ? row[i] : ReportCell.NotAvailable;
                                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                            .Padding(3).Text(value);
                                    }
                                }
                            });
                        }

                        foreach (var note in section.Notes)
                        {
                            content.Item().PaddingTop(3).Text(note)
                                .FontSize(8).Italic().FontColor(Colors.Grey.Darken3);
                        }
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ").FontSize(8);
                    t.CurrentPageNumber().FontSize(8);
                    t.Span(" of ").FontSize(8);
                    t.TotalPages().FontSize(8);
                });
            });
        }).GeneratePdf();

        return new RenderedReport($"{BaseFileName(document)}.pdf", bytes, "application/pdf");
    }

    private static RenderedReport RenderXlsx(ReportDocument document)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Report");
        var row = 1;

        sheet.Cells[row, 1].Value = document.Title;
        sheet.Cells[row, 1].Style.Font.Bold = true;
        sheet.Cells[row, 1].Style.Font.Size = 14;
        row += 1;
        sheet.Cells[row++, 1].Value = document.TenantLabel;
        sheet.Cells[row++, 1].Value = document.PeriodLabel;
        sheet.Cells[row++, 1].Value = $"Generated {document.GeneratedAt:dd MMM yyyy HH:mm} UTC";
        row += 1;

        foreach (var section in document.Sections)
        {
            sheet.Cells[row, 1].Value = section.Heading;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            row += 1;

            if (section.Rows.Count == 0)
            {
                sheet.Cells[row, 1].Value = section.EmptyMessage ?? "No rows.";
                sheet.Cells[row, 1].Style.Font.Italic = true;
                row += 1;
            }
            else
            {
                for (var c = 0; c < section.Columns.Count; c++)
                {
                    sheet.Cells[row, c + 1].Value = section.Columns[c];
                    sheet.Cells[row, c + 1].Style.Font.Bold = true;
                }
                row += 1;

                foreach (var dataRow in section.Rows)
                {
                    for (var c = 0; c < section.Columns.Count; c++)
                        sheet.Cells[row, c + 1].Value = c < dataRow.Length ? dataRow[c] : ReportCell.NotAvailable;
                    row += 1;
                }
            }

            // Notes are written into the sheet, not dropped. A spreadsheet that loses the
            // qualification keeps only the comfortable half of the answer.
            foreach (var note in section.Notes)
            {
                sheet.Cells[row, 1].Value = note;
                sheet.Cells[row, 1].Style.Font.Italic = true;
                row += 1;
            }

            row += 1;
        }

        if (sheet.Dimension is not null) sheet.Cells[sheet.Dimension.Address].AutoFitColumns(10, 60);

        return new RenderedReport($"{BaseFileName(document)}.xlsx", package.GetAsByteArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }
}
