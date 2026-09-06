using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Quote ownership. Kept in a partial so the scaffolded Quote.cs stays untouched; the column and
// its foreign key are configured in Models/ErpRfqAutomationContext.QuoteOwnership.cs.
public partial class Quote
{
    /// <summary>
    /// The user this quote belongs to, for the read scopes that must answer "whose money is
    /// this" — the dashboard funnel above all.
    ///
    /// <para>Ownership used to be inferred from <see cref="CreatedBy"/>, a free-text actor string
    /// matched against a user's email or "First Last". That is a display field: it carries
    /// "System", an importer's name, an address belonging to nobody in the tenant, and it does not
    /// move when the work does. Scoping a headline money figure on it means a rename or a shared
    /// mailbox silently moves revenue between people.</para>
    ///
    /// <para>NULL means the owner is genuinely not established, and it is left NULL wherever the
    /// old free text does not resolve to exactly one user in the same business unit. A wrong owner
    /// is worse than no owner: it puts one rep's revenue under another rep's heading, which is the
    /// failure this column exists to prevent. Readers below tenant scope therefore exclude unowned
    /// quotes and state how many they excluded rather than quietly absorbing them.</para>
    /// </summary>
    public long? OwnerUserId { get; set; }
}

/// <summary>
/// The one rule that turns a free-text actor into a quote owner, shared by the write paths that
/// create a quote and by the migration that backfilled the existing rows.
///
/// <para>It is deliberately the SAME match the workload board has always used — email, or
/// "First Last", case-insensitively — so the backfill reproduces the ownership the product has
/// been showing rather than inventing a second, differently-shaped answer. What it adds is the
/// unambiguity requirement: two users who both answer to the string leave the quote unowned.</para>
/// </summary>
public static class QuoteOwnerAttribution
{
    /// <summary>
    /// The user in <paramref name="businessUnitId"/> that <paramref name="createdBy"/> names, or
    /// null when it names nobody or more than one.
    ///
    /// <para>The candidates are matched in memory rather than in the predicate because the name
    /// comparison has to be case-insensitive in both dialects the product runs on, and
    /// <c>ToLower()</c> on a concatenation is the kind of expression that silently falls back to
    /// client evaluation on one of them. A business unit's user list is tens of rows.</para>
    /// </summary>
    public static async Task<long?> ResolveAsync(
        ErpRfqAutomationContext context,
        long businessUnitId,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        var actor = createdBy?.Trim();
        if (string.IsNullOrEmpty(actor)) return null;

        var candidates = await context.Users.AsNoTracking()
            .Where(user => user.Buid == businessUnitId)
            .Select(user => new { user.Id, user.Email, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        var matches = candidates
            .Where(user =>
                string.Equals(user.Email, actor, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"{user.FirstName} {user.LastName}".Trim(), actor, StringComparison.OrdinalIgnoreCase))
            .Select(user => user.Id)
            .Distinct()
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }
}
