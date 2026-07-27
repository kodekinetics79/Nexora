namespace ERP_RFQ_Automation.ProductIntelligence;

public sealed class DeterministicProductItemResolver : IProductItemResolver
{
    public const string CurrentRuleVersion = "product-resolution/v1";
    private const decimal FuzzyCandidateFloor = 0.25m;
    private const decimal AmbiguityMargin = 0.05m;
    private const int MaximumCandidates = 5;

    private readonly IProductResolutionCatalog _catalog;
    private readonly IApprovedProductReferenceSource _references;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, CatalogSnapshot> _tenantSnapshots = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProductMatchKey, ProductMatch> _matches = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    public DeterministicProductItemResolver(
        IProductResolutionCatalog catalog,
        IApprovedProductReferenceSource references)
    {
        _catalog = catalog;
        _references = references;
    }

    public async Task<ProductResolutionResult> ResolveAsync(
        ProductResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(request.BusinessUnitId));
        if (request.SourceLeadRevisionId <= 0) throw new ArgumentOutOfRangeException(nameof(request.SourceLeadRevisionId));
        if (request.SourceLeadItemRevisionId <= 0) throw new ArgumentOutOfRangeException(nameof(request.SourceLeadItemRevisionId));

        var normalizedPart = ProductIdentityNormalizer.NormalizePartNumber(request.OriginalPartNumber);
        var normalizedManufacturer = ProductIdentityNormalizer.NormalizeManufacturer(request.OriginalManufacturer);
        var snapshot = await GetSnapshotAsync(request.BusinessUnitId, cancellationToken);
        var key = new ProductMatchKey(request.BusinessUnitId, normalizedPart, normalizedManufacturer,
            request.OriginalPartNumber?.Trim(), request.Description?.Trim());
        var match = _matches.GetOrAdd(key, _ => Match(snapshot.Products, snapshot.References, request,
            normalizedPart, normalizedManufacturer));

        return new ProductResolutionResult(
            request.BusinessUnitId,
            request.SourceLeadRevisionId,
            request.SourceLeadItemRevisionId,
            request.OriginalPartNumber,
            normalizedPart,
            request.OriginalManufacturer,
            normalizedManufacturer,
            match.RankedCandidates,
            match.Confidence,
            match.Margin,
            match.Method,
            CurrentRuleVersion,
            request.Evidence ?? Array.Empty<ProductResolutionEvidence>(),
            match.DecisionState,
            match.ResolvedProductId,
            match.IsAmbiguous,
            false);
    }

    private async Task<CatalogSnapshot> GetSnapshotAsync(long businessUnitId, CancellationToken cancellationToken)
    {
        if (_tenantSnapshots.TryGetValue(businessUnitId, out var cached)) return cached;

        await _snapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (_tenantSnapshots.TryGetValue(businessUnitId, out cached)) return cached;
            var products = (await _catalog.GetActiveProductsAsync(businessUnitId, cancellationToken))
                .Where(product => product.BusinessUnitId == businessUnitId)
                .GroupBy(product => product.ProductId)
                .Select(group => group.First())
                .ToArray();
            var references = (await _references.GetApprovedReferencesAsync(businessUnitId, cancellationToken))
                .Where(reference => reference.BusinessUnitId == businessUnitId)
                .ToArray();
            cached = new CatalogSnapshot(products, references);
            _tenantSnapshots.TryAdd(businessUnitId, cached);
            return cached;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private static ProductMatch Match(
        ProductIdentityCandidate[] products,
        ApprovedProductReference[] references,
        ProductResolutionRequest request,
        string? normalizedPart,
        string? normalizedManufacturer)
    {

        var ranked = ExactMatches(products, normalizedPart, normalizedManufacturer).ToList();
        if (ranked.Count == 0)
            ranked.AddRange(ReferenceMatches(products, references, normalizedPart, normalizedManufacturer));
        if (ranked.Count == 0)
            ranked.AddRange(SimilarityMatches(products, request, normalizedManufacturer));

        ranked = ranked
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.ProductId)
            .Take(MaximumCandidates)
            .ToList();

        var confidence = ranked.FirstOrDefault()?.Confidence ?? 0m;
        var margin = ranked.Count switch
        {
            0 => 0m,
            1 => confidence,
            _ => Math.Max(0m, confidence - ranked[1].Confidence)
        };
        var top = ranked.FirstOrDefault();
        var isDeterministicMethod = top?.Method is ProductResolutionMethods.ExactPartNumber
            or ProductResolutionMethods.ExactInternalCode
            or ProductResolutionMethods.CanonicalCompactIdentity
            or ProductResolutionMethods.ApprovedAlias
            or ProductResolutionMethods.ApprovedSupersession;
        var ambiguous = ranked.Count > 1 && margin < AmbiguityMargin;
        var autoLink = top is not null && isDeterministicMethod && !ambiguous;
        var decision = autoLink
            ? ProductResolutionDecisionState.AutoLinked
            : ranked.Count > 0
                ? ProductResolutionDecisionState.ReviewRequired
                : ProductResolutionDecisionState.Unresolved;

        return new ProductMatch(ranked, confidence, margin, top?.Method, decision,
            autoLink ? top!.ProductId : null, ambiguous);
    }

    private static IEnumerable<RankedProductCandidate> ExactMatches(
        IEnumerable<ProductIdentityCandidate> products,
        string? normalizedPart,
        string? normalizedManufacturer)
    {
        if (normalizedPart is null) yield break;

        var catalog = products.Select(product => new
        {
            Product = product,
            Part = ProductIdentityNormalizer.NormalizePartNumber(product.PartNumber),
            InternalCode = ProductIdentityNormalizer.NormalizePartNumber(product.InternalCode),
        }).ToArray();
        var direct = catalog.Where(product => product.Part == normalizedPart
            || product.InternalCode == normalizedPart).ToArray();
        var candidates = direct.Length > 0
            ? direct
            : catalog.Where(product => Compact(product.Part) == Compact(normalizedPart)
                || Compact(product.InternalCode) == Compact(normalizedPart)).ToArray();

        foreach (var candidate in candidates)
        {
            var method = direct.Length == 0
                ? ProductResolutionMethods.CanonicalCompactIdentity
                : candidate.Part == normalizedPart
                || (direct.Length == 0 && Compact(candidate.Part) == Compact(normalizedPart))
                ? ProductResolutionMethods.ExactPartNumber
                : candidate.InternalCode == normalizedPart
                    || (direct.Length == 0 && Compact(candidate.InternalCode) == Compact(normalizedPart))
                    ? ProductResolutionMethods.ExactInternalCode
                    : null;
            if (method is null) continue;

            var confidence = method == ProductResolutionMethods.ExactPartNumber ? 1m
                : method == ProductResolutionMethods.ExactInternalCode ? 0.99m : 0.98m;
            yield return Candidate(candidate.Product, confidence, method,
                method == ProductResolutionMethods.CanonicalCompactIdentity
                    ? "Unique canonical compact catalog identity"
                    : "Exact normalized catalog identity",
                normalizedPart, normalizedManufacturer);
        }
    }

    private static string? Compact(string? value) => value is null
        ? null
        : new string(value.Where(char.IsLetterOrDigit).ToArray());

    private static IEnumerable<RankedProductCandidate> ReferenceMatches(
        IReadOnlyCollection<ProductIdentityCandidate> products,
        IEnumerable<ApprovedProductReference> references,
        string? normalizedPart,
        string? normalizedManufacturer)
    {
        if (normalizedPart is null) yield break;

        foreach (var reference in references.Where(reference =>
                     ProductIdentityNormalizer.NormalizePartNumber(reference.ReferenceValue) == normalizedPart))
        {
            var referenceManufacturer = ProductIdentityNormalizer.NormalizeManufacturer(reference.Manufacturer);
            if (normalizedManufacturer is not null && referenceManufacturer is not null
                && normalizedManufacturer != referenceManufacturer) continue;

            var product = products.SingleOrDefault(candidate => candidate.ProductId == reference.ProductId);
            if (product is null) continue;
            var method = reference.Kind == ProductReferenceKind.Alias
                ? ProductResolutionMethods.ApprovedAlias
                : ProductResolutionMethods.ApprovedSupersession;
            var confidence = reference.Kind == ProductReferenceKind.Alias ? 0.98m : 0.97m;
            var evidence = new ProductResolutionEvidence("approved-reference", reference.ApprovalReference,
                reference.ReferenceValue, $"Approved {reference.Kind} at {reference.ApprovedAtUtc:O}");
            yield return Candidate(product, confidence, method, $"Matched approved {reference.Kind}",
                normalizedPart, normalizedManufacturer, evidence);
        }
    }

    private static IEnumerable<RankedProductCandidate> SimilarityMatches(
        IEnumerable<ProductIdentityCandidate> products,
        ProductResolutionRequest request,
        string? normalizedManufacturer)
    {
        var sourceTokens = ProductIdentityNormalizer.Tokens(request.OriginalPartNumber, request.Description);
        if (sourceTokens.Count == 0) yield break;

        foreach (var product in products)
        {
            var candidateTokens = ProductIdentityNormalizer.Tokens(
                product.PartNumber, product.InternalCode, product.ProductName, product.Description);
            if (candidateTokens.Count == 0) continue;
            var overlap = (decimal)sourceTokens.Intersect(candidateTokens).Count()
                / sourceTokens.Union(candidateTokens).Count();
            if (overlap < FuzzyCandidateFloor) continue;

            var manufacturer = ProductIdentityNormalizer.NormalizeManufacturer(product.Manufacturer);
            var manufacturerMatches = normalizedManufacturer is not null && manufacturer == normalizedManufacturer;
            var confidence = Math.Min(0.89m, 0.35m + (0.45m * overlap) + (manufacturerMatches ? 0.09m : 0m));
            yield return Candidate(product, Math.Round(confidence, 4), ProductResolutionMethods.LocalSimilarity,
                $"Local token similarity {Math.Round(overlap * 100m, 1)}%", null, normalizedManufacturer);
        }
    }

    private static RankedProductCandidate Candidate(
        ProductIdentityCandidate product,
        decimal confidence,
        string method,
        string reason,
        string? matchedValue,
        string? normalizedManufacturer,
        params ProductResolutionEvidence[] evidence)
    {
        var details = new List<ProductResolutionEvidence>(evidence)
        {
            new("catalog-product", product.ProductId.ToString(), matchedValue,
                $"Part={product.PartNumber}; InternalCode={product.InternalCode}; Manufacturer={normalizedManufacturer}")
        };
        return new RankedProductCandidate(product.ProductId, product.PartNumber, product.InternalCode,
            product.Manufacturer, product.ProductName, confidence, method, reason, details);
    }

    private sealed record CatalogSnapshot(
        ProductIdentityCandidate[] Products,
        ApprovedProductReference[] References);

    private sealed record ProductMatchKey(
        long BusinessUnitId,
        string? NormalizedPart,
        string? NormalizedManufacturer,
        string? OriginalPart,
        string? Description);

    private sealed record ProductMatch(
        IReadOnlyList<RankedProductCandidate> RankedCandidates,
        decimal Confidence,
        decimal Margin,
        string? Method,
        ProductResolutionDecisionState DecisionState,
        long? ResolvedProductId,
        bool IsAmbiguous);
}
