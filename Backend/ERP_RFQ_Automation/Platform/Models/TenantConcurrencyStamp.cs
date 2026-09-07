using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// Makes <see cref="Tenant.Version"/> actually work.
///
/// <para><b>Why this exists, and why it is not optional.</b> Declaring
/// <c>IsConcurrencyToken()</c> on a plain <c>long</c> does not make anything increment it —
/// Npgsql auto-generates only the <c>xmin</c> system column. Left alone, the value stays 1 for
/// the lifetime of every tenant row, so every UPDATE emits
/// <c>WHERE "Id" = @id AND "Version" = 1</c>, which always matches, and no
/// <c>DbUpdateConcurrencyException</c> can ever be raised. The protection would be documented on
/// the entity, visible in the model, present in the migration — and completely inert.</para>
///
/// <para>That is not a hypothetical: it is exactly what happened to the email-assembly token, and
/// <see cref="Ingestion.Assembly.EmailInquiryConcurrencyStamp"/> exists because of it. Adding a
/// column called Version and believing the job is done is the failure mode this file is here to
/// prevent from recurring one subsystem over.</para>
///
/// <para><b>The failure it prevents.</b> Two console screens issue the same full-object
/// <c>PUT /profile</c>, each echoing the seventeen fields it is not editing from its own
/// snapshot. An operator correcting an address in one tab, while a colleague fixes the legal name
/// in another, silently loses one of the two edits — and nothing records that it happened. The
/// redesigned single-page customer screen makes this MORE likely rather than less, because one
/// commit there carries edits a person may have started ten minutes and two interruptions ago.
/// The token has to be live before those writes are merged.</para>
///
/// <para>Stamped in <c>SaveChanges</c> rather than at each of the dozen tenant write sites,
/// deliberately: a call site that forgets is precisely how a token becomes inert, and there is no
/// write path to <c>platform."Tenants"</c> that does not pass through here.</para>
/// </summary>
internal static class TenantConcurrencyStamp
{
    /// <summary>
    /// Advances the token on every modified tenant, so the WHERE clause EF emits carries the
    /// value the reader actually saw and a stale writer is refused instead of winning.
    /// </summary>
    internal static void Stamp(ChangeTracker changeTracker)
    {
        foreach (var entry in changeTracker.Entries<Tenant>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.Version++;
        }
    }
}
