using ERP_RFQ_Automation.Fx;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Foreign-exchange authority (Fx/). Effective-dated, approval-gated rates plus the
// per-document approved snapshot that makes a historical conversion reproducible.
//
// CONVENTION NOTE — read before extending this module.
// Every other module here configures its entities fluently and splices a single
// Configure<Module>Model(modelBuilder) call into ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial. FX deliberately does NOT do that: `partial void
// OnModelCreatingPartial` permits exactly one implementation, that implementation lives in
// ErpRfqAutomationContext.Tenancy.cs, and this change set was not authorised to edit that
// file. So FxRate/FxRateSnapshot carry their table names, precision and indexes as data
// annotations on the entities themselves (Fx/FxEntities.cs), which EF Core applies by
// convention from these DbSet declarations alone — no splice point required.
//
// TENANT ISOLATION — both layers are now closed, and neither is optional here, because these
// two tables decide what rate a customer's quote is converted at:
//   * Database: 20260804203000_TenantIsolationForFxAuthority enables RLS and creates the
//     nexora_tenant_isolation policies. This constrains authenticated HTTP requests, which
//     execute as nexora_tenant_app.
//   * EF: global query filters are registered in ErpRfqAutomationContext.Tenancy.cs alongside
//     the other ~150 filtered entities. This is the layer that matters for background workers,
//     because nexora_pipeline_app is created BYPASSRLS and RLS does not constrain it.
//   * FxConversionService additionally filters BusinessUnitId explicitly in every query, so
//     tenant scoping does not rest on any single mechanism.
//
// Still carried as annotations rather than fluent config, and worth revisiting whenever this
// module next gets a splice point:
//   * The partial unique index ("one open-ended rate per pair", HasFilter on EffectiveTo IS
//     NULL) cannot be expressed as an annotation. The CK_FxRates_* check constraints are
//     created directly by the Module05 migration instead.
//   * FKs to Currency (composite (BusinessUnitId, CurrencyId) -> (BusinessUnitId, Id) with
//     DeleteBehavior.Restrict, per the Procurement precedent) are not declared; the currency
//     pair is validated in CurrencyController before an FxRate row is written.
public partial class ErpRfqAutomationContext
{
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<FxRateSnapshot> FxRateSnapshots => Set<FxRateSnapshot>();
}
