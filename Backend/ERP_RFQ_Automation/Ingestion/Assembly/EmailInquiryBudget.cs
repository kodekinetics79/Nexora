using System;
using System.Threading;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// The ONE budget for a whole message tree, nested content included.
///
/// <para><b>Why it is an object and not a set of parameters.</b> Before recursion existed the
/// limits were locals in a single loop, and the embedded-message branch was handed
/// <c>limits.MaxTotalBytes</c> afresh. The moment traversal recurses, that shape hands every
/// nested level a brand-new allowance: three forwards each carrying 90 MB would each pass a
/// "100 MB total" check and the message would cost 270 MB. Passing one mutable instance down
/// every branch makes the shared budget true by construction rather than by every call site
/// remembering to subtract.</para>
///
/// <para>It is deliberately mutable and deliberately not thread-safe: planning walks one message
/// on one thread, and making it concurrent would invite someone to plan branches in parallel,
/// which would make the component ordinals non-deterministic and break replay identity.</para>
/// </summary>
public sealed class EmailInquiryBudget
{
    private readonly EmailInquiryLimits _limits;

    public EmailInquiryBudget(EmailInquiryLimits limits, CancellationToken cancellationToken = default)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        RemainingComponents = limits.MaxComponents;
        RemainingBytes = limits.MaxTotalBytes;
        CancellationToken = cancellationToken;
    }

    /// <summary>Per-component ceiling. Constant — it does not deplete.</summary>
    public long ComponentLimit => _limits.MaxComponentBytes;

    /// <summary>Deepest nesting the planner will follow. 0 means "do not open forwards".</summary>
    public int MaxNestingDepth => _limits.MaxNestingDepth;

    /// <summary>Ceiling below which a cid-referenced inline image may be decoration.</summary>
    public long InlineAssetMaxBytes => _limits.InlineAssetMaxBytes;

    /// <summary>Components still allowed across the ENTIRE tree.</summary>
    public int RemainingComponents { get; private set; }

    /// <summary>Decoded bytes still allowed across the ENTIRE tree.</summary>
    public long RemainingBytes { get; private set; }

    public CancellationToken CancellationToken { get; }

    /// <summary>True once no further component may be planned at any depth.</summary>
    public bool ComponentsExhausted => RemainingComponents <= 0;

    /// <summary>Charges one component slot. Returns false when the tree is full.</summary>
    public bool TryTakeComponent()
    {
        if (RemainingComponents <= 0) return false;
        RemainingComponents--;
        return true;
    }

    /// <summary>
    /// Charges decoded bytes against the shared allowance.
    ///
    /// <para>Charged ONCE, by the component that actually holds the bytes. An embedded message is
    /// charged for its serialized <c>.eml</c>; the parts discovered inside it are charged
    /// separately for their own decoded content. That is intentional double-counting of the same
    /// underlying octets and it is the safe direction — the memory really is materialised twice,
    /// so a budget that charged only once would under-count what the process is holding.</para>
    /// </summary>
    public void ChargeBytes(long bytes)
    {
        if (bytes <= 0) return;
        RemainingBytes = Math.Max(0, RemainingBytes - bytes);
    }

    /// <summary>Nesting depth remaining below the current level.</summary>
    public bool CanDescendTo(int depth) => depth <= MaxNestingDepth;
}
