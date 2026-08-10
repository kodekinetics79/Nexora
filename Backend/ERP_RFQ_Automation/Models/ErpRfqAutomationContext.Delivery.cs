using ERP_RFQ_Automation.Delivery;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Gate 7 Module 7 — outbound delivery and proof of delivery (FR-DLM-01, 02, 03, 05, 07).
//
// Mapping lives in Delivery/DeliveryModelBuilderExtensions.cs so the module owns its own schema;
// this partial supplies the DbSets and the one hook the Tenancy partial calls. The tenant filters
// are passed in as expressions rather than declared in the extension because CurrentTenantId is a
// private member of this class.
public partial class ErpRfqAutomationContext
{
    public DbSet<DeliveryProof> DeliveryProofs => Set<DeliveryProof>();
    public DbSet<DeliveryProofLine> DeliveryProofLines => Set<DeliveryProofLine>();
    public DbSet<DeliveryShortfallDecision> DeliveryShortfallDecisions => Set<DeliveryShortfallDecision>();

    // Declaring declaration for the hook invoked from OnModelCreatingPartial.
    partial void ConfigureDeliveryModel(ModelBuilder modelBuilder);

    partial void ConfigureDeliveryModel(ModelBuilder modelBuilder)
        => modelBuilder.ApplyDeliveryModel(
            proof => CurrentTenantId == null || proof.BusinessUnitId == CurrentTenantId,
            line => CurrentTenantId == null || line.BusinessUnitId == CurrentTenantId,
            decision => CurrentTenantId == null || decision.BusinessUnitId == CurrentTenantId);
}
