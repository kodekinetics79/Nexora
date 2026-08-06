using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Turns an HTML RFQ into the SAME text shape the DOCX reader produces: one line per block, and
/// one line per table row with the cells tab-joined.
///
/// <para>
/// WHY THE TABLE SHAPE IS THE WHOLE POINT. The only HTML handling that existed in this codebase
/// was <c>Regex.Replace(html, "&lt;.*?&gt;", " ")</c> in the mail path. A portal RFQ's line items
/// live in a <c>&lt;table&gt;</c>; a tag strip collapses that grid into one prose blob in which
/// "10" and "6&quot; ball valve" are no longer on the same row, so the quantities and the
/// descriptions can no longer be paired. Emitting tab-joined rows keeps the grid, and matches
/// what <c>ProductionDocumentReader.ExtractTextFromDocx</c> already emits for Word tables, so the
/// downstream chunker sees one familiar shape rather than two.
/// </para>
///
/// <para>
/// SAFETY. HtmlAgilityPack is a tolerant PARSER, not a browser: it never executes script, never
/// resolves an external reference, and — being an HTML parser rather than an XML one — has no
/// DTD, no entity declarations and therefore no entity-expansion ("billion laughs") surface. On
/// top of that this extractor: drops <c>script</c>/<c>style</c>/<c>noscript</c>/<c>iframe</c>/
/// <c>object</c>/<c>embed</c>/<c>svg</c>/<c>head</c> subtrees before reading any text, so a
/// payload can never reach the extracted content or an operator's log; bounds nested-node depth
/// via <see cref="HtmlDocument.OptionMaxNestedChildNodes"/> so a hand-built
/// 100,000-deep&#160;<c>&lt;div&gt;</c> chain cannot exhaust the stack; and caps the emitted
/// character count so a page that expands under normalisation cannot grow without limit.
/// </para>
/// </summary>
public static class HtmlDocumentTextExtractor
{
    /// <summary>Deepest nesting HtmlAgilityPack will build before refusing the document.</summary>
    public const int MaxNestedNodes = 500;

    /// <summary>Ceiling on emitted characters. Inspection already caps input at 25 MB.</summary>
    public const int MaxOutputCharacters = 4_000_000;

    private static readonly HashSet<string> DroppedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "iframe", "frame", "frameset",
        "object", "embed", "applet", "svg", "canvas", "head", "template"
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "footer", "blockquote", "pre", "hr", "dt", "dd"
    };

    /// <summary>
    /// True when <paramref name="bytes"/> begins (after any BOM and leading whitespace) with
    /// markup that only an HTML document produces. Deliberately anchored at the START of the file:
    /// "contains &lt;table&gt; somewhere" would match a base64 blob or a CSV that mentions a tag,
    /// and typing a file by a substring found anywhere in it is how signature checks get bypassed.
    /// </summary>
    public static bool HasHtmlSignature(ReadOnlySpan<byte> bytes)
    {
        var span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];

        var index = 0;
        while (index < span.Length && index < 512 &&
               (span[index] == (byte)' ' || span[index] == (byte)'\t'
                || span[index] == (byte)'\r' || span[index] == (byte)'\n'))
        {
            index++;
        }
        span = span[index..];

        // An Excel/Word "Save as web page" export leads with an XML declaration or a comment
        // before the html element; both are still HTML documents.
        if (StartsWithAscii(span, "<?xml") || StartsWithAscii(span, "<!--"))
        {
            var probe = span[..Math.Min(span.Length, 4096)];
            return IndexOfAscii(probe, "<html") >= 0
                   || IndexOfAscii(probe, "<!doctype html") >= 0
                   || IndexOfAscii(probe, "<table") >= 0;
        }

        return StartsWithAscii(span, "<!doctype html")
               || StartsWithAscii(span, "<html")
               || StartsWithAscii(span, "<head")
               || StartsWithAscii(span, "<body")
               || StartsWithAscii(span, "<table");
    }

    /// <summary>
    /// Extracts text. Never throws for content reasons — an unparseable fragment yields whatever
    /// text was recoverable, and the caller decides whether "nothing recoverable" is terminal.
    /// </summary>
    public static string ExtractText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length == 0 ? string.Empty : ExtractText(Decode(bytes));
    }

    public static string ExtractText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var document = new HtmlDocument
        {
            OptionMaxNestedChildNodes = MaxNestedNodes,
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true,
            OptionCheckSyntax = false
        };

        try
        {
            document.LoadHtml(html);
        }
        catch (Exception)
        {
            // OptionMaxNestedChildNodes signals by throwing. A document too deep to parse is a
            // document with no recoverable text — which the caller turns into a visible
            // disposition, not into an empty success.
            return string.Empty;
        }

        var root = document.DocumentNode;
        foreach (var node in root.Descendants().Where(n => DroppedElements.Contains(n.Name)).ToList())
        {
            node.Remove();
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var emitted = 0;
        Walk(root, lines, current, ref emitted, tableDepth: 0);
        Flush(lines, current);

        return string.Join('\n', lines);
    }

    private static void Walk(HtmlNode node, List<string> lines, StringBuilder current, ref int emitted, int tableDepth)
    {
        if (emitted >= MaxOutputCharacters) return;

        foreach (var child in node.ChildNodes)
        {
            if (emitted >= MaxOutputCharacters) return;

            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    var text = WebUtility.HtmlDecode(child.InnerText);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (current.Length > 0 && !char.IsWhiteSpace(current[^1])) current.Append(' ');
                        var normalised = Collapse(text);
                        current.Append(normalised);
                        emitted += normalised.Length;
                    }
                    continue;

                case HtmlNodeType.Element:
                    if (DroppedElements.Contains(child.Name)) continue;

                    if (string.Equals(child.Name, "tr", StringComparison.OrdinalIgnoreCase))
                    {
                        Flush(lines, current);
                        var cells = new List<string>();
                        foreach (var cell in child.ChildNodes.Where(c =>
                                     c.NodeType == HtmlNodeType.Element &&
                                     (c.Name.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                                      c.Name.Equals("th", StringComparison.OrdinalIgnoreCase))))
                        {
                            var cellText = new StringBuilder();
                            var cellLines = new List<string>();
                            Walk(cell, cellLines, cellText, ref emitted, tableDepth + 1);
                            // A cell holding block content joins with spaces, exactly as the DOCX
                            // reader joins multi-paragraph cells, so one row stays one line.
                            if (cellText.Length > 0) cellLines.Add(cellText.ToString());
                            cells.Add(Collapse(string.Join(' ', cellLines)).Trim());
                        }
                        if (cells.Count > 0 && cells.Any(c => c.Length > 0))
                        {
                            var row = string.Join('\t', cells);
                            lines.Add(row);
                            emitted += row.Length;
                        }
                        continue;
                    }

                    if (BlockElements.Contains(child.Name) || child.Name.Equals("table", StringComparison.OrdinalIgnoreCase))
                    {
                        Flush(lines, current);
                        Walk(child, lines, current, ref emitted, tableDepth);
                        Flush(lines, current);
                        continue;
                    }

                    Walk(child, lines, current, ref emitted, tableDepth);
                    continue;

                default:
                    continue; // comments, DTDs and processing instructions carry no RFQ content
            }
        }
    }

    private static void Flush(List<string> lines, StringBuilder current)
    {
        if (current.Length == 0) return;
        var value = Collapse(current.ToString()).Trim();
        current.Clear();
        if (value.Length > 0) lines.Add(value);
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value)
        {
            //   is the non-breaking space every Office HTML export is full of; left alone it
            // defeats every downstream trim and number parse.
            var isSpace = char.IsWhiteSpace(character) || character == ' ';
            if (isSpace)
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            if (char.IsControl(character)) continue;
            builder.Append(character);
            lastWasSpace = false;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Decodes bytes to text. UTF-8 first (with a BOM honoured), falling back to Latin-1 for the
    /// windows-1252 portal exports that never declare a charset — a decode failure must not lose
    /// the document.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(StripBom(bytes));
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    private static bool StartsWithAscii(ReadOnlySpan<byte> bytes, string prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (char.ToLowerInvariant((char)bytes[index]) != prefix[index]) return false;
        }
        return true;
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> bytes, string needle)
    {
        for (var start = 0; start + needle.Length <= bytes.Length; start++)
        {
            if (StartsWithAscii(bytes[start..], needle)) return start;
        }
        return -1;
    }
}
