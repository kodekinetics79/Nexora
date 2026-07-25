using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.ProductIntelligence;

public sealed class EfProductResolutionCatalog : IProductResolutionCatalog
{
    private readonly ErpRfqAutomationContext _db;

    public EfProductResolutionCatalog(ErpRfqAutomationContext db) => _db = db;

    public async Task<IReadOnlyList<ProductIdentityCandidate>> GetActiveProductsAsync(
        long businessUnitId,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        return await _db.Products
            .AsNoTracking()
            .Where(product => product.Buid == businessUnitId && product.IsActive != false)
            .OrderBy(product => product.Id)
            .Select(product => new ProductIdentityCandidate(
                businessUnitId,
                product.Id,
                product.PartNo,
                product.DocId,
                null,
                product.ProductName,
                product.Description))
            .ToListAsync(cancellationToken);
    }
}
