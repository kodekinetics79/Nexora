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

    /// <summary>Line 9 of Aramco RFP 6000000028, verbatim: two approved vendors, both naming the same maker part.</summary>
    private const string TwoVendorsOnePart =
        "4000019667 - 0060000235 - BENTLY-NEVADA LLC - US 000000005002436489 - 10030469 - BENTLY-NEVADA LLC - US REMARKS - WILL SHIP AS PARTS 149986-01 " +
        "ED_MODEL_NUMBER - 350033 CUSTOMER_MATERIAL_DESCRIPTION2 - ELAY OUTPUT SUPERSEDED_NUMBER - 3500/33-02-01 ED_REMARKS_C1 - AND14999202 " +
        "CUSTOMER_MATERIAL_CODE - 000000005002436489 CUSTOMER_MATERIAL_DESCRIPTION1 - MODULE, 16 CHANNEL FAILSAFE R ED_SUPERSEDED_NUMBER - 3500330201 " +
        "REMARKS_C1 - AND 149992-02 PART_NUMBER - 3500/33-02-02 ED_REMARKS - WILLSHIPASPARTS14998601 MODEL_NUMBER - 3500/33 ED_PART_NUMBER - 3500330202 " +
        "4000019666 - 0060003524 - ** GE OIL AND GAS THE NETHERLANDS B - NL 000000005002436488 - 10000181 - ** GE OIL AND GAS THE NETHERLANDS B - NL " +
        "ED_MODEL_NUMBER - 350033 CUSTOMER_MATERIAL_DESCRIPTION2 - ELAY OUTPUT CUSTOMER_MATERIAL_DESCRIPTION1 - MODULE, 16 CHANNEL FAILSAFE R " +
        "ED_SUPERSEDED_NUMBER - 3500330201 ED_PART_NUMBER - 3500330202 CUSTOMER_MATERIAL_CODE - 000000005002436488 SUPERSEDED_NUMBER - 3500/33-02-01 " +
        "MODEL_NUMBER - 3500/33 PART_NUMBER - 3500/33-02-02";

    [Fact]
    public void Each_approved_vendor_keeps_its_own_numbers_and_remarks()
    {
        var reading = ManufacturingPartText.Read(TwoVendorsOnePart);

        Assert.Equal(2, reading.Vendors.Count);
        var bently = reading.Vendors[0];
        Assert.Equal("BENTLY-NEVADA LLC", bently.Maker);
        Assert.Equal("US", bently.Country);
        Assert.Equal("3500/33-02-02", bently.PartNumber);
        Assert.Equal("3500/33", bently.ModelNumber);
        Assert.Equal(["3500/33-02-01"], bently.SupersededNumbers);
        Assert.Equal("WILL SHIP AS PARTS 149986-01 AND 149992-02", bently.Remarks);   // _C1 continues the remark
        var ge = reading.Vendors[1];
        Assert.Equal("GE OIL AND GAS THE NETHERLANDS B", ge.Maker);
        Assert.Equal("NL", ge.Country);
        Assert.Equal("3500/33-02-02", ge.PartNumber);
        // Both vendors name the same maker part: that number is the part number.
        Assert.Equal("3500/33-02-02", reading.AgreedPartNumber);
    }

    [Fact]
    public void Vendors_naming_different_parts_agree_on_nothing()
    {
        Assert.Null(ManufacturingPartText.Read(TwoMakers).AgreedPartNumber);
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
