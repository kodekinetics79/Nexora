using ERP_RFQ_Automation.Ingestion.Assembly;

namespace ERP_RFQ_Automation.Tests;

public sealed class EmailInquiryAssemblyRecoveryWorkerTests
{
    [Fact]
    public void A_skipped_rolling_deploy_lease_retries_promptly()
    {
        Assert.Equal(TimeSpan.FromSeconds(30),
            EmailInquiryAssemblyRecoveryWorker.DelayAfterSweep(
                EmailInquiryRecoverySweepResult.Skipped, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_completed_sweep_keeps_the_configured_cadence()
    {
        var completed = new EmailInquiryRecoverySweepResult(
            0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.FromMilliseconds(1));

        Assert.Equal(TimeSpan.FromMinutes(7),
            EmailInquiryAssemblyRecoveryWorker.DelayAfterSweep(
                completed, TimeSpan.FromMinutes(7)));
    }
}
