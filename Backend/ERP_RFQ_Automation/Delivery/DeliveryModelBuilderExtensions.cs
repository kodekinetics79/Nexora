using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP_RFQ_Automation.Delivery;

/// <summary>
/// Gate 7 Module 7 — outbound delivery and proof-of-delivery schema.
///
/// <para>Three new tenant-scoped tables: <c>delivery_proofs</c>, <c>delivery_proof_lines</c> and
/// <c>delivery_shortfall_decisions</c>. Every one needs BOTH a row-level-security policy and an
/// explicit <c>GRANT</c> in the migration that creates it — the schema revoked default privileges,
/// so a policy without a grant raises <c>42501</c> before any row predicate is evaluated. Three
/// tables shipped exactly that defect two gates ago; the delta is stated in the gate report.</para>
///
/// <para>Also two columns on the existing <c>Shipments</c> table (<c>DeliveryStatus</c>,
/// <c>DeliveryCityID</c> and the two attribution columns beside them) and one alternate key on
/// <c>ShipmentItems</c>. <c>ShipmentItems</c> carries no tenant column — its isolation is
/// parent-derived — so the only key a proof line can point at without being able to name another
/// shipment's line is the pair (line, shipment).</para>
///
/// <para>Deliberately portable: no <c>jsonb</c>, no PostgreSQL-only expressions, so the SQLite lane
/// exercises the same relationships. The <c>CHECK</c> constraints are NOT enforced there (that lane
/// runs with <c>ignore_check_constraints</c> on), which is why every invariant they express is also
/// enforced in <c>DeliveryConfirmationService</c>.</para>
/// </summary>
public static class DeliveryModelBuilderExtensions
{
    public static void ApplyDeliveryModel(
        this ModelBuilder modelBuilder,
        Expression<Func<DeliveryProof, bool>> proofTenantFilter,
        Expression<Func<DeliveryProofLine, bool>> lineTenantFilter,
        Expression<Func<DeliveryShortfallDecision, bool>> decisionTenantFilter)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureShipmentDeliveryColumns(modelBuilder);
        ConfigureProofs(modelBuilder, proofTenantFilter);
        ConfigureProofLines(modelBuilder, lineTenantFilter);
        ConfigureDecisions(modelBuilder, decisionTenantFilter);
    }

    private static void ConfigureShipmentDeliveryColumns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.Ignore(x => x.HasDespatched);
            entity.Ignore(x => x.IsConfirmed);

            // The code default and the backfill default are the SAME WORD but NOT the same
            // meaning, and that is stated here because wiring-contract failure #10 is exactly this
            // trap. A NEW shipment defaults to SCHEDULED — nothing has left. An EXISTING row cannot
            // be SCHEDULED, because until this gate every shipment issued stock the moment it was
            // created, so every row already in the table has despatched. The migration must backfill
            // 'DISPATCHED', not the column default. See the gate report.
            entity.Property(x => x.DeliveryStatus)
                .HasMaxLength(24).IsRequired()
                .HasDefaultValue(DeliveryStatuses.Scheduled);
            entity.Property(x => x.DeliveryStatusChangedBy).HasMaxLength(255);
            entity.Property(x => x.DeliveryCityId).HasColumnName("DeliveryCityID");

            entity.HasCheckConstraint("CK_Shipments_DeliveryStatus",
                "\"DeliveryStatus\" IN ('SCHEDULED','DISPATCHED','IN_TRANSIT','DELIVERED',"
                + "'DELIVERY_EXCEPTION','CANCELLED')");
            // A state change with no name and no time behind it is not attributable, and this is
            // the column an operator's account of a delivery dispute is reconstructed from.
            entity.HasCheckConstraint("CK_Shipments_DeliveryStatusAttribution",
                "(\"DeliveryStatusChangedOn\" IS NULL AND \"DeliveryStatusChangedBy\" IS NULL) "
                + "OR (\"DeliveryStatusChangedOn\" IS NOT NULL AND \"DeliveryStatusChangedBy\" IS NOT NULL)");

            // The despatch board and the ledger both filter on it; without this every delivered
            // quantity in the system is a sequential scan of every shipment ever raised.
            entity.HasIndex(x => new { x.BusinessUnitId, x.DeliveryStatus })
                .HasDatabaseName("IX_Shipments_BU_DeliveryStatus");

            // Tenant-qualified so a shipment can never name another tenant's city. SetCity's key is
            // CityID alone, so the pair (BUID, CityID) has to be made addressable first.
            entity.HasOne(x => x.DeliveryCity).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DeliveryCityId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, CityId = x.CityId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SetCity>().HasAlternateKey(x => new { x.Buid, x.CityId });

        // ShipmentItems has no BusinessUnitId. The pair (line, shipment) is the only foreign key a
        // proof line can carry that cannot address another shipment's — or another tenant's — line.
        modelBuilder.Entity<ShipmentItem>().HasAlternateKey(x => new { x.Id, x.ShipmentId });
    }

    private static void ConfigureProofs(
        ModelBuilder modelBuilder, Expression<Func<DeliveryProof, bool>> tenantFilter)
    {
        modelBuilder.Entity<DeliveryProof>(entity =>
        {
            entity.ToTable("delivery_proofs");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Ignore(x => x.HasGpsFix);

            entity.Property(x => x.ReceivedByName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ReceivedByContact).HasMaxLength(255);
            entity.Property(x => x.ReceivedByPosition).HasMaxLength(255);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.NexoraSerial).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64);
            entity.Property(x => x.RecordedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();

            // WGS-84. 7 decimals is ~1cm, far beyond what a handset resolves, which is the point of
            // carrying the accuracy alongside: a coordinate with no error bar reads as certainty.
            entity.Property(x => x.GpsLatitude).HasPrecision(10, 7);
            entity.Property(x => x.GpsLongitude).HasPrecision(10, 7);
            entity.Property(x => x.GpsAccuracyMeters).HasPrecision(9, 2);

            entity.HasCheckConstraint("CK_delivery_proofs_Gps",
                "(\"GpsLatitude\" IS NULL AND \"GpsLongitude\" IS NULL) "
                + "OR (\"GpsLatitude\" IS NOT NULL AND \"GpsLongitude\" IS NOT NULL "
                + "AND \"GpsLatitude\" BETWEEN -90 AND 90 AND \"GpsLongitude\" BETWEEN -180 AND 180)");
            // A fix with no time is not evidence of where anybody was when they signed.
            entity.HasCheckConstraint("CK_delivery_proofs_GpsCapturedOn",
                "\"GpsCapturedOn\" IS NULL OR \"GpsLatitude\" IS NOT NULL");
            entity.HasCheckConstraint("CK_delivery_proofs_GpsAccuracy",
                "\"GpsAccuracyMeters\" IS NULL OR \"GpsAccuracyMeters\" > 0");
            entity.HasCheckConstraint("CK_delivery_proofs_Version", "\"Version\" >= 1");

            // ONE confirmation per shipment. A second signature against the same consignment would
            // double-count every accepted unit on it, and "which POD is the real one" is not a
            // question a dispute should have to answer.
            entity.HasIndex(x => new { x.BusinessUnitId, x.ShipmentId }).IsUnique()
                .HasDatabaseName("UX_delivery_proofs_BU_Shipment");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_delivery_proofs_BU_IdempotencyKey");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasDatabaseName("IX_delivery_proofs_BU_CommercialCase");

            entity.HasOne<Shipment>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Attachment>().WithMany()
                .HasForeignKey(x => x.SignatureEvidenceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Attachment>().WithMany()
                .HasForeignKey(x => x.StampEvidenceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Attachment>().WithMany()
                .HasForeignKey(x => x.PhotoEvidenceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });
    }

    private static void ConfigureProofLines(
        ModelBuilder modelBuilder, Expression<Func<DeliveryProofLine, bool>> tenantFilter)
    {
        modelBuilder.Entity<DeliveryProofLine>(entity =>
        {
            entity.ToTable("delivery_proof_lines");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Ignore(x => x.RefusedQuantity);
            entity.Ignore(x => x.IsShort);

            entity.Property(x => x.DespatchedQuantity).HasPrecision(18, 6);
            entity.Property(x => x.AcceptedQuantity).HasPrecision(18, 6);
            entity.Property(x => x.ExceptionReasonCode).HasMaxLength(32);
            entity.Property(x => x.ExceptionNote).HasMaxLength(1000);
            entity.Property(x => x.Notes).HasMaxLength(500);

            entity.HasCheckConstraint("CK_delivery_proof_lines_Quantities",
                "\"DespatchedQuantity\" > 0 AND \"AcceptedQuantity\" >= 0 "
                + "AND \"AcceptedQuantity\" <= \"DespatchedQuantity\"");
            entity.HasCheckConstraint("CK_delivery_proof_lines_Reason",
                "\"ExceptionReasonCode\" IS NULL "
                + "OR \"ExceptionReasonCode\" IN ('SHORT_SHIPMENT','DAMAGED','REJECTED','LOST_IN_TRANSIT')");
            // The invariant the whole of FR-DLM-07 rests on, at the database rather than only in
            // the service: a shortfall always says why, and a clean line never invents a reason.
            entity.HasCheckConstraint("CK_delivery_proof_lines_ShortfallHasReason",
                "(\"AcceptedQuantity\" < \"DespatchedQuantity\" AND \"ExceptionReasonCode\" IS NOT NULL) "
                + "OR (\"AcceptedQuantity\" = \"DespatchedQuantity\" AND \"ExceptionReasonCode\" IS NULL)");

            // One proof line per shipment line. Two rows for one line would be two answers to
            // "how much did they take".
            entity.HasIndex(x => new { x.BusinessUnitId, x.DeliveryProofId, x.ShipmentItemId }).IsUnique()
                .HasDatabaseName("UX_delivery_proof_lines_BU_Proof_ShipmentItem");
            // The FR-DLM-02 read key: cumulative accepted for an order line, one indexed predicate.
            entity.HasIndex(x => new { x.BusinessUnitId, x.OrderItemId })
                .HasDatabaseName("IX_delivery_proof_lines_BU_OrderItem");
            entity.HasIndex(x => new { x.BusinessUnitId, x.OrderId })
                .HasDatabaseName("IX_delivery_proof_lines_BU_Order");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasDatabaseName("IX_delivery_proof_lines_BU_Shipment");

            entity.HasOne<DeliveryProof>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DeliveryProofId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Shipment>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ShipmentItem>().WithMany()
                .HasForeignKey(x => new { x.ShipmentItemId, x.ShipmentId })
                .HasPrincipalKey(x => new { x.Id, x.ShipmentId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Order>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrderItem>().WithMany()
                .HasForeignKey(x => new { x.OrderItemId, x.OrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });
    }

    private static void ConfigureDecisions(
        ModelBuilder modelBuilder, Expression<Func<DeliveryShortfallDecision, bool>> tenantFilter)
    {
        modelBuilder.Entity<DeliveryShortfallDecision>(entity =>
        {
            entity.ToTable("delivery_shortfall_decisions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

            entity.Property(x => x.Decision).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.DecidedBy).HasMaxLength(255).IsRequired();

            entity.HasCheckConstraint("CK_delivery_shortfall_decisions_Decision",
                "\"Decision\" IN ('RESUPPLY','CREDIT')");

            // One decision per shortfall, and the uniqueness is the append-only guarantee: a second
            // answer cannot be written, so the row cannot be quietly reversed after the exception
            // centre has already closed the case off it.
            entity.HasIndex(x => new { x.BusinessUnitId, x.DeliveryProofLineId }).IsUnique()
                .HasDatabaseName("UX_delivery_shortfall_decisions_BU_ProofLine");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasDatabaseName("IX_delivery_shortfall_decisions_BU_Shipment");

            entity.HasOne<DeliveryProofLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DeliveryProofLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Shipment>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });
    }
}
