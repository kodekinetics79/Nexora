namespace ERP_RFQ_Automation.Models;

// The tenant's fallback lead owner. Declared in a partial under CommercialRouting/ — with the
// Models namespace, the same way BusinessUnit.TaxRegistration.cs sits beside the tax module it
// belongs to — because it is routing configuration that happens to hang off the business unit,
// and it is mapped by CommercialRoutingModelBuilderExtensions rather than by the scaffolded
// context. Nothing outside CommercialRouting reads it.
public partial class BusinessUnit
{
    /// <summary>
    /// "When Nexora can't work out who owns an inquiry, give it to ___."
    ///
    /// <para>ONE setting the customer fills in, deliberately in place of an inference engine. Until
    /// it existed, an inquiry the engine could not place was parked on the routing queue forever —
    /// and on a tenant with no per-customer ownership rows, which is every tenant on day one, that
    /// was EVERY inquiry. The queue is the right destination for a tenant that wants to triage by
    /// hand; it is the wrong destination for a tenant that just wants the work to land on someone.
    /// This is how that tenant says so, in one field, once.</para>
    ///
    /// <para>Null means "no fallback" and restores exactly the previous behaviour. A user named
    /// here still has to pass the ordinary availability test at routing time, so a name that is
    /// deactivated, loses its governed profile or runs out of capacity quietly stops being used
    /// instead of quietly swallowing work. That is also why there is no foreign key: a
    /// <c>Restrict</c> edge would make the fallback owner undeletable, and a dangling id is already
    /// harmless — it fails the availability test and the inquiry goes to the queue.</para>
    ///
    /// <para>Deliberately NOT round-robin or team load balancing. On a team small enough to need a
    /// fallback at all, account continuity is worth more than even distribution, and the owner
    /// asked for one setting rather than a second scheduler to reason about.</para>
    /// </summary>
    public long? DefaultLeadOwnerUserId { get; set; }

    /// <summary>Who last set or cleared <see cref="DefaultLeadOwnerUserId"/>. A tenant-wide routing
    /// default that silently decides who gets work must be answerable for.</summary>
    public long? DefaultLeadOwnerSetByUserId { get; set; }

    /// <summary>When <see cref="DefaultLeadOwnerUserId"/> was last set or cleared (UTC).</summary>
    public DateTime? DefaultLeadOwnerSetOn { get; set; }
}
