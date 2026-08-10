using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.MasterData;

/// <summary>
/// The actor a master-data audit row is attributed to. Every field is server-derived: the id and
/// role come from the validated token, never from a request body — same rule, and the same
/// reasoning, as <see cref="ActorContext"/>.
/// </summary>
public readonly record struct MasterDataAuditActor(
    long? UserId,
    long? RoleId,
    string Name,
    string? CorrelationId,
    string Source)
{
    /// <summary>The attribution used when nothing else is known. Never silently becomes a person.</summary>
    public static MasterDataAuditActor System { get; } =
        new(null, null, "system", null, MasterDataChangeSources.System);

    public static MasterDataAuditActor FromPrincipal(HttpContext httpContext)
    {
        var actor = ActorContext.From(httpContext.User, httpContext.TraceIdentifier);
        return new MasterDataAuditActor(
            actor.UserId, actor.RoleId, actor.Stamp, actor.CorrelationId, MasterDataChangeSources.Api);
    }
}

/// <summary>
/// Ambient, request-scoped actor for the master-data audit.
///
/// <para><b>Why ambient rather than injected.</b> The capture point is
/// <c>ErpRfqAutomationContext.SaveChanges</c> — the one place no write path can go around. That
/// override is on the DbContext, which is constructed by EF and has no reach into the request's
/// DI scope, and widening its constructor would change how every existing construction site
/// (including the test harnesses) resolves. An <see cref="AsyncLocal{T}"/> pushed once per request
/// by <see cref="MasterDataAuditActorMiddleware"/> flows into the save without touching either.
/// <c>TenantScopeAccessor</c> already uses exactly this mechanism for the tenant.</para>
///
/// <para>The scope is a stack, so <see cref="PushSource"/> can mark a bulk import without losing
/// the human who started it.</para>
/// </summary>
public static class MasterDataAuditScope
{
    private static readonly AsyncLocal<MasterDataAuditActor?> CurrentActor = new();

    /// <summary>The ambient actor, or null when there is none — a background sweep, a migration
    /// harness, or a save that happened outside the request pipeline. Callers must NOT invent an
    /// identity for that case; <see cref="MasterDataAuditInterceptor"/> falls back to the row's own
    /// server-set CreatedBy/ModifiedBy stamp and then to <see cref="MasterDataAuditActor.System"/>.</summary>
    public static MasterDataAuditActor? Current => CurrentActor.Value;

    public static IDisposable Push(MasterDataAuditActor actor)
    {
        var previous = CurrentActor.Value;
        CurrentActor.Value = actor;
        return new Scope(() => CurrentActor.Value = previous);
    }

    /// <summary>
    /// Re-labels the ambient actor's <see cref="MasterDataAuditActor.Source"/> without changing who
    /// it is. Used by the spreadsheet uploaders so a bulk edit is separable from a screen edit in
    /// one query while still naming the person who ran it.
    /// </summary>
    public static IDisposable PushSource(string source)
    {
        var actor = CurrentActor.Value ?? MasterDataAuditActor.System;
        return Push(actor with { Source = source });
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// Publishes the authenticated caller as the ambient master-data audit actor for the duration of
/// the request. Registered AFTER <c>UseAuthentication</c> — before it, <c>HttpContext.User</c>
/// carries no claims and every audit row would be attributed to "system".
/// </summary>
public sealed class MasterDataAuditActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        using var scope = MasterDataAuditScope.Push(MasterDataAuditActor.FromPrincipal(context));
        await next(context);
    }
}

public static class MasterDataAuditActorMiddlewareExtensions
{
    public static IApplicationBuilder UseMasterDataAuditActor(this IApplicationBuilder app)
        => app.UseMiddleware<MasterDataAuditActorMiddleware>();
}
