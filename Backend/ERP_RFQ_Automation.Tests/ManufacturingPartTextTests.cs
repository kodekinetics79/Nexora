using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// An SAP sourcing export packs the buyer's approved makers and their part numbers into one
/// "Manufacturing Part Text" cell. Read from the real shape, not one invented to suit the reader.
/// </summary>
public sealed class ManufacturingPartTextTests
{
    private const string OneMaker =
        "4000020966 - 0060000235 - BENTLY-NEVADA LLC - US 000000005002923306 - 10030469 - BENTLY-NEVADA LLC - US " +
        "ED_PART_NUMBER - AA32307301 CUSTOMER_MATERIAL_CODE - 000000005002923306 PART_NUMBER - AA 323073-01 " +
        "CUSTOMER_MATERIAL_DESCRIPTION1 - MODULE ADAPT ESD 4CH. RELAY O";

    private const string TwoMakers =
        "4000062813 - 0060001092 - SCHNEIDER ELECTRIC THE NETHERLANDS - NL 000000005002831975 - 10001675 - SCHNEIDER ELECTRIC THE NETHERLANDS - NL " +
        "ED_PART_NUMBER - LV429827 CUSTOMER_MATERIAL_CODE - 000000005002831975 PART_NUMBER - LV429827 CUSTOMER_MATERIAL_DESCRIPTION2 - 50 A AT 65 " +
        "CUSTOMER_MATERIAL_DESCRIPTION1 - CIRCUIT BREAKER MOLDED CASE, 4000062814 - 0060001761 - ** PEPPERL+FUCHS (AUST) PTY LTD - AU 000000005002831976 - 10001676 - ** PEPPERL+FUCHS (AUST) PTY LTD - AU " +
        "PART_NUMBER - NJ2-12GM40-E2 ED_PART_NUMBER - NJ212GM40E2";

    [Fact]
    public void One_approved_maker_and_its_part_number_are_read()
    {
        var reading = ManufacturingPartText.Read(OneMaker);

        Assert.Equal(new[] { "BENTLY-NEVADA LLC" }, reading.Manufacturers);
        // The buyer's punctuated form wins over its stripped "ED_" twin.
        Assert.Equal(new[] { "AA 323073-01" }, reading.PartNumbers);
        Assert.Equal("000000005002923306", reading.CustomerMaterialCode);
    }

    [Fact]
    public void Several_approved_makers_are_all_read_and_status_flags_are_dropped()
    {
        var reading = ManufacturingPartText.Read(TwoMakers);

        Assert.Equal(new[] { "SCHNEIDER ELECTRIC THE NETHERLANDS", "PEPPERL+FUCHS (AUST) PTY LTD" }, reading.Manufacturers);
        Assert.Equal(new[] { "LV429827", "NJ2-12GM40-E2" }, reading.PartNumbers);
    }

    [Fact]
    public void A_maker_whose_name_contains_a_dash_is_read_whole()
    {
        var reading = ManufacturingPartText.Read(
            "4000033664 - 0060001613 - ** STAHL - ELECTROMACH - NL 000000005002826957 - 10001111 - ** STAHL - ELECTROMACH - NL PART_NUMBER - 8146/5071");

        Assert.Equal(new[] { "STAHL - ELECTROMACH" }, reading.Manufacturers);
        Assert.Equal(new[] { "8146/5071" }, reading.PartNumbers);
    }

    [Fact]
    public void Unlisted_keys_never_leak_into_a_part_number()
    {
        // A drawing number and an item number follow the part number; each is its own key.
        var reading = ManufacturingPartText.Read(
            "4000000001 - 0060000001 - PULSAFEEDER, INCORPORATED - US 000000005000000001 - 10000001 - PULSAFEEDER, INCORPORATED - US " +
            "ED_PART_NUMBER - W206731000 ED_DRAWING_NUMBER - AP00330 PART_NUMBER - W206731-000 ITEM_NUMBER - 535 DRAWING_NUMBER - AP00330 SUPERSEDED_NUMBER - W206730-000");

        Assert.Equal(new[] { "W206731-000" }, reading.PartNumbers);
        Assert.Equal(new[] { "W206730-000" }, reading.SupersededNumbers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Siemens 3RT2015 contactor, 24 VDC coil")]
    [InlineData("Approved: ABB or Schneider - see spec")]
    public void Any_other_buyers_free_text_is_left_alone(string? text)
    {
        Assert.False(ManufacturingPartText.Recognises(text));
        Assert.True(ManufacturingPartText.Read(text).IsEmpty);
    }
}
