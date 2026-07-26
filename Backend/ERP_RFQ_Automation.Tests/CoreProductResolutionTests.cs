using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.ProductIntelligence;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class CoreProductResolutionTests
{
    [Fact]
    public async Task ExactPart_PreservesMeaningfulConfigurationSeparators_AndAutoLinks()
    {
        var resolver = Resolver(
            Product(1, 11, "AB-12/CFG+X", "INTERNAL.001"),
            Product(2, 11, "AB12/CFG+X", "INTERNAL.002"));

        var result = await resolver.ResolveAsync(Request(11, " ab-12 / cfg+x "));

        Assert.Equal("AB-12/CFG+X", result.NormalizedPartNumber);
        Assert.Equal(ProductResolutionDecisionState.AutoLinked, result.DecisionState);
        Assert.Equal(1, result.ResolvedProductId);
        Assert.Equal(ProductResolutionMethods.ExactPartNumber, result.Method);
        Assert.False(result.ExternalProviderUsed);
        Assert.Equal(101, result.SourceLeadRevisionId);
        Assert.Equal(1001, result.SourceLeadItemRevisionId);
        Assert.Contains(result.Evidence, evidence => evidence.Reference == "document-1!A2");
    }

    [Fact]
    public async Task ExactInternalCode_IsResolvedAfterPartNumberLookup()
    {
        var resolver = Resolver(Product(4, 11, "SELLER-PART-4", "INT/CFG-4"));

        var result = await resolver.ResolveAsync(Request(11, "int / cfg-4"));

        Assert.Equal(ProductResolutionDecisionState.AutoLinked, result.DecisionState);
        Assert.Equal(4, result.ResolvedProductId);
        Assert.Equal(ProductResolutionMethods.ExactInternalCode, result.Method);
    }

    [Fact]
    public async Task CanonicalCompactPart_ResolvesUniqueSeparatedCatalogIdentity()
    {
        var resolver = Resolver(Product(5, 11, "CORE-ATP-100"));

        var result = await resolver.ResolveAsync(Request(11, "coreatp100"));

        Assert.Equal(ProductResolutionDecisionState.AutoLinked, result.DecisionState);
        Assert.Equal(5, result.ResolvedProductId);
        Assert.Equal(ProductResolutionMethods.CanonicalCompactIdentity, result.Method);
    }

    [Fact]
    public async Task CanonicalCompactPart_DoesNotAutoLinkCollidingCatalogIdentities()
    {
        var resolver = Resolver(Product(5, 11, "CORE-ATP-100"), Product(6, 11, "COREA-TP100"));

        var result = await resolver.ResolveAsync(Request(11, "coreatp100"));

        Assert.True(result.IsAmbiguous);
        Assert.Equal(ProductResolutionDecisionState.ReviewRequired, result.DecisionState);
        Assert.Null(result.ResolvedProductId);
    }

    [Fact]
    public async Task LowConfidenceSimilarity_IsRankedButNeverAutoLinked()
    {
        var resolver = Resolver(Product(1, 11, "PUMP-900", "INT-1", "Acme", "Industrial pump assembly"));

        var result = await resolver.ResolveAsync(Request(11, "unknown", "pump assembly"));

        Assert.Equal(ProductResolutionDecisionState.ReviewRequired, result.DecisionState);
        Assert.Null(result.ResolvedProductId);
        Assert.Equal(ProductResolutionMethods.LocalSimilarity, result.Method);
        Assert.InRange(result.Confidence, 0.35m, 0.89m);
    }

    [Fact]
    public async Task EqualApprovedAliases_AreAmbiguousAndNeverAutoLink()
    {
        var products = new[] { Product(1, 11, "NEW-1"), Product(2, 11, "NEW-2") };
        var references = new[]
        {
            Reference(11, ProductReferenceKind.Alias, "LEGACY-A", 1, "approval-1"),
            Reference(11, ProductReferenceKind.Alias, "LEGACY-A", 2, "approval-2")
        };
        var resolver = Resolver(products, references);

        var result = await resolver.ResolveAsync(Request(11, "LEGACY-A"));

        Assert.True(result.IsAmbiguous);
        Assert.Equal(0m, result.Margin);
        Assert.Equal(ProductResolutionDecisionState.ReviewRequired, result.DecisionState);
        Assert.Null(result.ResolvedProductId);
        Assert.Equal(2, result.RankedCandidates.Count);
    }

    [Fact]
    public async Task UniqueApprovedAlias_AutoLinksWithApprovalEvidence()
    {
        var resolver = Resolver(
            new[] { Product(7, 11, "CATALOG-7") },
            new[] { Reference(11, ProductReferenceKind.Alias, "CUSTOMER-7", 7, "alias-approval-7") });

        var result = await resolver.ResolveAsync(Request(11, "customer-7"));

        Assert.Equal(7, result.ResolvedProductId);
        Assert.Equal(ProductResolutionMethods.ApprovedAlias, result.Method);
        Assert.Contains(result.RankedCandidates.Single().Evidence,
            evidence => evidence.Reference == "alias-approval-7");
    }

    [Fact]
    public async Task ApprovedSupersession_ResolvesToReplacementProduct()
    {
        var resolver = Resolver(
            new[] { Product(8, 11, "REPLACEMENT-8") },
            new[] { Reference(11, ProductReferenceKind.Supersession, "OBSOLETE-8", 8, "change-8") });

        var result = await resolver.ResolveAsync(Request(11, "OBSOLETE-8"));

        Assert.Equal(ProductResolutionDecisionState.AutoLinked, result.DecisionState);
        Assert.Equal(8, result.ResolvedProductId);
        Assert.Equal(ProductResolutionMethods.ApprovedSupersession, result.Method);
    }

    [Fact]
    public async Task EfCatalog_ExplicitlyExcludesOtherTenantAndSharedProducts()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 11);
            Seed.BusinessUnit(seed, 12);
            seed.Products.AddRange(
                DbProduct(1, 11, "TENANT-11"),
                DbProduct(2, 12, "TENANT-12"),
                DbProduct(3, null, "SHARED"));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(11);
        var catalog = new EfProductResolutionCatalog(context);
        var products = await catalog.GetActiveProductsAsync(11);

        var product = Assert.Single(products);
        Assert.Equal(1, product.ProductId);
        Assert.Equal(11, product.BusinessUnitId);
    }

    [Fact]
    public async Task ResolverDefensivelyRejectsCrossTenantCatalogAndReferenceRows()
    {
        var resolver = Resolver(
            new[] { Product(1, 12, "CROSS-TENANT") },
            new[] { Reference(12, ProductReferenceKind.Alias, "ALIAS-X", 1, "other-tenant") });

        var result = await resolver.ResolveAsync(Request(11, "ALIAS-X", "cross tenant"));

        Assert.Equal(ProductResolutionDecisionState.Unresolved, result.DecisionState);
        Assert.Empty(result.RankedCandidates);
    }

    private static ProductResolutionRequest Request(long businessUnitId, string? part, string? description = null) =>
        new(businessUnitId, 101, 1001, part, "Acme", description,
            [new ProductResolutionEvidence("source-cell", "document-1!A2", part)]);

    private static ProductIdentityCandidate Product(
        long id,
        long businessUnitId,
        string part,
        string? internalCode = null,
        string? manufacturer = "Acme",
        string? name = null) =>
        new(businessUnitId, id, part, internalCode, manufacturer, name ?? part, name);

    private static ApprovedProductReference Reference(
        long businessUnitId,
        ProductReferenceKind kind,
        string value,
        long productId,
        string approval) =>
        new(businessUnitId, kind, value, productId, "Acme", approval, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static DeterministicProductItemResolver Resolver(params ProductIdentityCandidate[] products) =>
        Resolver(products, Array.Empty<ApprovedProductReference>());

    private static DeterministicProductItemResolver Resolver(
        IReadOnlyList<ProductIdentityCandidate> products,
        IReadOnlyList<ApprovedProductReference> references) =>
        new(new StaticCatalog(products), new StaticReferences(references));

    private static Product DbProduct(long id, long? businessUnitId, string part) => new()
    {
        Id = id,
        Buid = businessUnitId,
        PartNo = part,
        ProductName = part,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow,
        IsActive = true
    };

    private sealed class StaticCatalog(IReadOnlyList<ProductIdentityCandidate> products) : IProductResolutionCatalog
    {
        public Task<IReadOnlyList<ProductIdentityCandidate>> GetActiveProductsAsync(
            long businessUnitId,
            CancellationToken cancellationToken = default) => Task.FromResult(products);
    }

    private sealed class StaticReferences(IReadOnlyList<ApprovedProductReference> references) : IApprovedProductReferenceSource
    {
        public Task<IReadOnlyList<ApprovedProductReference>> GetApprovedReferencesAsync(
            long businessUnitId,
            CancellationToken cancellationToken = default) => Task.FromResult(references);
    }
}
