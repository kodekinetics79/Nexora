using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

/// <summary>
/// Reading a sheet whose headings no alias recognises, from the cells alone.
///
/// <para>This is the dangerous half of the profiling — it assigns rather than refuses — so most
/// of what follows tests that it DECLINES: on ambiguity, on line numbers, on anything short of a
/// quantity plus a descriptor. Declining is free, because the document simply keeps the behaviour
/// it has today.</para>
/// </summary>
public class SpreadsheetFieldInferenceTests
{
    private static string Sheet(params string[] rows) => "[Worksheet: Sheet1]\n" + string.Join("\n", rows) + "\n";

    private static SpreadsheetFieldInference.Inference Infer(string sheet)
        => SpreadsheetFieldInference.Infer(SpreadsheetReading.Of(sheet));

    [Fact]
    public void UnknownHeaders_ButOrdinaryContent_AreMappedFromTheCells()
    {
        // No alias in the product matches any of these headings.
        var inference = Infer(Sheet(
            "Pos\tArtikelnummer\tBezeichnung\tMenge\tEinheit",
            "10\t902017274\tKEY:SHAFT,SQUARE,10 MM LG\t176\tEA",
            "20\t902017275\tVALVE,GATE,2 IN CLASS 150\t4\tEA",
            "30\t902017276\tGASKET,SPIRAL WOUND,4 IN\t12\tEA",
            "40\t902017277\tBOLT,STUD,M20 X 120\t64\tEA",
            "50\t902017278\tNUT,HEX,M20 GALVANISED\t128\tEA"));

        Assert.True(inference.IsUsable);
        Assert.Equal(4, inference.FieldColumns[RfqSpreadsheetFields.Quantity]);
        Assert.Equal(3, inference.FieldColumns[RfqSpreadsheetFields.ProductName]);
        Assert.Equal(5, inference.FieldColumns[RfqSpreadsheetFields.UnitOfMeasure]);
    }

    [Fact]
    public void ALineNumberColumn_IsNeverReadAsTheQuantity()
    {
        // THE EXPENSIVE MISTAKE THIS GUARD EXISTS FOR. Column 1 is numeric, positive and of
        // modest magnitude — indistinguishable from a quantity on every other measure. Reading it
        // as one would put the row's POSITION into the customer's quote as the amount ordered, on
        // every line, and the quote would look perfectly correct.
        var inference = Infer(Sheet(
            "Pos\tBezeichnung\tMenge",
            "1\tKEY:SHAFT,SQUARE,10 MM LG\t176",
            "2\tVALVE,GATE,2 IN CLASS 150\t4",
            "3\tGASKET,SPIRAL WOUND,4 IN\t12",
            "4\tBOLT,STUD,M20 X 120\t64",
            "5\tNUT,HEX,M20 GALVANISED\t128"));

        Assert.True(inference.IsUsable);
        Assert.Equal(3, inference.FieldColumns[RfqSpreadsheetFields.Quantity]);
        Assert.NotEqual(1, inference.FieldColumns[RfqSpreadsheetFields.Quantity]);
    }

    [Fact]
    public void TwoPlausibleQuantityColumns_AreRefusedRatherThanChosenBetween()
    {
        // Ordered versus packed. Guessing here is how a quote goes out at the wrong number, and
        // the cost of declining is only that the document keeps the path it takes today.
        var inference = Infer(Sheet(
            "Ref\tBezeichnung\tMenge\tPackung",
            "902017274\tKEY:SHAFT,SQUARE\t176\t12",
            "902017275\tVALVE,GATE,2 IN\t4\t6",
            "902017276\tGASKET,SPIRAL WOUND\t12\t24",
            "902017277\tBOLT,STUD,M20\t64\t50",
            "902017278\tNUT,HEX,M20\t128\t100"));

        Assert.False(inference.IsUsable);
        Assert.Contains("refusing to choose", inference.Explanation);
    }

    [Fact]
    public void AQuantityWithNothingNamingTheItem_IsNotUsable()
    {
        var inference = Infer(Sheet(
            "A\tB",
            "10\t176",
            "20\t4",
            "30\t12",
            "40\t64",
            "50\t128"));

        Assert.False(inference.IsUsable);
    }

    [Fact]
    public void ACrossReferenceSheet_YieldsNoQuantityAndIsNotUsable()
    {
        var rows = new List<string> { "ASMO Item#\tARAMCO Item #\tMSG Descreption\tMSG #" };
        for (var i = 0; i < 12; i++)
            rows.Add($"200000{8634 + i}\t100072{1728 + i}\tSpares, UPS (061100)\t61100");

        var inference = Infer(Sheet(rows.ToArray()));

        Assert.False(inference.IsUsable);
        Assert.Contains("shape of an order quantity", inference.Explanation);
    }

    [Fact]
    public void APriceColumn_IsNotMistakenForTheQuantity()
    {
        var inference = Infer(Sheet(
            "Ref\tBezeichnung\tMenge\tPreis",
            "902017274\tKEY:SHAFT,SQUARE\t176\t12.50",
            "902017275\tVALVE,GATE,2 IN\t4\t340.00",
            "902017276\tGASKET,SPIRAL WOUND\t12\t8.75",
            "902017277\tBOLT,STUD,M20\t64\t2.20",
            "902017278\tNUT,HEX,M20\t128\t0.95"));

        Assert.True(inference.IsUsable);
        Assert.Equal(3, inference.FieldColumns[RfqSpreadsheetFields.Quantity]);
        Assert.Equal(4, inference.FieldColumns[RfqSpreadsheetFields.UnitPrice]);
    }
}
