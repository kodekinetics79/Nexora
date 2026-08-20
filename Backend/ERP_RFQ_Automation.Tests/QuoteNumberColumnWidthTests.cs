using ERP_RFQ_Automation.Repositories;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>Rfq.Rfqno</c> is varchar(200); <c>Quote.QuoteNo</c> is varchar(50). The approve-RFQ action
/// built the quote number as <c>QT-{Rfqno}</c> with nothing between the two, so up to 203
/// characters were written into a 50-character column.
///
/// <para>That is reachable on the ordinary journey, not by contrivance. The conversion paths copy
/// the BUYER'S OWN reference into <c>Rfqno</c> verbatim — industrial purchase references routinely
/// run past 47 characters — and the manual-upload door builds
/// <c>RFQ_{filename}_{yyyyMMddHHmm}</c>, which a 35-character filename alone overflows. The failure
/// is a Postgres "value too long" inside the request that creates the quote AND emails it, so the
/// whole approval dies on a string length with nothing on screen naming the cause.</para>
///
/// <para>These tests fix the BEHAVIOUR of the guard, not the numbering scheme: a number that fits
/// is returned untouched, and a number that does not keeps its tail, which is where every
/// discriminator in every generator's format lives.</para>
/// </summary>
public sealed class QuoteNumberColumnWidthTests
{
    private const int QuoteNoMaxLength = 50;

    [Fact]
    public void AReferenceThatFits_IsUsedByteForByte()
    {
        Assert.Equal("QT-NXR-RFQ-4-2026-00000042",
            RfqRepository.QuoteNumberFromRfq("NXR-RFQ-4-2026-00000042"));
    }

    /// <summary>
    /// The buyer's own reference, as the conversion paths copy it. 62 characters of customer
    /// purchase reference plus the "QT-" prefix is 65 — fifteen over the column.
    /// </summary>
    [Fact]
    public void ABuyersLongReference_FitsTheColumnInsteadOfFailingTheApproval()
    {
        const string buyerReference = "SAUDI-ARAMCO-PROCUREMENT-RFQ-2026-NORTHERN-AREA-4471-REV-C-002";
        Assert.True(buyerReference.Length > QuoteNoMaxLength, "fixture must exceed the column");

        var quoteNo = RfqRepository.QuoteNumberFromRfq(buyerReference);

        Assert.Equal(QuoteNoMaxLength, quoteNo.Length);
        Assert.StartsWith("QT-", quoteNo);
    }

    /// <summary>
    /// The tail is what is kept. Two long references that differ only at the END — which is exactly
    /// what the collision suffix the conversion paths append, and the upload timestamp, and the
    /// sequence in NXR-RFQ-{buid}-{yyyy}-{seq} all look like — must not collapse onto one number.
    /// Truncating from the front would map both onto the same quote number, and the unique index on
    /// (BusinessUnitID, QuoteNo) would turn that into a 500 on the second approval.
    /// </summary>
    [Fact]
    public void TwoReferencesDifferingOnlyAtTheEnd_StayApart()
    {
        const string prefix = "RFQ_NORTHERN-AREA-PIPELINE-INSTRUMENTATION-PACKAGE_2026";
        var first = RfqRepository.QuoteNumberFromRfq($"{prefix}0114");
        var second = RfqRepository.QuoteNumberFromRfq($"{prefix}0115");

        Assert.NotEqual(first, second);
        Assert.EndsWith("0114", first);
        Assert.EndsWith("0115", second);
    }

    /// <summary>
    /// A boundary the column cares about and nothing else does: 47 characters is the longest
    /// reference that survives the prefix untouched.
    /// </summary>
    [Fact]
    public void TheColumnBoundaryIsExact()
    {
        var justFits = new string('A', QuoteNoMaxLength - 3);
        Assert.Equal($"QT-{justFits}", RfqRepository.QuoteNumberFromRfq(justFits));
        Assert.Equal(QuoteNoMaxLength, RfqRepository.QuoteNumberFromRfq(justFits).Length);
        Assert.Equal(QuoteNoMaxLength, RfqRepository.QuoteNumberFromRfq(justFits + "B").Length);
    }
}
