using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// Makes a spreadsheet import that opens its OWN transaction legal under the configured retrying
/// execution strategy.
///
/// <para><b>The defect this closes.</b> Program.cs registers the DbContext with
/// <c>EnableRetryOnFailure</c>, so EF installs <c>NpgsqlRetryingExecutionStrategy</c>. That strategy
/// refuses any transaction opened outside its delegate:
/// <c>InvalidOperationException: The configured execution strategy 'NpgsqlRetryingExecutionStrategy'
/// does not support user-initiated transactions.</c> Every bulk importer — leads, quotations,
/// products, product categories, product sub-categories, suppliers and RFQs — opened one directly,
/// so all seven "Upload template" buttons failed 100% of the time against PostgreSQL while passing
/// on the SQLite test lane, which configures no retry strategy at all.</para>
///
/// <para><b>Why the whole import is the unit.</b> The importers parse the workbook, stage entities
/// and then commit; the transaction is opened partway through. The strategy therefore has to own
/// the entire call, not just the transaction — otherwise a retry would re-apply the failed
/// attempt's staged entities. <see cref="ExecuteAsync{T}"/> clears the change tracker and rewinds
/// the upload stream on every attempt so the retry starts from the same place the first attempt
/// did.</para>
/// </summary>
internal static class RetriableUploadTransaction
{
    /// <summary>
    /// Runs <paramref name="import"/> as one retriable unit. A caller that already owns a
    /// transaction already owns the unit, so the import is invoked directly in that case — nesting
    /// a strategy inside a live transaction is what the strategy exists to prevent.
    /// </summary>
    /// <param name="context">The context whose provider decides the strategy.</param>
    /// <param name="fileStream">The uploaded workbook. Rewound before every attempt when seekable;
    /// a retry that read from a consumed stream would report an empty file rather than retrying.</param>
    /// <param name="import">The importer body, which keeps its own transaction and commit.</param>
    public static Task<T> ExecuteAsync<T>(
        ErpRfqAutomationContext context, Stream? fileStream, Func<Task<T>> import)
    {
        if (!context.Database.IsRelational() || context.Database.CurrentTransaction is not null)
            return import();

        var strategy = context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() =>
        {
            context.ChangeTracker.Clear();
            if (fileStream is { CanSeek: true })
                fileStream.Position = 0;
            return import();
        });
    }
}
