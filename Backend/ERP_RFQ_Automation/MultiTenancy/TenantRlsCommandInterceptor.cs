using System.Collections.Concurrent;
using System.Data.Common;
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

    private readonly ITenantContext _tenantContext;
    private readonly ConcurrentDictionary<Guid, DbTransaction> _ownedTransactions = new();

    public TenantRlsCommandInterceptor(ITenantContext tenantContext)
        => _tenantContext = tenantContext;

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
        if (_tenantContext.BusinessUnitId is not { } businessUnitId)
            return;

        if (command.Transaction is null)
        {
            var transaction = command.Connection!.BeginTransaction();
            command.Transaction = transaction;
            _ownedTransactions[commandId] = transaction;
        }

        try
        {
            using var setup = CreateSetupCommand(command, businessUnitId);
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
        if (_tenantContext.BusinessUnitId is not { } businessUnitId)
            return;

        if (command.Transaction is null)
        {
            var transaction = await command.Connection!.BeginTransactionAsync(cancellationToken);
            command.Transaction = transaction;
            _ownedTransactions[commandId] = transaction;
        }

        try
        {
            await using var setup = CreateSetupCommand(command, businessUnitId);
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            await RollbackAsync(commandId, CancellationToken.None);
            throw;
        }
    }

    private static DbCommand CreateSetupCommand(DbCommand command, long businessUnitId)
    {
        var setup = command.Connection!.CreateCommand();
        setup.Transaction = command.Transaction;
        setup.CommandText = $"SET LOCAL ROLE {TenantRole}; SELECT set_config('nexora.business_unit_id', @tenant_id, true);";
        var parameter = setup.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = businessUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        setup.Parameters.Add(parameter);
        return setup;
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
