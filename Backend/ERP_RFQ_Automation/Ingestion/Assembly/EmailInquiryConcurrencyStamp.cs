using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Makes the optimistic-concurrency token on the email-assembly aggregate actually work.
///
/// <para><b>Why this exists.</b> Both entities declared <c>IsConcurrencyToken()</c> on a plain
/// <c>int</c> and nothing ever incremented it. Npgsql does not auto-generate <c>int</c> tokens —
/// only the <c>xmin</c> system column behaves that way — so the value stayed 0 for the lifetime
/// of every row. Every UPDATE therefore emitted <c>WHERE Id = @id AND ConcurrencyVersion = 0</c>,
/// which always matched, and no <c>DbUpdateConcurrencyException</c> could ever be raised. The
/// protection was documented on the entity, visible in the model, and inert.</para>
///
/// <para>The failure it was supposed to prevent is real: two workers finishing the last two
/// components of one message each read a snapshot in which the other is still <c>Extracting</c>,
/// both compute a status from that stale view, and the later write silently overwrites the
/// earlier — leaving a message with every component terminal permanently stranded one step short
/// of <c>ReadyForAssembly</c>, with no sweeper to find it.</para>
///
/// <para>Incrementing in <c>SaveChanges</c> rather than at each call site is deliberate: a call
/// site that forgets is exactly how the token became inert in the first place, and there is no
/// write path to these tables that does not pass through here.</para>
/// </summary>
internal static class EmailInquiryConcurrencyStamp
{
    /// <summary>
    /// Advances the token on every modified assembly and component so the WHERE clause EF emits
    /// carries the value the reader actually saw.
    /// </summary>
    internal static void Stamp(ChangeTracker changeTracker)
    {
        foreach (var entry in changeTracker.Entries<EmailInquiryAssembly>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.ConcurrencyVersion++;
        }

        foreach (var entry in changeTracker.Entries<EmailInquiryComponent>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.ConcurrencyVersion++;
        }
    }
}
