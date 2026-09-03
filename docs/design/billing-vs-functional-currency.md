# Billing currency versus tenant functional currency

Stream 3, item C. Design first; implementation follows this note.

## Problem

A Saudi client that quotes in SAR cannot activate as Production. One column,
`platform.Tenants.BaseCurrencyCode`, does two jobs:

1. **Functional (quoting) currency** — `TenantBaselineSeeder.EnsureBaseCurrencyAsync`
   (`Platform/Services/TenantBaselineSeeder.cs:290-318`) writes it as the tenant's ONE
   `Currency` row with `IsBaseCurrency=true`; FX, ledger books, quotes and orders resolve
   against that row.
2. **Billing currency** — `TenantActivationPolicyService.cs:80-83` requires it to equal
   `"USD"` for `billing.currency-tax`, and `:88-93` requires the pinned rate card's currency
   to equal it for `commercial.rate-card`.

So the frontend forces the field to USD (`provisionValidation.ts:300-313`, default `:183`),
every provisioned tenant quotes in USD, and the remediation text
(`ActivationControlRemediationCatalog.cs:158-167`) tells the operator a non-USD tenant must
be re-provisioned.

Live evidence: tenant 3 (`noor-sons-llc`) `BaseCurrencyCode=USD`; its BU 7 `Currency` row is
USD ("US Dollar", base). `platform.RateCards` row 1 `Currency=USD`, no tenant column.

## The onboarding lane's claim, verified

"Both columns already exist on different tables." Checked against the live schema and the
model snapshot (`MigrationsBaseline/ErpRfqAutomationContextModelSnapshot.cs:19164-19367`):

| concept | column | exists |
|---|---|---|
| functional currency | `public.Currency` (per BU, `IsBaseCurrency`), seeded from `Tenants.BaseCurrencyCode` | yes |
| billing currency | `platform.RateCards.Currency` (per card, forced USD in 4 places), `Plans.MonthlyPriceUsd`, `BillingStatements.Currency` | yes |
| billing currency on `Tenants` | — | **no** |

There is no per-tenant billing-currency column, and none is needed: v1 billing is USD-only
by construction (`BillingStatementService.cs:599-607` 409s any non-USD card at compute), so
the platform billing currency is a platform constant carried on the rate card, not a
per-tenant attribute. **No migration is required.** Item C proceeds.

## Proposed mechanism

```
PlatformBillingCurrency.Code = "USD"                      Billing/, one constant, one place

billing.currency-tax   := (pinned card is null || card.Currency == PlatformBillingCurrency.Code)
                          && (tax number || Internal/Partner)
commercial.rate-card   := card active && card.Currency == PlatformBillingCurrency.Code && effective && lines > 0
Tenants.BaseCurrencyCode := the tenant's FUNCTIONAL currency only (doc + DTO text updated)
```

* `TenantActivationPolicyService` stops reading `BaseCurrencyCode` altogether.
* `PlatformBillingController` / `TenantsController` rate-card refusals compare against the
  constant (they already compared against the literal; this only names it).
* Remediation text for `billing.currency-tax` stops claiming a non-USD tenant must be
  re-provisioned.
* Provisioning wizard: functional currency accepts any ISO-4217 code; default `SAR` for a
  KSA-first product; helper text separates "the currency you quote in" from "Nexora bills
  you in USD". The rate-card resolver filters candidate cards by the billing currency, not
  by the tenant's functional currency.
* The seeder is untouched: it already seeds whatever code it is given (`TenantBaselineSeederTests`
  already prove a SAR profile yields a SAR base row).

## What could go wrong

| risk | mitigation |
|---|---|
| Existing USD tenants change verdict | they do not: card USD + tax → satisfied exactly as before |
| A tenant with no card pinned | `billing.currency-tax` passes on currency (nothing to disagree with) and fails on tax only; `commercial.rate-card` still blocks — same outcome as today, clearer reason |
| Someone re-adds a per-tenant USD assumption | the constant is the single grep target; the test below pins SAR + USD card → satisfied |
| Frontend drafts saved with `USD` default | rehydration keeps stored values; only the default for NEW drafts changes |

## Tests that prove it

* `TenantActivationPolicyTests`: a tenant with `BaseCurrencyCode="SAR"`, tax number,
  USD rate card → `billing.currency-tax` and `commercial.rate-card` satisfied (fails
  against the old code on both); the same tenant pinned to a hypothetical AED card →
  both unsatisfied.
* `TenantBaselineSeederTests` already asserts the seeded `Currency` row is SAR for a SAR
  profile (`:120-129`); referenced, not duplicated.
* `provisionValidation.test.ts`: SAR accepted, default SAR, `US` still refused.

## Rollout / rollback

No schema. Verdicts for existing tenants unchanged. Rollback = revert.

## Product-owner decisions to confirm

1. Wizard default functional currency becomes SAR (was USD).
2. Billing currency is a platform constant (USD) rather than a per-tenant setting; adding a
   per-tenant billing currency later needs a column (Stream 4 migration budget).
