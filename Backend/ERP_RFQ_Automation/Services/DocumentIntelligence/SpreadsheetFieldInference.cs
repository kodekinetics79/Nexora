namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Works out which column holds which field from the CELLS, for a sheet whose headers the alias
/// map does not recognise.
///
/// <para>The point is not convenience, it is cost and reach. A spreadsheet that reaches the model
/// path is chunked, and above roughly 650 detected line items it is refused outright by the
/// pre-flight ceiling — so today a genuine 2,000-line enquiry from a client whose column
/// spellings we have never seen is not read expensively, it is not read AT ALL. Inferring the
/// mapping puts that document back on the deterministic path, where it costs nothing, cannot be
/// truncated, cannot hallucinate and has no size limit.</para>
///
/// <para>CONSERVATIVE BY CONSTRUCTION, because this half of the profiling is the dangerous half.
/// The non-bid floor asserts absence and can afford to be bold; this asserts "that column is the
/// quantity", and a wrong answer becomes a customer quote that is wrong by an order of magnitude
/// and looks perfectly correct. So:</para>
/// <list type="bullet">
/// <item>ambiguity is refused, never resolved — if two columns could be the quantity, we infer
/// nothing and the document takes the path it takes today;</item>
/// <item>a running sequence is never a quantity, because a line-number column is otherwise
/// indistinguishable from a small one;</item>
/// <item>a mapping is only usable with a quantity AND something naming what is being asked for;</item>
/// <item>every field is optional except those, and an unmapped column is dropped rather than
/// guessed at.</item>
/// </list>
/// <para>Refusing to infer is always safe: the document simply keeps the behaviour it has now.</para>
/// </summary>
public static class SpreadsheetFieldInference
{
    public sealed record Inference(
        IReadOnlyDictionary<string, int> FieldColumns,
        int HeaderRowNumber,
        string Explanation)
    {
        /// <summary>
        /// A mapping is only worth acting on with a quantity and a descriptor. Anything less is
        /// not a line a supplier could price.
        /// </summary>
        public bool IsUsable =>
            FieldColumns.ContainsKey(RfqSpreadsheetFields.Quantity)
            && (FieldColumns.ContainsKey(RfqSpreadsheetFields.ProductName)
                || FieldColumns.ContainsKey(RfqSpreadsheetFields.ManufacturerPartNumber));
    }

    public static Inference Infer(SpreadsheetReading reading)
    {
        var judgeable = reading.Profiles.Where(SpreadsheetColumnShape.IsJudgeable).ToList();
        if (judgeable.Count == 0)
            return new Inference(new Dictionary<string, int>(), reading.HeaderRowNumber,
                "No column carried enough values to profile.");

        // A quantity is the field worth being most careful about, so it is resolved first and
        // under the strictest rule: exactly one candidate, or none at all. A sheet with two
        // plausible quantity columns (ordered vs. packed, say) is precisely where guessing costs
        // money, and it is also rare enough that refusing to guess loses very little.
        var quantityCandidates = judgeable
            .Where(p => SpreadsheetColumnShape.IsQuantity(p)
                        && !p.LooksSequential
                        && !SpreadsheetColumnShape.IsDate(p)
                        && !SpreadsheetColumnShape.IsPrice(p))
            .ToList();

        if (quantityCandidates.Count != 1)
            return new Inference(new Dictionary<string, int>(), reading.HeaderRowNumber,
                quantityCandidates.Count == 0
                    ? "No column has the shape of an order quantity."
                    : $"{quantityCandidates.Count} columns could each be the quantity; refusing to choose.");

        var taken = new HashSet<int>();
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);

        void Take(string field, SpreadsheetColumnProfile? profile)
        {
            if (profile is null || taken.Contains(profile.Column)) return;
            fields[field] = profile.Column;
            taken.Add(profile.Column);
        }

        Take(RfqSpreadsheetFields.Quantity, quantityCandidates[0]);

        // The rest are optional and resolved on first match by column order, so a sheet that
        // repeats a shape maps stably rather than by enumeration order.
        Take(RfqSpreadsheetFields.RequiredDeliveryDate,
            First(judgeable, taken, SpreadsheetColumnShape.IsDate));
        Take(RfqSpreadsheetFields.UnitOfMeasure,
            First(judgeable, taken, SpreadsheetColumnShape.IsUnit));
        Take(RfqSpreadsheetFields.UnitPrice,
            First(judgeable, taken, SpreadsheetColumnShape.IsPrice));
        Take(RfqSpreadsheetFields.ProductName,
            First(judgeable, taken, SpreadsheetColumnShape.IsDescription));
        Take(RfqSpreadsheetFields.ManufacturerPartNumber,
            First(judgeable, taken, SpreadsheetColumnShape.IsIdentifier));

        var named = string.Join(", ", fields.OrderBy(f => f.Value).Select(f => $"{f.Key}=col{f.Value}"));
        return new Inference(fields, reading.HeaderRowNumber,
            $"Inferred from column content: {named}.");
    }

    private static SpreadsheetColumnProfile? First(
        IEnumerable<SpreadsheetColumnProfile> profiles,
        IReadOnlySet<int> taken,
        Func<SpreadsheetColumnProfile, bool> matches)
        => profiles.Where(p => !taken.Contains(p.Column) && matches(p))
                   .OrderBy(p => p.Column)
                   .FirstOrDefault();
}
