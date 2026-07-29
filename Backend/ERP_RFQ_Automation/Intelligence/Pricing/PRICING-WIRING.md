# Pricing Intelligence Engine - Wiring Notes

The pricing engine is a tenant-scoped, read-only decision aid for RFQ lines. It is
registered through `AddPricingIntelligence()` and exposed through authenticated,
permission-scoped HTTP routes.

## Active Routes

- `GET /api/intelligence/rfqs/{id}/price-preview` returns a shadow preview.
- `POST /api/intelligence/rfqs/{id}/apply-pricing` is a closed compatibility route and
  always returns `409 Conflict`.

Both routes derive the tenant from the authenticated context and require `RFQ Management`
and `Quotations` view permissions. Direct price mutation is prohibited. A user must use
the governed Supplier award and Customer Quote workflow to confirm commercial pricing.

## Admitted Evidence

The only admitted signal is an accepted Customer Quote line for the same tenant, exact
Product and explicit matching currency. Evidence is bounded to the trailing 24 months,
newest first, with at most 100 rows retained per Product. Recency and quantity similarity
affect the advisory confidence.

These legacy inputs are disabled because their current storage contracts do not provide
the tenant-qualified, canonical, explicit-currency evidence required for financial use:

- Supplier price-list view;
- Supplier purchase history;
- raw Supplier Quote projection;
- unstamped Product master pricing.

Supplier purchase history must not be re-enabled until it has a non-null tenant key,
tenant-qualified relationships, a data-bearing backfill, strict PostgreSQL RLS and
cross-tenant negative tests.

## Guardrails

- Unknown or mismatched currency evidence is excluded; no FX rate is inferred.
- A combined total is returned only when every line is priced in one verified currency.
- Mixed currencies remain partitioned in `totals.byCurrency`.
- No cost basis, synthetic margin or binding floor is invented.
- `ApplyPricingAsync` always throws; agent or HTTP callers cannot mutate RFQ prices.
- Empty evidence produces an attention-required line, not a guessed price.

## Main Files

- `PricingModels.cs`: wire contracts.
- `IPricingEngine.cs`: shadow-pricing contract.
- `PricingEngine.cs`: accepted-Quote evidence and fail-closed totals.
- `PricingServiceCollectionExtensions.cs`: dependency registration.
- `PricingTools.cs`: agent-facing read-only preview; mutation compatibility path fails closed.
- `Controllers/PricingIntelligenceController.cs`: authenticated HTTP surface.
