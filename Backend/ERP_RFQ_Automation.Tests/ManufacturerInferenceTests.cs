using ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The inference is the point at which the platform starts putting words in the document's
/// mouth, so every refusal is asserted here, not assumed: two makers in one description, one
/// pattern with two makers, one observation, and a line that already says who made it.
/// </summary>
public sealed class ManufacturerInferenceTests
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.Ordinal);

    private static ManufacturerPartPattern Row(string pattern, string maker, int observations = 1, long id = 0) => new()
    {
        Id = id,
        BusinessUnitId = 7,
        Pattern = pattern,
        Manufacturer = maker,
        NormalizedManufacturer = ProductIdentityNormalizerFor(maker),
        ObservationCount = observations
    };

    private static string ProductIdentityNormalizerFor(string maker)
        => ERP_RFQ_Automation.ProductIntelligence.ProductIdentityNormalizer.NormalizeManufacturer(maker)!;

    // ---- candidates -------------------------------------------------------

    [Theory]
    [InlineData("X7-M5", "X7M5|X7")]
    [InlineData("3RT2015-1BB41", "3RT20151BB41|3RT2015|3RT")]
    [InlineData("LC1D09BD", "LC1D09BD|LC1D")]
    [InlineData("1SDA054523R1", "1SDA054523R1|1SDA")]
    [InlineData("12345", "")]
    [InlineData("a2a 50006470", "A2A50006470|A2A")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Candidates_run_longest_first_without_separators_and_never_purely_numeric(string? part, string expected)
    {
        var candidates = ManufacturerInference.PatternCandidates(part);

        Assert.Equal(expected, string.Join("|", candidates));
        Assert.All(candidates, c => Assert.DoesNotContain(c, x => x is '-' or '/' or '.' or ' ' or '_'));
        Assert.All(candidates, c => Assert.False(c.All(char.IsDigit)));
    }

    [Fact]
    public void A_stated_pair_teaches_the_same_candidates_the_lookup_will_use()
    {
        Assert.Equal(ManufacturerInference.PatternCandidates("LC1D09BD"),
            ManufacturerInference.ExtractStatedPairs("Schneider", "LC1D09BD"));
        Assert.Empty(ManufacturerInference.ExtractStatedPairs("", "LC1D09BD"));
        Assert.Empty(ManufacturerInference.ExtractStatedPairs("Schneider", null));
    }

    // ---- inline name ------------------------------------------------------

    [Fact]
    public void A_known_maker_written_in_the_description_is_read_from_there()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "Siemens", "ABB" };

        var result = ManufacturerInference.Infer(null, "Siemens 3RT2015 contactor 7.5kW", null, [], known);

        Assert.NotNull(result);
        Assert.Equal("Siemens", result.Manufacturer);
        Assert.Equal("inferred_from_description:Siemens", result.Reason);
        Assert.Equal(0.85m, result.Confidence);
    }

    [Fact]
    public void Accents_and_case_are_folded_on_both_sides_and_only_whole_words_match()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "Schneider Électric", "Siemens" };

        var accented = ManufacturerInference.Infer(null, "Contactor SCHNEIDER-ELECTRIC LC1D09BD", null, [], known);
        Assert.Equal("Schneider Électric", accented?.Manufacturer);

        // "Siemensstrasse" is a street, not a maker.
        Assert.Null(ManufacturerInference.Infer(null, "Deliver to Siemensstrasse 12", null, [], known));
    }

    [Fact]
    public void Two_different_known_makers_in_one_description_is_ambiguous()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "Siemens", "ABB" };

        Assert.Null(ManufacturerInference.Infer(null, "Siemens 3RT2015 or ABB equivalent", null, [], known));
    }

    [Fact]
    public void A_short_and_a_long_spelling_of_one_maker_are_not_ambiguous_and_the_long_one_wins()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "Schneider", "Schneider Electric" };

        var result = ManufacturerInference.Infer(null, "Schneider Electric LC1D09BD", null, [], known);

        Assert.Equal("Schneider Electric", result?.Manufacturer);
    }

    // ---- pattern ----------------------------------------------------------

    [Fact]
    public void A_pattern_the_tenant_has_seen_twice_for_one_maker_names_it()
    {
        var patterns = new[] { Row("X7M5", "BMW", 1, 1), Row("X7", "BMW", 3, 2) };

        var result = ManufacturerInference.Infer(null, "Bracket", "X7-M5", patterns, NoNames);

        // X7M5 has only one observation, so the longer candidate is insufficient and the
        // family prefix answers.
        Assert.NotNull(result);
        Assert.Equal("BMW", result.Manufacturer);
        Assert.Equal("inferred_from_part_number_pattern:X7", result.Reason);
        Assert.Equal(0.80m, result.Confidence);
    }

    [Fact]
    public void Observations_are_summed_across_rows_that_agree()
    {
        var patterns = new[] { Row("X7M5", "BMW", 1, 1), Row("X7M5", "bmw", 1, 2) };

        var result = ManufacturerInference.Infer(null, null, "X7-M5", patterns, NoNames);

        Assert.Equal("inferred_from_part_number_pattern:X7M5", result?.Reason);
    }

    [Fact]
    public void One_observation_is_an_anecdote_not_an_answer()
    {
        var patterns = new[] { Row("X7M5", "BMW", 1) };

        Assert.Null(ManufacturerInference.Infer(null, null, "X7-M5", patterns, NoNames));
    }

    [Fact]
    public void A_pattern_two_makers_share_is_ambiguous()
    {
        var patterns = new[] { Row("3RT", "Siemens", 5), Row("3RT", "Schneider", 5) };

        Assert.Null(ManufacturerInference.Infer(null, null, "3RT2015-1BB41", patterns, NoNames));
    }

    [Fact]
    public void An_ambiguous_longer_candidate_is_not_rescued_by_a_confident_shorter_one()
    {
        // The full number is disputed; the family prefix is unanimous. The dispute is the
        // more specific evidence and it wins — falling through would answer with exactly the
        // maker one of the disputing rows says is wrong.
        var patterns = new[]
        {
            Row("3RT20151BB41", "Siemens", 2, 1), Row("3RT20151BB41", "Schneider", 2, 2),
            Row("3RT", "Siemens", 10, 3)
        };

        Assert.Null(ManufacturerInference.Infer(null, null, "3RT2015-1BB41", patterns, NoNames));
    }

    [Fact]
    public void The_description_outranks_the_pattern()
    {
        var patterns = new[] { Row("X7M5", "BMW", 5) };
        var known = new HashSet<string>(StringComparer.Ordinal) { "BMW", "Audi" };

        var result = ManufacturerInference.Infer(null, "Audi bracket", "X7-M5", patterns, known);

        Assert.Equal("Audi", result?.Manufacturer);
        Assert.StartsWith("inferred_from_description:", result?.Reason);
    }

    // ---- never overrides --------------------------------------------------

    [Fact]
    public void A_line_that_states_its_maker_is_never_touched()
    {
        var patterns = new[] { Row("X7M5", "BMW", 5) };
        var known = new HashSet<string>(StringComparer.Ordinal) { "BMW" };

        Assert.Null(ManufacturerInference.Infer("Toyota", "BMW bracket", "X7-M5", patterns, known));
        Assert.Null(ManufacturerInference.Infer("BMW", "BMW bracket", "X7-M5", patterns, known));
    }

    [Fact]
    public void Nothing_is_inferred_from_nothing()
    {
        Assert.Null(ManufacturerInference.Infer(null, null, null, [], NoNames));
        Assert.Null(ManufacturerInference.Infer(null, "Widget", "12345", [Row("12", "Nobody", 9)], NoNames));
    }
}
