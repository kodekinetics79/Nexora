using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP_RFQ_Automation.MultiTenancy;

/// <summary>
/// Applies PostgreSQL tenant scope at the command boundary. Commands already inside a
/// transaction receive transaction-local role/GUC setup. Standalone commands get a short
/// transaction that remains open until their result has been consumed. This is compatible
/// with transaction-pooled connections and does not interfere with service-owned transactions.
/// </summary>
public sealed class TenantRlsCommandInterceptor : DbCommandInterceptor
{
    public const string TenantRole = "nexora_tenant_app";
    public const string IdentityRole = "nexora_identity_app";
    public const string PipelineRole = "nexora_pipeline_app";

    private readonly ITenantContext _tenantContext;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly byte[]? _auditActorSecret;
    private readonly ConcurrentDictionary<Guid, DbTransaction> _ownedTransactions = new();

    public TenantRlsCommandInterceptor(ITenantContext tenantContext)
        => _tenantContext = tenantContext;

    public TenantRlsCommandInterceptor(
        ITenantContext tenantContext, IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _tenantContext = tenantContext;
        _httpContextAccessor = httpContextAccessor;
        var secret = configuration["CommercialFinance:AuditActorSecret"];
        if (!string.IsNullOrWhiteSpace(secret)) _auditActorSecret = Encoding.UTF8.GetBytes(secret);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Prepare(command, eventData.CommandId);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Prepare(command, eventData.CommandId);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Prepare(command, eventData.CommandId);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(command, eventData.CommandId, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(command, eventData.CommandId, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(command, eventData.CommandId, cancellationToken);
        return result;
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Complete(eventData.CommandId);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Complete(eventData.CommandId);
        return result;
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        await CompleteAsync(eventData.CommandId, cancellationToken);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await CompleteAsync(eventData.CommandId, cancellationToken);
        return result;
    }

    public override InterceptionResult DataReaderDisposing(
        DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        Complete(eventData.CommandId);
        return result;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Rollback(eventData.CommandId);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        => RollbackAsync(eventData.CommandId, cancellationToken);

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
        => Rollback(eventData.CommandId);

    public override Task CommandCanceledAsync(
        DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
        => RollbackAsync(eventData.CommandId, cancellationToken);

    private void Prepare(DbCommand command, Guid commandId)
    {
        var executionScope = ResolveExecutionScope();
        if (executionScope is null)
            return;

        if (command.Transaction is null)
        {
            var transaction = command.Connection!.BeginTransaction();
            command.Transaction = transaction;
            _ownedTransactions[commandId] = transaction;
        }

        try
        {
            using var setup = CreateSetupCommand(command, executionScope.Value);
            setup.ExecuteNonQuery();
        }
        catch
        {
            Rollback(commandId);
            throw;
        }
    }

    private async Task PrepareAsync(DbCommand command, Guid commandId, CancellationToken cancellationToken)
    {
        var executionScope = ResolveExecutionScope();
        if (executionScope is null)
            return;

        if (command.Transaction is null)
        {
            var transaction = await command.Connection!.BeginTransactionAsync(cancellationToken);
            command.Transaction = transaction;
            _ownedTransactions[commandId] = transaction;
        }

        try
        {
            await using var setup = CreateSetupCommand(command, executionScope.Value);
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            await RollbackAsync(commandId, CancellationToken.None);
            throw;
        }
    }

    private ExecutionScope? ResolveExecutionScope()
    {
        var businessUnitId = _tenantContext.BusinessUnitId;
        var role = ResolveDatabaseRole(businessUnitId, _httpContextAccessor);
        return role is null
            ? null
            : new ExecutionScope(
                role,
                businessUnitId,
                businessUnitId is { } tenantId ? SignedActor(tenantId) : null);
    }

    /// <summary>
    /// Chooses the PostgreSQL role a command executes under. Returning null means "issue no
    /// SET LOCAL ROLE", which leaves the command on the connection's own login role.
    ///
    /// That matters more than it looks: ResolveDirectMigrationConnection (Program.cs) reuses the
    /// runtime username for migrations, so the runtime login role is also the role that OWNS the
    /// tables — and a table owner is exempt from its own row-level-security policies unless the
    /// table is declared FORCE. Verified against postgres:16 using the exact role topology
    /// ValidateRuntimeDatabaseRoleAsync enforces (owner NOINHERIT, NOSUPERUSER, NOBYPASSRLS):
    /// a SELECT on an ENABLE-only tenant table returned EVERY tenant's rows. So the old
    /// "anonymous / unrecognised route" fallthrough was the one code path where BOTH isolation
    /// layers were off at once — the EF filter is a no-op with a null tenant, and RLS did not
    /// bind the owner.
    ///
    /// It now falls through to <see cref="TenantRole"/> WITHOUT setting nexora.business_unit_id.
    /// nexora_tenant_app is NOBYPASSRLS, so every policy evaluates
    /// `"BusinessUnitID" = NULLIF(current_setting(...), '')::bigint` against NULL: the same
    /// verification showed 0 rows returned and INSERT rejected with "new row violates row-level
    /// security policy". A request that has no tenant gets no tenant data, which is the correct
    /// answer rather than a degradation.
    ///
    /// Deliberately NOT <see cref="PipelineRole"/>: that role is created BYPASSRLS
    /// (20260728134117_ConfigureDatabaseExecutionRoles), and the same verification showed it
    /// returning every tenant's rows both with and without FORCE. Routing the null case there
    /// would relabel the hole, not close it.
    ///
    /// Deliberately NOT a throw: this is reached on an in-flight HTTP request, and the failure
    /// mode of a wrong guess is a dead startup or a dead health probe rather than a leak. The
    /// role above is already fail-closed at the database, so throwing buys no isolation.
    ///
    /// The null return is kept for the parameterless-accessor constructor only. That overload is
    /// never used by Program.cs (the DI container resolves the three-argument constructor); it
    /// exists for background-worker test harnesses that sweep across tenants before pushing a
    /// scope, and forcing those onto an unGUCed tenant role would make them read nothing.
    ///
    /// ORDER MATTERS. The two login endpoints are resolved BEFORE the tenant check, because a
    /// login request that arrives carrying a live tenant JWT (the frontend attaches the bearer
    /// token to every request) would otherwise run as nexora_tenant_app, which has no privilege
    /// on "LoginAttempts" — see the inline note at the branch.
    /// </summary>
    internal static string? ResolveDatabaseRole(
        long? businessUnitId, IHttpContextAccessor? httpContextAccessor)
    {
        if (httpContextAccessor is null)
            return businessUnitId.HasValue ? TenantRole : null;

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return businessUnitId.HasValue ? TenantRole : PipelineRole;

        // THE TWO LOGIN PATHS ARE RESOLVED FIRST, deliberately ABOVE the tenant check.
        //
        // COMPOSITION DEFECT this closes: axiosInstance attaches the bearer token to EVERY
        // request, so a user who already holds a live tenant JWT and re-posts /api/Auth/Login
        // arrives here with businessUnitId set. Under the old ordering that request ran as
        // nexora_tenant_app — and 20260804181701_LoginAttemptThrottle REVOKEs all privileges
        // on "LoginAttempts" from that role. LoginAttemptThrottle fails OPEN on infrastructure
        // errors (a broken counter table must not lock every user out of the product), so the
        // 42501 insufficient_privilege was swallowed and the progressive lockout silently did
        // not exist on the default, token-bearing path: no counter, no lockout, no forensic
        // row, at the full request rate. /api/platform/auth/login had the identical shape.
        //
        // Neither branch grants anything an attacker could not already reach by simply dropping
        // the token — both login paths already resolve to these roles when businessUnitId is
        // null — so this adds no capability. It only stops a tenant token from DOWNGRADING the
        // role out of the privileges its own throttle table requires.
        //
        // StartsWithSegments rather than an exact string compare: "/api/Auth/Login/" routes to
        // the same endpoint but was a different string and fell through to the tenant role. It
        // is segment-aware, so a hypothetical "/api/Auth/LoginHistory" is NOT matched.
        if (httpContext.Request.Path.StartsWithSegments("/api/platform/auth/login"))
            return PipelineRole;
        if (httpContext.Request.Path.StartsWithSegments("/api/Auth/Login"))
            return IdentityRole;

        // Founding-administrator activation is a PRE-TENANT identity operation, and it is
        // resolved here for exactly the same two reasons as the login paths above.
        //
        // It cannot run as nexora_tenant_app, the role every other anonymous request falls
        // through to. That role is RLS-constrained and this request carries no tenant scope —
        // there is nobody to derive one from until the invitation itself says which account is
        // being activated — so nexora.business_unit_id is never set and the Users row the
        // redemption must update is invisible. The endpoint would return "invalid link" for a
        // perfectly good link, and no test on the SQLite suite can see it because SQLite has
        // neither roles nor row-level security.
        //
        // It is hoisted ABOVE the tenant check for the login branches' reason: the frontend
        // attaches whatever bearer token it holds to every request, so an operator or an
        // already-signed-in user opening an activation link would otherwise arrive with a
        // business unit set and downgrade the role out of the privileges the flow requires.
        //
        // nexora_identity_app rather than the bypass-everything pipeline role: activation needs
        // to read one invitation and write one password, and 20260807002500 grants it precisely
        // that — column-scoped UPDATE on Users that cannot touch RoleId, Buid or Email.
        if (httpContext.Request.Path.StartsWithSegments("/api/tenant-activation"))
            return IdentityRole;

        // Self-service password reset is the same kind of operation as activation — a pre-tenant
        // identity write on an anonymous request — and it is hoisted here for the same two
        // reasons, plus one of its own.
        //
        // It cannot run as nexora_tenant_app. That role is RLS-constrained and this request
        // carries no tenant scope, so nexora.business_unit_id is never set: the Users row the
        // request path must FIND BY EMAIL, and the one the completion path must update, are both
        // invisible to it. The visible symptom would be the flow's worst possible failure mode —
        // every address answered as "no such account", silently, forever, because the endpoint is
        // built never to say whether it found one. No SQLite test can see it either, since SQLite
        // has neither roles nor row-level security.
        //
        // It is hoisted ABOVE the tenant check for the login branches' reason: the frontend
        // attaches whatever bearer token it holds to every request, so a signed-in user helping a
        // colleague, or anyone with a stale token in localStorage, would otherwise arrive with a
        // business unit set and downgrade the role out of the privileges the flow requires.
        //
        // nexora_identity_app rather than the bypass-everything pipeline role: reset needs to read
        // one user, write one password hash and touch one table, and the migration that creates
        // PasswordResetTokens grants it precisely that — reusing the same column-scoped UPDATE on
        // Users that cannot touch RoleId, Buid or Email.
        if (httpContext.Request.Path.StartsWithSegments("/api/password-reset"))
            return IdentityRole;

        // NOT hoisted above this line: the REST of /api/platform. nexora_pipeline_app is
        // BYPASSRLS, and an impersonation token (PlatformAuthService) is a TENANT token that
        // carries businessUnitId and no platform scope — so hoisting the whole platform prefix
        // would let a tenant-scoped request execute under the bypass role the moment a platform
        // authorization check regressed. A tenant scope therefore keeps the fail-closed tenant
        // role everywhere except the login endpoints, whose privileges it demonstrably needs.
        if (businessUnitId.HasValue)
            return TenantRole;

        if (httpContext.Request.Path.StartsWithSegments("/api/platform"))
            return PipelineRole;

        // Fail closed: least-privileged role, no tenant GUC, therefore no tenant rows.
        return TenantRole;
    }

    private static DbCommand CreateSetupCommand(DbCommand command, ExecutionScope executionScope)
    {
        var setup = command.Connection!.CreateCommand();
        setup.Transaction = command.Transaction;
        setup.CommandText = $"SET LOCAL ROLE {executionScope.Role};";
        if (executionScope.BusinessUnitId is not { } businessUnitId)
            return setup;

        var signedActor = executionScope.SignedActor;
        setup.CommandText += " SELECT set_config('nexora.business_unit_id', @tenant_id, true), set_config('nexora.actor_id', @actor_id, true), set_config('nexora.actor_signature', @actor_signature, true), set_config('nexora.gl_issued_at', @gl_issued_at, true), set_config('nexora.gl_expires_at', @gl_expires_at, true), set_config('nexora.gl_nonce', @gl_nonce, true), set_config('nexora.gl_signature', @gl_signature, true);";
        var parameter = setup.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = businessUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        setup.Parameters.Add(parameter);
        var actor = setup.CreateParameter();
        actor.ParameterName = "actor_id";
        actor.Value = signedActor?.Actor ?? string.Empty;
        setup.Parameters.Add(actor);
        var signature = setup.CreateParameter();
        signature.ParameterName = "actor_signature";
        signature.Value = signedActor?.Signature ?? string.Empty;
        setup.Parameters.Add(signature);
        AddParameter(setup, "gl_issued_at", signedActor?.IssuedAtUnix.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        AddParameter(setup, "gl_expires_at", signedActor?.ExpiresAtUnix.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        AddParameter(setup, "gl_nonce", signedActor?.Nonce ?? string.Empty);
        AddParameter(setup, "gl_signature", signedActor?.EnvelopeSignature ?? string.Empty);
        return setup;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record SignedActorEnvelope(string Actor, string Signature, long IssuedAtUnix,
        long ExpiresAtUnix, string Nonce, string EnvelopeSignature);

    private readonly record struct ExecutionScope(
        string Role, long? BusinessUnitId, SignedActorEnvelope? SignedActor);

    private SignedActorEnvelope? SignedActor(long businessUnitId)
    {
        if (_auditActorSecret is null || _auditActorSecret.Length < 32) return null;
        var user = _httpContextAccessor?.HttpContext?.User;
        var actor = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.FindFirst("sub")?.Value
            ?? user?.FindFirst(ClaimTypes.Email)?.Value
            ?? user?.FindFirst("email")?.Value
            ?? user?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) return null;
        actor = actor.Trim();
        var canonical = $"{businessUnitId}\n{actor}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            _auditActorSecret, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = issuedAt + 30;
        var nonce = Guid.NewGuid().ToString("D");
        var envelope = $"{businessUnitId}\n{actor}\n{issuedAt}\n{expiresAt}\n{nonce}";
        var envelopeSignature = Convert.ToHexString(HMACSHA256.HashData(
            _auditActorSecret, Encoding.UTF8.GetBytes(envelope))).ToLowerInvariant();
        return new(actor, signature, issuedAt, expiresAt, nonce, envelopeSignature);
    }

    private void Complete(Guid commandId)
    {
        if (!_ownedTransactions.TryRemove(commandId, out var transaction))
            return;
        try { transaction.Commit(); }
        finally { transaction.Dispose(); }
    }

    private async Task CompleteAsync(Guid commandId, CancellationToken cancellationToken)
    {
        if (!_ownedTransactions.TryRemove(commandId, out var transaction))
            return;
        try { await transaction.CommitAsync(cancellationToken); }
        finally { await transaction.DisposeAsync(); }
    }

    private void Rollback(Guid commandId)
    {
        if (!_ownedTransactions.TryRemove(commandId, out var transaction))
            return;
        try { transaction.Rollback(); }
        finally { transaction.Dispose(); }
    }

    private async Task RollbackAsync(Guid commandId, CancellationToken cancellationToken)
    {
        if (!_ownedTransactions.TryRemove(commandId, out var transaction))
            return;
        try { await transaction.RollbackAsync(cancellationToken); }
        finally { await transaction.DisposeAsync(); }
    }
}
