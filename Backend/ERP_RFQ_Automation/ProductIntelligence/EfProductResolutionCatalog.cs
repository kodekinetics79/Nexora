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

public sealed class EfApprovedProductReferenceSource(ErpRfqAutomationContext db)
    : IApprovedProductReferenceSource
{
    public async Task<IReadOnlyList<ApprovedProductReference>> GetApprovedReferencesAsync(
        long businessUnitId, CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        var aliases = await db.ProductAliases.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive)
            .Select(x => new ApprovedProductReference(x.BusinessUnitId, ProductReferenceKind.Alias,
                x.NormalizedValue, x.ProductId, null, $"product-alias:{x.Id}", new DateTimeOffset(x.CreatedOn, TimeSpan.Zero)))
            .ToListAsync(cancellationToken);
        var supersessions = await db.ProductSupersessions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.EffectiveOn <= DateOnly.FromDateTime(DateTime.UtcNow)
                && (!x.EndsOn.HasValue || x.EndsOn >= DateOnly.FromDateTime(DateTime.UtcNow)))
            .Join(db.Products.AsNoTracking(), x => x.SupersededProductId, x => x.Id,
                (link, product) => new { link, product })
            .Select(x => new ApprovedProductReference(x.link.BusinessUnitId, ProductReferenceKind.Supersession,
                x.product.PartNo, x.link.ReplacementProductId, null,
                x.link.EvidenceReference ?? $"product-supersession:{x.link.Id}",
                new DateTimeOffset(x.link.CreatedOn, TimeSpan.Zero)))
            .ToListAsync(cancellationToken);
        return aliases.Concat(supersessions).ToArray();
    }
}
