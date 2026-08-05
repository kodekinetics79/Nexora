using ERP_RFQ_Automation.CustomerResolution;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The normaliser decides what "the same company" means, and every exact match in client
/// resolution is a comparison of two of its outputs. These cases are written against the
/// REAL production corpus: 14 Saudi Electricity Company bids received by
/// "ALI ZAID AL-QURAISHI&amp;PARTNERS EL" / "A.Z. ALQURAISHI &amp; PARTNERS ESOSA".
/// </summary>
public sealed class CustomerNameNormalizerTests
{
    [Theory]
    [InlineData("Saudi Electricity Company", "SAUDI ELECTRICITY")]
    [InlineData("SAUDI ELECTRICITY CO.", "SAUDI ELECTRICITY")]
    [InlineData("  saudi   electricity   company  ", "SAUDI ELECTRICITY")]
    public void Case_whitespace_and_generic_suffixes_fold_to_one_key(string input, string expected)
        => Assert.Equal(expected, CustomerNameNormalizer.LooseKey(input));

    [Fact]
    public void Ampersand_spells_out_and_never_leaves_a_dangling_connector()
    {
        // "&" between two surviving tokens is meaningful; once the token beside it is
        // stripped as a legal suffix, a trailing AND is noise.
        Assert.Equal("QURAISHI", CustomerNameNormalizer.LooseKey("Al-Quraishi & Partners Est."));
        Assert.Equal("ALI ZAID AL QURAISHI AND ELECTRIC",
            CustomerNameNormalizer.LooseKey("ALI ZAID AL-QURAISHI&PARTNERS ELECTRIC"));
    }

    [Fact]
    public void A_leading_article_is_dropped_but_an_interior_al_is_part_of_the_name()
    {
        Assert.Equal("QURAISHI", CustomerNameNormalizer.LooseKey("Al Quraishi"));
        Assert.Equal("QURAISHI", CustomerNameNormalizer.LooseKey("El-Quraishi"));
        // "ALI" merely starts with AL; it is a token in its own right.
        Assert.StartsWith("ALI ZAID", CustomerNameNormalizer.LooseKey("ALI ZAID AL-QURAISHI"));
        Assert.Contains("AL QURAISHI", CustomerNameNormalizer.LooseKey("ALI ZAID AL-QURAISHI"));
    }

    [Fact]
    public void Diacritics_tatweel_and_arabic_indic_digits_fold_to_ascii()
    {
        Assert.Equal(
            CustomerNameNormalizer.LooseKey("Societe Generale Munchen"),
            CustomerNameNormalizer.LooseKey("Société Générale München"));
        Assert.Equal("SEC 2004414", CustomerNameNormalizer.LooseKey("SEC ٢٠٠٤٤١٤"));
        Assert.Equal("SEC 2004414", CustomerNameNormalizer.LooseKey("SEC ۲۰۰۴۴۱۴"));
        // U+0640 ARABIC TATWEEL is decorative elongation, never identity.
        Assert.Equal(CustomerNameNormalizer.LooseKey("ABC"), CustomerNameNormalizer.LooseKey("ABـC"));
    }

    [Fact]
    public void A_name_made_entirely_of_generic_tokens_never_normalises_away_to_nothing()
    {
        // A customer genuinely called "Trading Company" must still have a key; stripping it
        // to "" would silently turn every such customer into the same customer.
        Assert.NotEqual(string.Empty, CustomerNameNormalizer.LooseKey("Trading Company"));
        Assert.Equal(string.Empty, CustomerNameNormalizer.LooseKey("   "));
        Assert.Equal(string.Empty, CustomerNameNormalizer.LooseKey(null));
    }

    [Fact]
    public void TightKey_collapses_separators_so_transliteration_spacing_stops_mattering()
    {
        // Interior spacing/hyphenation of the same name folds away.
        Assert.Equal(
            CustomerNameNormalizer.TightKey("ALI ZAID AL-QURAISHI"),
            CustomerNameNormalizer.TightKey("ALI ZAID ALQURAISHI"));
        Assert.Equal("SAUDIELECTRICITY", CustomerNameNormalizer.TightKey("Saudi Electricity Company"));

        // KNOWN LIMIT, stated rather than hidden: a LEADING "Al" is dropped as an article,
        // so "Al Quraishi" and "AlQuraishi" do NOT produce the same tight key — un-gluing a
        // leading AL/EL would also maul "ALI", "ALUMINIUM" and "ELECTRIC". The fuzzy stage
        // still scores them close, and a human confirmation is learned as an alias, which is
        // the platform's answer to transliteration variance everywhere else too.
        Assert.NotEqual(
            CustomerNameNormalizer.TightKey("AL QURAISHI"),
            CustomerNameNormalizer.TightKey("ALQURAISHI"));
    }

    [Fact]
    public void The_key_is_idempotent_so_stored_values_survive_being_renormalised()
    {
        var once = CustomerNameNormalizer.LooseKey("A.Z. ALQURAISHI & PARTNERS ESOSA");
        Assert.Equal(once, CustomerNameNormalizer.LooseKey(once));
    }

    [Fact]
    public void No_letter_substitution_is_applied_so_two_families_never_silently_merge()
    {
        // Q/K transliteration variance is resolved ONCE by a human and then learned as a
        // verified alias — never guessed by the normaliser.
        Assert.NotEqual(
            CustomerNameNormalizer.LooseKey("Al Quraishi"),
            CustomerNameNormalizer.LooseKey("Al Kuraishi"));
    }

    [Theory]
    [InlineData("Saudi Electricity", "Saudi Electricty", true)]     // one dropped letter
    [InlineData("Saudi Electricity", "Aramco Overseas", false)]
    public void JaroWinkler_recognises_near_misses_without_conflating_different_names(
        string left, string right, bool similar)
    {
        var score = CustomerNameNormalizer.JaroWinkler(
            CustomerNameNormalizer.TightKey(left), CustomerNameNormalizer.TightKey(right));
        Assert.Equal(similar, score >= 0.90d);
    }

    [Fact]
    public void JaroWinkler_is_bounded_and_symmetric()
    {
        Assert.Equal(1d, CustomerNameNormalizer.JaroWinkler("ABC", "ABC"));
        Assert.Equal(0d, CustomerNameNormalizer.JaroWinkler("ABC", null));
        Assert.Equal(
            CustomerNameNormalizer.JaroWinkler("SAUDIELECTRICITY", "SAUDIELECTRICTY"),
            CustomerNameNormalizer.JaroWinkler("SAUDIELECTRICTY", "SAUDIELECTRICITY"), 6);
    }
}
