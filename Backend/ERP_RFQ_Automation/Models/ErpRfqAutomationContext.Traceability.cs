using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Gate 5 Module 5 — material traceability (FR-MTR-01..05).
//
// Mapping lives in Traceability/TraceabilityModelBuilderExtensions.cs so the module owns its own
// schema; this partial supplies the DbSets and the one hook the Tenancy partial calls. The tenant
// filters are passed in as expressions rather than declared in the extension because
// CurrentTenantId is a private member of this class.
public partial class ErpRfqAutomationContext
{
    public DbSet<MaterialLot> MaterialLots => Set<MaterialLot>();
    public DbSet<MaterialLotCertificate> MaterialLotCertificates => Set<MaterialLotCertificate>();
    public DbSet<MaterialLotConsumption> MaterialLotConsumptions => Set<MaterialLotConsumption>();

    // Declaring declaration for the hook invoked from OnModelCreatingPartial.
    partial void ConfigureMaterialTraceabilityModel(ModelBuilder modelBuilder);

    partial void ConfigureMaterialTraceabilityModel(ModelBuilder modelBuilder)
        => modelBuilder.ApplyMaterialTraceabilityModel(
            lot => CurrentTenantId == null || lot.BusinessUnitId == CurrentTenantId,
            certificate => CurrentTenantId == null || certificate.BusinessUnitId == CurrentTenantId,
            consumption => CurrentTenantId == null || consumption.BusinessUnitId == CurrentTenantId);
}
