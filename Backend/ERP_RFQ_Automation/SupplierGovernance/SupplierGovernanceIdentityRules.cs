using System;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ERP_RFQ_Automation.SupplierGovernance;

/// <summary>
/// Stamps the two governance identity columns — <see cref="Supplier.ConcurrencyToken"/> and
/// <see cref="Supplier.EffectiveFrom"/> — on every Supplier that reaches the database, whatever
/// wrote it.
///
/// <para><b>The defect this closes.</b> <c>SupplierRepository.AddAsync</c> set both; the bulk
/// Excel import (<c>SupplierUploaderService</c>) did not, and both columns are nullable with no
/// store default and no trigger. A bulk-imported Supplier therefore landed with
/// <c>ConcurrencyToken IS NULL</c>, and <c>SupplierGovernanceService.GovernAsync</c> compares that
/// null against the caller's non-nullable <c>ExpectedConcurrencyToken</c>: the comparison can
/// never succeed, so every governance decision on a bulk-imported Supplier was rejected with
/// "The Supplier changed after it was loaded. Refresh and review the latest evidence." A refresh
/// cannot help — the read path returns the same null — so the Supplier was permanently
/// ungovernable, and the message told the operator to do the one thing that could not work.
/// A second, quieter consequence: <c>SupplierController.Update</c> only enforces the optimistic
/// concurrency check <c>if (existing.ConcurrencyToken.HasValue)</c>, so a bulk-imported Supplier
/// also had no lost-update protection on the edit screen.</para>
///
/// <para><b>Why here and not in the uploader.</b> Setting it in the uploader would state the same
/// rule in two places and leave the next write path — a service, a worker, a future importer — to
/// remember it a third time. Every one of them goes through
/// <c>ErpRfqAutomationContext.SaveChanges</c>, which is the same argument
/// <c>MasterDataAuditInterceptor</c> already makes for capturing the audit there rather than in
/// the controllers.</para>
///
/// <para><b>Why not a store default.</b> <c>gen_random_uuid()</c> / <c>now()</c> would be
/// PostgreSQL-only, so the SQLite test lane and any offline tooling would keep writing nulls and
/// the invariant could never be proved by a test. Worse, EF omits a property still holding its
/// CLR default from the INSERT — the trap already documented on
/// <c>TenantBaselineSeeder.EnsureUnitsOfMeasureAsync</c> — so reading the generated value back
/// onto a <see cref="ConcurrencyCheckAttribute"/> property would need
/// <c>ValueGeneratedOnAdd</c>, which in turn makes EF ignore a token the application DID supply.
/// The column default is left as it is; the assignment moves to the one place both paths pass
/// through. Rows already carrying NULL are repaired by the
/// <c>BackfillSupplierGovernanceIdentity</c> migration.</para>
/// </summary>
internal static class SupplierGovernanceIdentityRules
{
    /// <summary>
    /// Fills either column when it is absent, and never overwrites a value the caller set — a
    /// Supplier arriving with a token from <c>SupplierRepository</c> or from
    /// <c>SupplierGovernanceService</c> keeps exactly that token, so the optimistic-concurrency
    /// handshake those two implement is untouched.
    /// </summary>
    /// <remarks>
    /// <c>Modified</c> is stamped as well as <c>Added</c>: a Supplier imported before this rule
    /// existed still carries NULL, and healing it on the first save that touches it means the
    /// application repairs itself on any database the backfill has not been applied to.
    /// </remarks>
    public static void Stamp(ChangeTracker? changeTracker)
    {
        if (changeTracker is null) return;

        foreach (var entry in changeTracker.Entries<Supplier>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            var supplier = entry.Entity;
            supplier.ConcurrencyToken ??= Guid.NewGuid();

            // The same rule SupplierRepository.AddAsync applies: governance is effective from the
            // moment the record came into existence. CreatedOn is NOT NULL in the store, so the
            // UtcNow fallback only covers a caller that never set it at all.
            supplier.EffectiveFrom ??= supplier.CreatedOn == default ? DateTime.UtcNow : supplier.CreatedOn;
        }
    }
}
