using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Sourcing-copilot ("Agent") entity configuration, kept in a partial so the large
// scaffolded context file stays untouched — mirrors the Tenancy partial pattern.
// Invoked from ErpRfqAutomationContext.Tenancy.cs's OnModelCreatingPartial via a
// single delegating call to ConfigureAgentModel(modelBuilder) (see that file).
//
// Every tenant-scoped agent entity carries `long BusinessUnitId` and a global query
// filter using the SAME fail-closed pattern as the Tenancy partial:
//   CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId
// (CurrentTenantId is a private member of this same partial class.)
//
// Timestamps use `now()` server defaults, matching the extraction/platform entities,
// and run under Npgsql.EnableLegacyTimestampBehavior (set in Program.cs).
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial's
    // OnModelCreatingPartial. The implementing declaration below supplies the body.
    partial void ConfigureAgentModel(ModelBuilder modelBuilder);

    partial void ConfigureAgentModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentSession>(e =>
        {
            e.ToTable("AgentSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedByName).HasMaxLength(200);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.BusinessUnitId, x.UpdatedOn }).HasDatabaseName("IX_AgentSessions_BU_UpdatedOn");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentMessage>(e =>
        {
            e.ToTable("AgentMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Content).HasColumnType("text");
            e.Property(x => x.ToolName).HasMaxLength(100);
            e.Property(x => x.ToolInput).HasColumnType("jsonb");
            e.Property(x => x.ToolResult).HasColumnType("jsonb");
            e.HasIndex(x => new { x.SessionId, x.Sequence }).HasDatabaseName("IX_AgentMessages_Session_Sequence");
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentApproval>(e =>
        {
            e.ToTable("AgentApprovals");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(100).IsRequired();
            e.Property(x => x.InputJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.Property(x => x.RequestedBy).HasMaxLength(200);
            e.Property(x => x.DecidedBy).HasMaxLength(200);
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.BusinessUnitId, x.Status }).HasDatabaseName("IX_AgentApprovals_BU_Status");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentAuditLog>(e =>
        {
            e.ToTable("AgentAuditLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.ToolName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Decision).HasMaxLength(40).IsRequired();
            e.Property(x => x.InputJson).HasColumnType("jsonb");
            e.Property(x => x.ResultSummary).HasMaxLength(1000);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.BusinessUnitId, x.CreatedOn }).HasDatabaseName("IX_AgentAuditLogs_BU_CreatedOn");
            // Append-only audit trail, also tenant-scoped by BusinessUnitId.
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentPolicy>(e =>
        {
            e.ToTable("AgentPolicies");
            e.HasKey(x => x.Id);
            e.Property(x => x.AutonomyLevel).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.MaxAutoAwardValue).HasColumnType("numeric(18,2)");
            e.Property(x => x.MaxAutoOrderValue).HasColumnType("numeric(18,2)");
            e.Property(x => x.PerToolOverrides).HasColumnType("jsonb");
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => x.BusinessUnitId).IsUnique().HasDatabaseName("UX_AgentPolicies_BU");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SupplierSolicitation>(e =>
        {
            e.ToTable("SupplierSolicitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.BusinessUnitId, x.RfqId }).HasDatabaseName("IX_SupplierSolicitations_BU_Rfq");
            e.HasIndex(x => new { x.BusinessUnitId, x.RfqId, x.SupplierId }).HasDatabaseName("IX_SupplierSolicitations_BU_Rfq_Supplier");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SourcingAward>(e =>
        {
            e.ToTable("SourcingAwards");
            e.HasKey(x => x.Id);
            e.Property(x => x.UnitPrice).HasColumnType("numeric(18,2)");
            e.Property(x => x.Quantity).HasColumnType("numeric(18,2)");
            e.Property(x => x.TotalValue).HasColumnType("numeric(18,2)");
            e.Property(x => x.Rationale).HasMaxLength(2000);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.BusinessUnitId, x.RfqId }).HasDatabaseName("IX_SourcingAwards_BU_Rfq");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
