namespace ERP_RFQ_Automation.Models;

/// <summary>
/// What the BUYER calls this line: their own material number and the maker and maker part number
/// they asked for. Copied from the RFQ line when the quote line is made and kept on the quote line,
/// because the quote is a document: SEC and Aramco evaluators match our offer to their request by
/// material number (905750742, 000000002000009400) and confirm the part by maker and part number
/// (SAFT LS14500-AX). The RFQ line can change after the quote goes out — a re-upload of the same
/// RFP once wiped every maker on 1,514 lines — so the printed quote must not depend on it.
///
/// <para>Null on lines typed by hand and on lines written before these columns existed; the
/// read side then falls back to the linked RFQ line (QuoteItemResponseDTO.Requested*).</para>
/// </summary>
public partial class QuoteItem
{
    public const int MaxCustomerMaterialCode = 200;
    public const int MaxManufacturerName = 400;
    public const int MaxManufacturerPartNumber = 200;

    /// <summary>The buyer's own material / item number (SAP material number and alike).</summary>
    public string? CustomerMaterialCode { get; set; }

    /// <summary>The maker the buyer asked for.</summary>
    public string? ManufacturerName { get; set; }

    /// <summary>The maker's part number the buyer asked for.</summary>
    public string? ManufacturerPartNumber { get; set; }

    /// <summary>
    /// Copies the buyer's material number, maker and maker part number from the RFQ line. Every
    /// path that makes a quote line from an RFQ line calls this, so none of them can forget one.
    /// <paramref name="onlyWhenMissing"/> keeps what a line already carries (a legacy line being
    /// re-priced, a revision of a sent quote).
    /// </summary>
    public void CarryBuyerIdentityFrom(Rfqitem? line, bool onlyWhenMissing = false)
    {
        if (line is null) return;
        if (!onlyWhenMissing || string.IsNullOrWhiteSpace(CustomerMaterialCode))
            CustomerMaterialCode = Clean(line.ItemMaterialCode, MaxCustomerMaterialCode);
        if (!onlyWhenMissing || string.IsNullOrWhiteSpace(ManufacturerName))
            ManufacturerName = Clean(line.ManufacturerName, MaxManufacturerName);
        if (!onlyWhenMissing || string.IsNullOrWhiteSpace(ManufacturerPartNumber))
            ManufacturerPartNumber = Clean(line.ManufacturerPartNumber, MaxManufacturerPartNumber);
    }

    /// <summary>Copies the three values from another quote line (a revision copies its source).</summary>
    public void CarryBuyerIdentityFrom(QuoteItem source)
    {
        CustomerMaterialCode = source.CustomerMaterialCode;
        ManufacturerName = source.ManufacturerName;
        ManufacturerPartNumber = source.ManufacturerPartNumber;
    }

    internal static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
