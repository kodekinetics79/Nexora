using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>SEC's "Material PO text" separated into specification, maker and standing instruction — from real prints.</summary>
public sealed class SapMaterialPoTextTests
{
    [Fact]
    public void The_specification_the_maker_and_the_instruction_come_apart()
    {
        var text = ";Material PO text:\nGUARD,BULK NOUN:\nPARENT EQUIPMENT/FUNCTION:\nCOUPLING,FUEL PUMP;\nSIZE:\n272 MM WD X 215 MM LG X 240 MM HT;\nMATERIAL:\nSTL;\nADDITIONAL DATA:\nFOR MLI 0510 - FUEL PUMP ARR.\nGENERAL ELECTRIC (GE) (LM/FM):\nP/N#221B4034G005\n* FOR THIS ITEM YOU ARE REQUIRED TO\nAFFIX SEC SPECIFIED BARCODE/LABEL WITH\nGS1 DATA MATRIX AND HUMAN READABLE\nINFORMATION. SEC WILL NOT ACCEPT\nUNLABELED GOODS.";

        var reading = SapMaterialPoText.Read(text);

        var maker = Assert.Single(reading.Makers);
        Assert.Equal("GENERAL ELECTRIC", maker.Name);
        Assert.Equal("221B4034G005", maker.PartNumber);
        Assert.StartsWith("GUARD,BULK NOUN:", reading.Specification);
        Assert.Contains("272 MM WD X 215 MM LG X 240 MM HT", reading.Specification);
        Assert.DoesNotContain("BARCODE", reading.Specification);
        Assert.DoesNotContain("GENERAL ELECTRIC", reading.Specification);
        Assert.StartsWith("* FOR THIS ITEM", reading.Instructions);
        Assert.Contains("UNLABELED GOODS", reading.Instructions);
    }

    [Fact]
    public void Two_makers_are_both_kept_in_the_order_the_buyer_wrote_them()
    {
        var text = ";Material PO text:\nSWITCH,FLOW:\nRATING:\n300 PSI MAX;\nMATERIAL:\nGENERAL ELECTRIC (GE) (LM/FM):\nP/N#M-100X-S232-120-001\nMALEMA:\nP/N#24864-001\n* FOR THIS ITEM YOU ARE REQUIRED TO AFFIX";

        var reading = SapMaterialPoText.Read(text);

        Assert.Equal(["GENERAL ELECTRIC", "MALEMA"], reading.Makers.Select(m => m.Name));
        Assert.Equal(["M-100X-S232-120-001", "24864-001"], reading.Makers.Select(m => m.PartNumber));
    }

    [Fact]
    public void A_placeholder_maker_is_not_a_maker_and_a_model_number_is_kept_as_a_model()
    {
        var text = "RELAY , PNEUMATIC\nFUNCTION:LEVEL CONTROLLER\nAdditional Data :\nMASONEILAN INTERNATIONAL INC. :\nP/N# 060177001999\nREFERENCE ONLY/TEMPORARY/UNKNOWN :\nP/N# 4006 POS. 28\nVEGA:\nModel#VEGADIS81(AXIKIMACX)\nVEGA:\nP/N#DIS81AXIKIMACX\nExternal Memo Text :\n* FOR THIS ITEM";

        var reading = SapMaterialPoText.Read(text);

        Assert.Equal(["MASONEILAN INTERNATIONAL INC.", "VEGA"], reading.Makers.Select(m => m.Name));
        Assert.Equal("060177001999", reading.Makers[0].PartNumber);
        Assert.Equal("VEGADIS81(AXIKIMACX)", reading.Makers[1].Model);
        Assert.Equal("DIS81AXIKIMACX", reading.Makers[1].PartNumber);
        Assert.StartsWith("External Memo Text", reading.Instructions);
    }

    [Fact]
    public void A_maker_closing_a_prose_line_is_still_read_and_the_prose_stays_in_the_specification()
    {
        var text = "SKID , NO MODIFIER\nSIZE:22 IN L X 20 IN W X 99 IN H\nAdditional Data :\nETD ASSEMBLY (OPERATING HYDRAULIC PRESSURE : 1600~3000PSI) DRAWING\nNO : 141A5547 GENERAL ELECTRIC (GE) (LM/FM) :\nP/N# 112-2093";

        var reading = SapMaterialPoText.Read(text);

        var maker = Assert.Single(reading.Makers);
        Assert.Equal("GENERAL ELECTRIC", maker.Name);
        Assert.Equal("112-2093", maker.PartNumber);
        Assert.Contains("NO : 141A5547", reading.Specification);
        Assert.Null(reading.Instructions);
    }

    [Fact]
    public void A_bare_number_straight_under_the_makers_name_is_their_part_number()
    {
        var text = ";Material PO text:\nBATTERY,STORAGE:\nVOLTAGE:\nMAX VOLT 1.5 V;\nADDITIONAL DATA:\nUSED IN QCPP EXT-3 BLK 1,2 STATION DC B\nATTERY\nALCAD BATTERIES:\nLBE830P-1\n* FOR THIS ITEM YOU ARE REQUIRED TO";

        var reading = SapMaterialPoText.Read(text);

        var maker = Assert.Single(reading.Makers);
        Assert.Equal("ALCAD BATTERIES", maker.Name);
        Assert.Equal("LBE830P-1", maker.PartNumber);
        Assert.Contains("STATION DC B", reading.Specification);
    }

    [Fact]
    public void An_attribute_label_is_never_mistaken_for_a_maker()
    {
        var text = "BEARING , PLAIN\nDESIGN:BUSHING SLEEVE\nMATERIAL:\nSTL;\nSTANDARD/SPECIFICATION:\nASTM";

        var reading = SapMaterialPoText.Read(text);

        Assert.Empty(reading.Makers);
        Assert.Contains("STANDARD/SPECIFICATION:", reading.Specification);
    }
}
