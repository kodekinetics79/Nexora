using System.Collections.Generic;
using System.Net;
using System.Text;

namespace ERP_RFQ_Automation.Notifications.Templating
{
    /// <summary>
    /// Turns the lines of a supplier RFQ into the rows of the "rfq-to-supplier" email.
    ///
    /// The template renderer substitutes tokens verbatim, so everything that came from a
    /// customer's document (descriptions, part numbers, maker lists) is HTML-encoded here before
    /// it enters the HTML part. The plain-text part gets the same facts, one line each.
    /// With no lines (a message queued before the per-line contract existed) both parts fall
    /// back to the single-row summary the older payload carried.
    /// </summary>
    public static class RfqToSupplierLineFormatter
    {
        private const string LabelCell = "<td style=\"padding:8px 0; color:#64748b; font-size:14px; vertical-align:top;\">";
        private const string ValueCell = "<td style=\"padding:8px 0; color:#0f172a; font-size:14px;\">";

        public static string HtmlRows(IReadOnlyList<RfqToSupplierLine>? lines, string? fallbackSummary)
        {
            if (lines is null || lines.Count == 0)
                return $"  <tr>{LabelCell}Items</td>{ValueCell}{Encode(fallbackSummary)}</td></tr>";

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.Append("  <tr>").Append(LabelCell).Append("Line ").Append(Encode(line.LineNumber)).Append("</td>")
                  .Append(ValueCell);
                sb.Append("<strong>").Append(Encode(line.Description ?? line.MakerPartNumber ?? line.MaterialCode ?? "Item")).Append("</strong>");
                foreach (var detail in Details(line))
                    sb.Append("<br/>").Append(Encode(detail));
                sb.Append("</td></tr>\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        public static string TextRows(IReadOnlyList<RfqToSupplierLine>? lines, string? fallbackSummary)
        {
            if (lines is null || lines.Count == 0)
                return $"Items:      {fallbackSummary}";

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.Append("Line ").Append(line.LineNumber).Append(": ")
                  .Append(line.Description ?? line.MakerPartNumber ?? line.MaterialCode ?? "Item").Append('\n');
                foreach (var detail in Details(line))
                    sb.Append("    ").Append(detail).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The facts under a line's heading, in the order a supplier reads them.</summary>
        private static IEnumerable<string> Details(RfqToSupplierLine line)
        {
            if (!string.IsNullOrWhiteSpace(line.Maker) && !string.IsNullOrWhiteSpace(line.MakerPartNumber))
                yield return $"Maker: {line.Maker}, part no. {line.MakerPartNumber}";
            else if (!string.IsNullOrWhiteSpace(line.Maker))
                yield return $"Maker: {line.Maker}";
            else if (!string.IsNullOrWhiteSpace(line.MakerPartNumber))
                yield return $"Part no. {line.MakerPartNumber}";

            if (!string.IsNullOrWhiteSpace(line.AcceptableMakers))
                yield return $"Acceptable makers: {line.AcceptableMakers}";

            if (!string.IsNullOrWhiteSpace(line.MaterialCode))
                yield return $"Material code: {line.MaterialCode}";

            var unit = string.IsNullOrWhiteSpace(line.UnitOfMeasure) ? "" : $" {line.UnitOfMeasure}";
            yield return $"Quantity: {line.Quantity}{unit}";

            if (!string.IsNullOrWhiteSpace(line.RequiredBy))
                yield return $"Needed by: {line.RequiredBy}";
        }

        private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
