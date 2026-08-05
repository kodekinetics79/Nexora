using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories;

/// <summary>
/// Which brands the tenant's customers actually ask for, and how concentrated that
/// demand is.
///
/// This is the only analytic in the pilot set that tells the owner something he does
/// not already know. He knows his deadlines and he knows his own throughput; he does
/// not know that 2,726 of his 2,966 extracted lines name a manufacturer across 449
/// distinct spellings, that CROUSE HINDS/EATON accounts for 143 lines, or that
/// LEDVANCE has been asked for in units of 16,356. That is a purchasing and stocking
/// conversation, and it falls straight out of data that already exists — no catalog,
/// no customer identity, no FX.
///
/// THE SPELLINGS ARE THE HARD PART. 449 raw values do not mean 449 brands: buyers type
/// "EATON", "Eaton Corp.", "CROUSE HINDS/EATON" and "eaton  crouse-hinds" on different
/// documents. Grouping on the raw column would scatter one brand across a dozen rows and
/// make the concentration look far flatter than it is. Normalisation here is
/// deliberately CONSERVATIVE — case, punctuation, whitespace and a short list of
/// corporate suffixes — because an aggressive rule that merges two genuinely different
/// manufacturers produces a confident wrong answer, and <see cref="BrandDemandRowDTO.Variants"/>
/// exposes how many spellings were folded so a reader can audit any row.
/// </summary>
public sealed class BrandDemandRepository
{
    private readonly ErpRfqAutomationContext _context;

    public BrandDemandRepository(ErpRfqAutomationContext context) => _context = context;

    /// <summary>Corporate form suffixes stripped after punctuation removal.</summary>
    private static readonly HashSet<string> CorporateSuffixes = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "LTD", "LIMITED", "LLC", "PLC", "CORP", "CORPORATION",
        "CO", "COMPANY", "GMBH", "AG", "SA", "SAS", "BV", "NV", "SPA", "SRL", "PTE",
        "PVT", "PRIVATE", "LLP", "KG", "AB", "AS", "OY"
    };

    public async Task<BrandDemandDTO> GetAsync(
        long businessUnitId, DateTime? from, DateTime? to, int topN = 25,
        CancellationToken cancellationToken = default)
    {
        var query = _context.LeadItems.AsNoTracking()
            .Where(li => li.Lead.BusinessUnitId == businessUnitId);
        if (from.HasValue) query = query.Where(li => li.Lead.CreatedDate >= from.Value);
        if (to.HasValue) query = query.Where(li => li.Lead.CreatedDate <= to.Value);

        var rows = await query
            .Select(li => new { li.LeadId, li.ManufacturerName, li.Quantity })
            .ToListAsync(cancellationToken);

        var totalLines = rows.Count;
        var named = rows.Where(r => !string.IsNullOrWhiteSpace(r.ManufacturerName)).ToList();

        var groups = named
            .Select(r => new
            {
                r.LeadId,
                Raw = r.ManufacturerName!.Trim(),
                Key = Normalize(r.ManufacturerName!),
                r.Quantity
            })
            .Where(r => r.Key.Length > 0)
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                // Display form = the most common raw spelling, so the row reads the way the
                // customers write it rather than in a normalised shape nobody would recognise.
                var display = g.GroupBy(x => x.Raw, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .First().Key;
                return new
                {
                    Key = g.Key,
                    Display = display,
                    Variants = g.Select(x => x.Raw).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Lines = g.Count(),
                    Documents = g.Select(x => x.LeadId).Distinct().Count(),
                    Quantity = g.Sum(x => (long)x.Quantity)
                };
            })
            .OrderByDescending(g => g.Lines)
            .ThenByDescending(g => g.Documents)
            .ThenBy(g => g.Display, StringComparer.Ordinal)
            .ToList();

        var linesWithManufacturer = groups.Sum(g => g.Lines);

        // Share is taken over ALL lines, not only the ones naming a brand. Dividing by the
        // named subset would let a brand on 143 of 2,726 named lines read as a larger part
        // of the book than it is.
        decimal Share(int lines) => totalLines == 0 ? 0m : decimal.Round(lines * 100m / totalLines, 1);

        var topRows = groups
            .Take(Math.Clamp(topN, 1, 200))
            .Select(g => new BrandDemandRowDTO(
                g.Display, g.Key, g.Variants, g.Lines, g.Documents, g.Quantity, Share(g.Lines)))
            .ToList();

        return new BrandDemandDTO(
            DateTime.UtcNow,
            from,
            to,
            totalLines,
            linesWithManufacturer,
            totalLines - linesWithManufacturer,
            groups.Count,
            named.Select(r => r.ManufacturerName!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Share(groups.Take(5).Sum(g => g.Lines)),
            topRows,
            "Quantities are summed across mixed units of measure (each, metres, boxes, sets) "
            + "and are indicative of relative scale only, never a total. Line and document "
            + "counts are exact.");
    }

    /// <summary>
    /// Upper-cased, punctuation-free, whitespace-collapsed, corporate-suffix-stripped.
    /// Diacritics are folded so "SIEMENS" and "SIEMÉNS" meet.
    ///
    /// Deliberately does NOT attempt fuzzy or token-subset matching: "EATON" and
    /// "CROUSE HINDS EATON" stay separate rows. They may well be the same commercial
    /// relationship, but deciding that is a judgement about the customer's business, and
    /// a normaliser that quietly makes it would produce a concentration figure nobody
    /// could check.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var folded = raw.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');
        }

        var tokens = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Strip trailing corporate forms only — "CO" leading a name ("CO OPERATIVE") is
        // not a suffix, and a name that is nothing BUT suffixes keeps its tokens rather
        // than normalising to the empty string.
        while (tokens.Count > 1 && CorporateSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join(' ', tokens);
    }
}
