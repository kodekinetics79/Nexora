using ERP_RFQ_Automation.CommercialIntelligence.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<CommercialExceptionCase> CommercialExceptionCases => Set<CommercialExceptionCase>();
    public DbSet<CommercialExceptionEvent> CommercialExceptionEvents => Set<CommercialExceptionEvent>();
    public DbSet<CommercialExceptionOutboxMessage> CommercialExceptionOutboxMessages => Set<CommercialExceptionOutboxMessage>();
    public IQueryable<CommercialExceptionOperation> CommercialExceptionOperations
        => Set<CommercialExceptionOperation>()
            .Where(x => ScopedTenantId == null || x.BusinessUnitId == ScopedTenantId);
}
