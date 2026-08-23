using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

/// <summary>
/// The ONE set of gates a lead must clear before it becomes an RFQ, shared by every
/// door that can create a lead-linked RFQ:
///
///  1. <c>LeadRepository.ConvertLeadToRfqAsync</c> (POST /api/Lead/{id}/convert-to-rfq),
///  2. <c>LeadConversionIntelligence.ConvertAsync</c> (POST /api/ConversionIntelligence/{id}/convert
///     and the agent tool), and
///  3. <c>RfqRepository.AddAsync</c> when POST /api/Rfq names a LeadId.
///
/// Before this type existed, doors 1 and 2 each carried their own copy of these checks
/// (already drifting in message wording) and door 3 carried none — a raw POST /api/Rfq
/// with a LeadId silently created a second RFQ for a lead that already had one, on an
/// unqualified lead, past the duplicate flag, past extraction review. Duplicated gates
/// are gates that drift; a door with no gate is not a door.
///
/// The database-level backstop for the same invariant is the partial unique index on
/// RFQ."LeadID" (one non-null LeadID per RFQ row): these checks are the friendly,
/// explainable refusal; the index is what makes a racing second insert physically
/// impossible. <see cref="IsDuplicateKey"/> maps that index violation back to a
/// resolvable outcome instead of a 500.
/// </summary>
public static class LeadConversionGate
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> (the exception type every conversion
    /// controller already maps to 409 with a user-renderable message) unless the lead may
    /// be converted to an RFQ right now.
    ///
    /// <para>The caller must have loaded <c>lead.LeadStatus</c>; an unloaded navigation is
    /// indistinguishable from "no status", which would canonicalize to a refusal for the
    /// wrong reason. Fail loudly on the programming error instead of refusing cryptically.</para>
    /// </summary>
    public static void EnsureEligible(Lead lead)
    {
        if (lead.LeadStatusId.HasValue && lead.LeadStatus == null)
            throw new InvalidOperationException(
                "LeadConversionGate requires the LeadStatus navigation to be loaded (Include(l => l.LeadStatus)).");

        var lifecycleCode = LifecyclePolicy.Canonicalize(
            "Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue);
        if (lifecycleCode != "QUALIFIED")
            throw new InvalidOperationException("Only a qualified lead can be converted to an RFQ.");

        // WP-A3 hard block: an unresolved duplicate flag stops conversion at every door.
        if (lead.DuplicateStatus is "suspected" or "confirmed")
            throw new InvalidOperationException(
                $"This lead is flagged as a possible duplicate of lead #{lead.DuplicateOfLeadId}; resolve the duplicate flag first.");

        if (lead.RequiresCommercialReview && !lead.CommercialFactsVerified)
            throw new InvalidOperationException(
                "AI-extracted commercial facts must be approved in extraction review before RFQ conversion.");

        if (!lead.CustomerId.HasValue)
            throw new InvalidOperationException("Resolve the lead customer before creating an RFQ.");
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a unique/primary-key violation — the shape a lost
    /// race against the RFQ."LeadID" partial unique index takes. Covers PostgreSQL
    /// (production), SQLite (the relational test lane) and SQL Server (legacy), mirroring
    /// <c>EmailService.IsDuplicateKeyException</c> which predates the SQLite case.
    /// </summary>
    public static bool IsDuplicateKey(DbUpdateException ex)
    {
        // PostgreSQL unique_violation.
        if (ex.InnerException is Npgsql.PostgresException pg)
            return pg.SqlState == "23505";
        // SQL Server unique/PK violation error numbers.
        if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlServer)
            return sqlServer.Number is 2627 or 2601;
        // SQLite is only ever the relational TEST lane, so the app deliberately does not
        // reference Microsoft.Data.Sqlite — match by type name + SQLite's stable constraint
        // message prefixes instead of a typed catch. (SQLITE_CONSTRAINT_UNIQUE = 2067,
        // _PRIMARYKEY = 1555, both surfaced with these exact message prefixes.)
        if (ex.InnerException is { } inner
            && inner.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException")
            return inner.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)
                || inner.Message.Contains("PRIMARY KEY constraint failed", StringComparison.Ordinal);
        return false;
    }
}
