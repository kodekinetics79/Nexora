using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The zone the capture service writes raw messages to must be one the storage providers
/// actually accept.
///
/// <para><b>The bug this pins.</b> Capture wrote to a zone named <c>"inbound-email"</c>.
/// Both providers validate the zone against a two-value whitelist and throw
/// <see cref="ArgumentException"/> on anything else — and that throw is NOT the
/// storage-unavailable contract capture catches, so it escaped and every single message
/// failed at the first durable write. The path was dead on arrival on every provider, and
/// no test saw it because every capture test substituted the storage with a double that
/// accepted any string.</para>
///
/// <para>These assertions run against the REAL validator, which is the only thing that can
/// answer the question.</para>
/// </summary>
public sealed class EmailInquiryRawEvidenceZoneTests
{
    [Fact]
    public void The_raw_email_zone_is_accepted_by_the_real_storage_validator()
    {
        var sha256 = new string('a', 64);

        // No throw is the whole assertion. Any zone outside the whitelist fails here, which
        // is precisely what happened in production.
        LocalEvidenceObjectStorage.ValidateIdentity(1, EmailInquiryCaptureService.RawEmailZone, sha256);
    }

    [Fact]
    public void A_zone_outside_the_whitelist_is_still_refused()
    {
        // Proves the test above is not vacuous: the validator does reject, so the passing
        // case means the zone is genuinely on the whitelist.
        var sha256 = new string('a', 64);

        Assert.Throws<ArgumentException>(
            () => LocalEvidenceObjectStorage.ValidateIdentity(1, "inbound-email", sha256));
    }

    [Fact]
    public void Raw_inbound_mail_is_quarantined_not_pre_cleared()
    {
        // A message straight off a mailbox has not been through security inspection. Writing
        // it to "cleared" would legalise the call while asserting something false about the
        // bytes, and would put un-inspected content in the zone other modules read as trusted.
        Assert.Equal("quarantine", EmailInquiryCaptureService.RawEmailZone);
    }
}
