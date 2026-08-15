using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Retention;

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
    public void Raw_inbound_mail_has_its_OWN_zone_and_is_never_pre_cleared()
    {
        // "cleared" would assert something false about un-inspected bytes and put them where
        // other modules read trusted content.
        Assert.NotEqual("cleared", EmailInquiryCaptureService.RawEmailZone);

        // And NOT "quarantine" either, which is the subtler bug: the retention purge deletes
        // the sibling key derived by swapping quarantine <-> cleared, so a raw message sharing
        // that namespace with an .eml SourceDocument would be destroyed when the document was
        // purged. See RawEmailZone's own documentation.
        Assert.NotEqual("quarantine", EmailInquiryCaptureService.RawEmailZone);
    }

    [Fact]
    public void The_raw_mail_zone_is_immune_to_the_retention_purges_zone_swap()
    {
        // THE assertion behind the zone choice, run against the real derivation rather than
        // asserted in prose: a raw-mail key has no sibling, so purging anything else can never
        // take the authoritative copy of a customer's message with it.
        var key = LocalEvidenceObjectStorage.BuildKey(
            1, EmailInquiryCaptureService.RawEmailZone, new string('a', 64), ".eml");

        Assert.Single(EvidenceRetentionEligibility.ZoneKeysFor(key.Replace('\\', '/')));

        // Proof the derivation genuinely does pair the other two, so the assertion above is
        // about this zone and not about ZoneKeysFor being inert.
        var quarantined = LocalEvidenceObjectStorage.BuildKey(1, "quarantine", new string('a', 64), ".eml");
        Assert.Equal(2, EvidenceRetentionEligibility.ZoneKeysFor(quarantined.Replace('\\', '/')).Count);
    }
}
