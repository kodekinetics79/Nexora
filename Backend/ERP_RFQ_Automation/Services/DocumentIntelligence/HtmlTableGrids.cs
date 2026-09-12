using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HtmlAgilityPack;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Reads an HTML document into the same positional grids a Word document's tables produce, so
/// the one set of table readers (<see cref="DocxTableParser"/>, <see cref="DocxFormBlockParser"/>)
/// serves both.
///
/// <para>Sourcing portals export the SAME event print as a Word file or as an HTML page named
/// <c>.doc</c> — SAP Ariba's "print version of the event" is one page whether the buyer saved it
/// as Word or as web. Until now the Word one was read deterministically, table by table, and
/// the HTML one was refused at the door for not being a Word file. A rep cannot be asked to
/// know the difference.</para>
/// </summary>
public static class HtmlTableGrids
{
    /// <summary>Paragraphs retained from outside the tables; the rest is the tables' business.</summary>
    public const int MaxParagraphs = 200;

    /// <param name="Grids">One grid per TOP-LEVEL table, in document order. Ragged rows are tolerated downstream.</param>
    /// <param name="Paragraphs">Block text outside every table — the "Label: value" lines and prose a Word body would carry.</param>
    public sealed record Reading(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<string?>>> Grids,
        IReadOnlyList<string> Paragraphs);

    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet",
        "svg", "canvas", "head", "template"
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "dt", "dd", "section", "article"
    };

    public static Reading Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var empty = new Reading(Array.Empty<IReadOnlyList<IReadOnlyList<string?>>>(), Array.Empty<string>());
        if (bytes.Length == 0) return empty;

        var document = new HtmlDocument
        {
            OptionMaxNestedChildNodes = HtmlDocumentTextExtractor.MaxNestedNodes,
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true,
            OptionCheckSyntax = false
        };
        try
        {
            document.LoadHtml(HtmlDocumentTextExtractor.Decode(bytes));
        }
        catch (Exception)
        {
            return empty;
        }

        var root = document.DocumentNode;
        foreach (var node in root.Descendants().Where(n => Dropped.Contains(n.Name)).ToList())
            node.Remove();

        // TOP-LEVEL tables only, for the same reason DocxTableParser reads only top-level Word
        // tables: a layout table wrapping the line grid would otherwise be read twice.
        var grids = root.Descendants("table")
            .Where(table => !table.Ancestors("table").Any())
            .Select(BuildGrid)
            .Where(grid => grid.Count > 0)
            .ToList();

        var paragraphs = new List<string>();
        foreach (var block in root.Descendants().Where(n => BlockElements.Contains(n.Name)))
        {
            if (block.Ancestors("table").Any()) continue;
            // Only the innermost block carries text; a div wrapping paragraphs is not a paragraph.
            if (block.Descendants().Any(child => BlockElements.Contains(child.Name))) continue;
            var text = Normalise(block.InnerText);
            if (text.Length == 0) continue;
            paragraphs.Add(text);
            if (paragraphs.Count >= MaxParagraphs) break;
        }

        return new Reading(grids, paragraphs);
    }

    /// <summary>
    /// Flattens one table positionally, the way <c>DocxTableParser.BuildGrid</c> flattens a Word
    /// table: a cell spanning columns occupies its first column and pads the rest, so every
    /// column to its right keeps its own index.
    /// </summary>
    private static List<IReadOnlyList<string?>> BuildGrid(HtmlNode table)
    {
        var grid = new List<IReadOnlyList<string?>>();
        foreach (var row in table.Descendants("tr"))
        {
            // A row belongs to the nearest table above it; nested tables' rows are theirs.
            if (row.Ancestors("table").First() != table) continue;

            var cells = new List<string?>();
            foreach (var cell in row.ChildNodes.Where(n =>
                         n.Name.Equals("td", StringComparison.OrdinalIgnoreCase)
                         || n.Name.Equals("th", StringComparison.OrdinalIgnoreCase)))
            {
                var text = CellText(cell);
                var span = Math.Clamp(cell.GetAttributeValue("colspan", 1), 1, 64);
                for (var offset = 0; offset < span; offset++)
                    cells.Add(offset == 0 ? text : null);
            }
            if (cells.Count > 0) grid.Add(cells);
        }
        return grid;
    }

    /// <summary>
    /// A cell's text with its line structure kept: a <c>&lt;br&gt;</c> or a block inside the cell
    /// is a line break, as a paragraph inside a Word cell is. The Ariba item text and PO text
    /// are multi-line specifications, and flattening them loses where one clause ends.
    /// </summary>
    private static string? CellText(HtmlNode cell)
    {
        var builder = new StringBuilder();
        Append(cell, builder);
        var lines = builder.ToString().Split('\n')
            .Select(Normalise)
            .Where(line => line.Length > 0)
            .ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static void Append(HtmlNode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                builder.Append(HtmlEntity.DeEntitize(child.InnerText));
                continue;
            }
            if (child.NodeType != HtmlNodeType.Element) continue;
            if (child.Name.Equals("br", StringComparison.OrdinalIgnoreCase)) { builder.Append('\n'); continue; }
            var block = BlockElements.Contains(child.Name) || child.Name.Equals("tr", StringComparison.OrdinalIgnoreCase);
            if (block) builder.Append('\n');
            Append(child, builder);
            if (block) builder.Append('\n');
        }
    }

    private static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decoded = HtmlEntity.DeEntitize(text).Replace(' ', ' ');
        var parts = decoded.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
