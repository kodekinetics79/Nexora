using ERP_RFQ_Automation.Authorization;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Explicitly authenticated commercial actor for controller unit tests whose subject is not
/// row-scope resolution. Authorization-specific behavior is covered by CommercialAccessScopeTests.
/// </summary>
public sealed class PermitCommercialAccessContext(long businessUnitId, long userId = 1)
    : ICommercialAccessContext
{
    private readonly CommercialActorScope _actor = new(
        businessUnitId,
        userId,
        RoleId: 1,
        AccountTeamScope.TenantWide(userId));

    public Task<CommercialActorScope?> ResolveAsync(CancellationToken ct = default) =>
        Task.FromResult<CommercialActorScope?>(_actor);

    public Task<bool> CanAccessLeadAsync(long leadId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> CanAccessCustomerAsync(long customerId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> CanAccessRfqAsync(long rfqId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> CanAccessQuoteAsync(long quoteId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> CanAccessOrderAsync(long orderId, CancellationToken ct = default) =>
        Task.FromResult(true);
}
