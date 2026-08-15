namespace ERP_RFQ_Automation.MultiTenancy;

/// <summary>
/// Marks a narrow region of code as PLATFORM-PLANE work, so the commands it issues execute as
/// <c>nexora_pipeline_app</c> even while a tenant scope is pushed.
///
/// <para><b>Why this exists.</b> Two planes share one schema and one connection. The tenant plane
/// (<c>public</c>) is reached as <c>nexora_tenant_app</c> under row-level security; the platform
/// plane (<c>platform</c> — tenants, rate cards, usage events, ratings, minute aggregates) is
/// reached as <c>nexora_pipeline_app</c>, and no grant on it is held by the tenant role. A handful
/// of code paths legitimately do BOTH in one transaction. Usage metering inside the extraction
/// persist transaction is the archetype: the business write is tenant-plane and must stay bound by
/// RLS, the meter is platform-plane, and the two must commit or roll back together or the platform
/// bills for work it did not keep.</para>
///
/// <para><b>Why an ambient flag rather than a parameter.</b> The role is chosen by
/// <see cref="TenantRlsCommandInterceptor"/> at the command boundary, below every service that
/// could carry a parameter, and it reads the tenant from an <see cref="ITenantContext"/> that
/// captured its value in its CONSTRUCTOR — so popping the tenant scope around the call does not
/// change the role. The same ambient-scope shape is already used by
/// <c>MasterDataAuditScope</c>.</para>
///
/// <para><b>What it does NOT do.</b> It does not disable EF's global query filters, does not clear
/// <c>nexora.business_unit_id</c>, and does not widen any role's grants. It selects an already
/// existing, already privileged execution role for the statements inside the block, and the block
/// ends with the <c>using</c>. Everything after it is back on the tenant role under RLS.</para>
///
/// <para><b>It is half of a pair.</b> This flag governs the interceptor only. A connection can also
/// have been put on a role by a direct <c>SET LOCAL ROLE</c> — <c>ExtractionQueue</c> does exactly
/// that, with no interceptor involved, and SET LOCAL persists to the end of the transaction. A use
/// site must therefore ALSO issue its own <c>SET LOCAL ROLE</c> for the platform role and restore
/// the previous one afterwards; this flag is what stops the interceptor from undoing that before
/// every command in between.</para>
///
/// <para><b>How to use it safely.</b> Wrap the SMALLEST region that touches the platform plane, and
/// never a region that reads or writes tenant rows — <c>nexora_pipeline_app</c> is BYPASSRLS, so a
/// tenant-plane statement inside the block is unpoliced. Each use site must say, in a comment,
/// which platform tables it needs and why the work cannot be deferred out of the transaction.</para>
/// </summary>
public static class PlatformPlaneExecution
{
    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>True while the calling flow is inside <see cref="Enter"/>.</summary>
    public static bool IsActive => Current.Value;

    /// <summary>
    /// Enters the platform plane for the calling asynchronous flow. Reentrant: nesting is
    /// harmless, and disposal restores the previous state rather than unconditionally clearing it.
    /// </summary>
    public static IDisposable Enter()
    {
        var previous = Current.Value;
        Current.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Current.Value = previous;
        }
    }
}
