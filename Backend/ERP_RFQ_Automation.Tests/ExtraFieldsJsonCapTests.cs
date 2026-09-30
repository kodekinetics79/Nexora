using System.Text.Json;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// XS-06: four motor lines of Aramco RFP 6000000003 (1038, 1058, 1076, 1090) name 54–63 approved
/// makers each. Under the old 2 KB cap the first extra field alone was too long, the serializer
/// returned null, and the line arrived with no makers, no part numbers and no Hazardous / SASO
/// flags — just a description.
/// </summary>
public sealed class ExtraFieldsJsonCapTests
{
    private static string ApprovedMakers(int count) => string.Join("; ", Enumerable.Range(1, count)
        .Select(n => $"MOTOR MAKER NUMBER {n} INDUSTRIES LIMITED (DE): P/N 1LA{n:0000}-4AA60, model 1LA{n:0000}, replaces 1LA{n:0000}-4AA50"));

    [Fact]
    public void A_long_approved_maker_list_is_kept_with_every_flag_beside_it()
    {
        var extra = new Dictionary<string, string>
        {
            ["Approved manufacturers"] = ApprovedMakers(60),
            ["Manufacturer part numbers"] = "1LA0001-4AA60; 1LA0002-4AA60",
            ["Material Type"] = "9CAT",
            ["Hazardous Indicator"] = "Yes",
            ["SASO Indicator"] = "Yes",
        };
        Assert.True(extra["Approved manufacturers"].Length > 5_000);

        var stored = ExtraFieldsJson.Serialize(extra);

        Assert.NotNull(stored);
        var read = ExtraFieldsJson.Deserialize(stored)!;
        Assert.Equal(extra.Keys, read.Keys);
        Assert.Equal("Yes", read["Hazardous Indicator"]);
        Assert.Equal("Yes", read["SASO Indicator"]);
        Assert.Equal(extra["Approved manufacturers"], read["Approved manufacturers"]);
    }

    [Fact]
    public void A_first_entry_longer_than_the_whole_cap_is_shortened_visibly_never_lost()
    {
        var extra = new Dictionary<string, string>
        {
            ["Approved manufacturers"] = ApprovedMakers(400),
            ["Hazardous Indicator"] = "Yes",
        };

        var stored = ExtraFieldsJson.Serialize(extra);

        Assert.NotNull(stored);
        Assert.True(stored!.Length <= ExtraFieldsJson.MaxSerializedChars);
        var read = ExtraFieldsJson.Deserialize(stored)!;
        Assert.EndsWith(ExtraFieldsJson.CutMarker, read["Approved manufacturers"]);
        Assert.StartsWith("MOTOR MAKER NUMBER 1 INDUSTRIES LIMITED", read["Approved manufacturers"]);
        // The short flag is kept: the long value gave way, not the flag.
        Assert.Equal("Yes", read["Hazardous Indicator"]);
    }

    [Fact]
    public void Escaped_characters_still_fit_the_cap()
    {
        // The serializer escapes "+", "&" and non-ASCII, so JSON can be several times the text.
        var extra = new Dictionary<string, string> { ["Approved manufacturers"] = string.Concat(Enumerable.Repeat("PEPPERL+FUCHS & CO … ", 3_000)) };

        var stored = ExtraFieldsJson.Serialize(extra);

        Assert.NotNull(stored);
        Assert.True(stored!.Length <= ExtraFieldsJson.MaxSerializedChars);
        Assert.NotNull(JsonSerializer.Deserialize<Dictionary<string, string>>(stored));
    }
}
