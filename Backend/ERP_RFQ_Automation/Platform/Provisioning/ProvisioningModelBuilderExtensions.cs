using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// EF configuration for the durable-provisioning tables, kept in the owning module so the context
/// needs exactly one delegating call — the same splice discipline
/// <c>ApplyTenantOnboardingModel</c> and <c>ConfigureCommercialFinance</c> already use.
/// </summary>
public static class ProvisioningModelBuilderExtensions
{
    public static ModelBuilder ApplyTenantProvisioningModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ProvisioningExecution>(entity =>
        {
            // Platform schema, alongside ImpersonationSessions and TenantAdminInvitations: a
            // control-plane record ABOUT a tenant, written before the tenant exists. See the
            // entity docs for why it carries NO global query filter — adding one would enrol it
            // in the RLS-policy expectation of PostgreSqlProductionDialectTests, which no
            // pre-tenant table can satisfy.
            entity.ToTable("ProvisioningExecutions", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(x => x.RequestFingerprint).IsRequired().HasMaxLength(64);
            entity.Property(x => x.RequestPayload).IsRequired().HasColumnType("jsonb");
            entity.Property(x => x.ResultPayload).HasColumnType("jsonb");
            entity.Property(x => x.AdminPasswordHash).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Slug).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(256);
            entity.Property(x => x.AdminEmail).IsRequired().HasMaxLength(320);
            entity.Property(x => x.AdminActivation).IsRequired().HasMaxLength(16);
            entity.Property(x => x.CorrelationId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.RequestedBy).IsRequired().HasMaxLength(320);
            entity.Property(x => x.CancelledBy).HasMaxLength(320);
            entity.Property(x => x.CancellationReason).HasMaxLength(1000);
            entity.Property(x => x.CurrentStep).HasMaxLength(64);
            entity.Property(x => x.FailedStep).HasMaxLength(64);
            entity.Property(x => x.FailureReason).HasMaxLength(2000);
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);

            // Stored as its NAME. An execution row is read during a support investigation months
            // later, and reordering the enum must never turn a historical Failed into a Cancelled.
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(16);

            // A plain concurrency token with no store default: the runner increments it by hand,
            // so EF compares the value it read against the value in the row and a claim that
            // lost a race fails loudly instead of overwriting the winner's lease.
            entity.Property(x => x.Version).IsConcurrencyToken();

            // THE duplicate-tenant guard. Unique rather than "checked first", because a check
            // is a read-then-write and a double-click is two writes racing: the database is the
            // only place the second one can be refused with certainty.
            entity.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("UX_ProvisioningExecutions_IdempotencyKey");

            // The SECOND guard, for callers that send no key. One slug may have at most one
            // execution that is still Pending, Running or Failed — so a double-click, a client
            // retry or a proxy replay cannot start a rival attempt on the same tenant even
            // without an idempotency key.
            //
            // Failed is inside the guard on purpose. A failed execution still OWNS its slug and
            // the rows its early steps committed; letting a fresh submit claim the same slug
            // would strand those rows with nothing pointing at them. The operator retries it or
            // cancels it — both are one click, and both are recorded.
            //
            // Succeeded is outside it: from then on platform."Tenants".Slug's own unique index
            // is the authority, and it is a stronger one.
            entity.HasIndex(x => x.Slug)
                .IsUnique()
                .HasFilter("\"State\" IN ('Pending', 'Running', 'Failed')")
                .HasDatabaseName("UX_ProvisioningExecutions_LiveSlug");

            // The runner's claim query: oldest first among the work that is actually available.
            entity.HasIndex(x => new { x.State, x.LeaseUntil, x.CreatedOn })
                .HasDatabaseName("IX_ProvisioningExecutions_Claim");

            // The console's list: newest attempts first, filtered by state.
            entity.HasIndex(x => x.CreatedOn)
                .HasDatabaseName("IX_ProvisioningExecutions_CreatedOn");

            // "Show me the execution that created this tenant" — the support path from a tenant
            // detail screen back to how it came to exist.
            entity.HasIndex(x => x.TenantId)
                .HasDatabaseName("IX_ProvisioningExecutions_TenantId");

            entity.HasMany(x => x.Steps)
                .WithOne(x => x.Execution)
                .HasForeignKey(x => x.ExecutionId)
                // Cascade: a step has no meaning without its execution, and the pair is the unit
                // an operator would ever delete.
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProvisioningStep>(entity =>
        {
            entity.ToTable("ProvisioningSteps", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.StepCode).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.FailureCode).HasMaxLength(64);
            entity.Property(x => x.FailureReason).HasMaxLength(2000);
            entity.Property(x => x.Detail).HasColumnType("jsonb");

            // One row per step per execution, forever. The runner UPDATES this row on every
            // retry rather than appending, so "attempt 4 of the baseline seed" is one row with
            // AttemptCount = 4 — a step list the console can render without deduplicating.
            entity.HasIndex(x => new { x.ExecutionId, x.StepCode })
                .IsUnique()
                .HasDatabaseName("UX_ProvisioningSteps_Execution_StepCode");

            // The console reads a whole execution's steps in display order.
            entity.HasIndex(x => new { x.ExecutionId, x.Ordinal })
                .HasDatabaseName("IX_ProvisioningSteps_Execution_Ordinal");
        });

        modelBuilder.Entity<ProvisioningDraft>(entity =>
        {
            entity.ToTable("ProvisioningDrafts", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name).IsRequired().HasMaxLength(256);
            entity.Property(x => x.OwnerEmail).IsRequired().HasMaxLength(320);
            entity.Property(x => x.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(x => x.Version).IsConcurrencyToken();

            // "My drafts, most recently touched first" — the only way this table is ever read.
            entity.HasIndex(x => new { x.OwnerEmail, x.UpdatedOn })
                .HasDatabaseName("IX_ProvisioningDrafts_Owner_UpdatedOn");
        });

        return modelBuilder;
    }
}

/// <summary>
/// Applies <see cref="ProvisioningModelBuilderExtensions.ApplyTenantProvisioningModel"/> without
/// editing the context.
///
/// <para><b>Why this exists.</b> The splice line belongs in
/// <c>ErpRfqAutomationContext.Tenancy.cs</c>'s <c>OnModelCreatingPartial</c>, next to
/// <c>ApplyTenantOnboardingModel()</c>, and that is where it should end up. Until it lands, this
/// customiser is how the module is exercised end to end: <c>ReplaceService&lt;IModelCustomizer&gt;</c>
/// on the options builder runs the context's own <c>OnModelCreating</c> first and then adds
/// these three tables, which is enough for the whole engine and its tests to run against a real
/// relational database.</para>
///
/// <para>Harmless once the splice lands. EF model configuration is idempotent — configuring an
/// entity twice re-applies the same builder calls — so a deployment that has both is in exactly
/// the same state as one that has only the splice.</para>
/// </summary>
public sealed class ProvisioningModelCustomizer : RelationalModelCustomizer
{
    public ProvisioningModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.ApplyTenantProvisioningModel();
    }
}
