using ERP_RFQ_Automation.Services.DocumentIntelligence;
using Spire.Doc;

namespace ERP_RFQ_Automation.Tests;

public sealed class WordBinaryTextExtractorTests
{
    [Fact]
    public void GeneratedLegacyDoc_ParsesLocallyWithoutOcr()
    {
        using var document = new Document();
        var paragraph = document.AddSection().AddParagraph();
        paragraph.AppendText("RFQ LEGACY-100 PART ABC-900 QUANTITY 12");
        using var stream = new MemoryStream();
        document.SaveToStream(stream, FileFormat.Doc);

        var text = WordBinaryTextExtractor.Extract(stream.ToArray());

        Assert.Contains("LEGACY-100", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ABC-900", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnreadableOleDoc_ReturnsNoTextForTruthfulUnsupportedOutcome()
    {
        var bytes = Convert.FromHexString("D0CF11E0A1B11AE10000000000000000");

        Assert.Equal(string.Empty, WordBinaryTextExtractor.Extract(bytes));
    }
}
