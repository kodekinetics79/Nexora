using ERP_RFQ_Automation.Billing.Metering;

namespace ERP_RFQ_Automation.Tests;

public sealed class ExtractionUsageSignalTests
{
    [Fact]
    public void Server_usage_identity_is_stable_and_tenant_qualified()
    {
        var first = UsageEventIdentity.FromIdempotencyKey(41, "extraction-job:91:pages");
        Assert.Equal(first, UsageEventIdentity.FromIdempotencyKey(41, " extraction-job:91:pages "));
        Assert.NotEqual(first, UsageEventIdentity.FromIdempotencyKey(42, "extraction-job:91:pages"));
        Assert.NotEqual(first, UsageEventIdentity.FromIdempotencyKey(41, "extraction-job:91:ocr-pages"));
    }

    [Fact]
    public void Production_cannot_reenable_the_legacy_unmetered_extraction_path()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ERP_RFQ_Automation.Extraction.UnifiedDocumentIngestionGuard.Enforce(true, false));
        ERP_RFQ_Automation.Extraction.UnifiedDocumentIngestionGuard.Enforce(true, true);
        ERP_RFQ_Automation.Extraction.UnifiedDocumentIngestionGuard.Enforce(false, false);
    }
}
