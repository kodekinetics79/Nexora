using ERP_RFQ_Automation.CustomerResolution;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Client resolution as a no-op, for door tests whose subject is reconciliation rather than who
/// the buyer turns out to be.
///
/// <para>Shared rather than copied per test class for the same reason
/// <c>UploadedLeadResolution</c> is shared in production: three doors each growing their own
/// version is how the resolution gap appeared in the first place. It records what it was asked
/// to resolve, so a test that DOES care can assert the door called it.</para>
///
/// <para>It answers Unresolved, never throws, and links nothing — a door test must not depend on
/// matching rules it is not exercising.</para>
/// </summary>
public sealed class StubLeadCustomerResolution : ILeadCustomerResolutionService
{
    public List<(long BusinessUnitId, long LeadId)> Resolved { get; } = [];

    public Task<ClientResolutionOutcome> ResolveAsync(
        long businessUnitId, long leadId, CancellationToken ct = default)
    {
        Resolved.Add((businessUnitId, leadId));
        return Task.FromResult(ClientResolutionOutcome.Unresolved(
            "stub", "Client resolution is stubbed out for this door test."));
    }

    public Task<CustomerResolutionBackfillResult> BackfillAsync(
        long businessUnitId, int maxLeads = 500, bool includeSuggested = true,
        CancellationToken ct = default)
        => Task.FromResult(new CustomerResolutionBackfillResult(0, 0, 0, 0, 0, 0));
}
