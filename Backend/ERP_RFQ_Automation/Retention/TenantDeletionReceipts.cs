using System.Net;
using System.Text;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Retention;

/// <summary>One irreversible deletion, described for the people who did not press the button.</summary>
/// <param name="Headline">What happened, in one line: "47 stored documents deleted".</param>
/// <param name="Lines">The figures, label and value, in the order they should be read.</param>
/// <param name="Reason">The reason the administrator wrote, verbatim.</param>
public sealed record TenantDeletionReceipt(
    string Headline,
    IReadOnlyList<KeyValuePair<string, string>> Lines,
    string Reason);

/// <summary>
/// Emails a receipt for a real deletion to every owner-rank administrator of the workspace.
///
/// <para><b>Why a receipt and not a gate.</b> The owner asked for a second confirmation "either by
/// sending email or a popup". An emailed one-time code would make deletion impossible for every
/// tenant whose outbound mail is not configured — and outbound mail on this platform needs both an
/// active SMTP mailbox row and a provider setting that can disagree (issue #54). A second typed
/// confirmation therefore stands as the gate, because it works everywhere, and the email is the
/// thing mail is actually good at: telling the OTHER administrators, promptly and in writing, that
/// it happened, who did it and why. A mistaken click is then visible to the whole owner tier the
/// same minute, rather than discovered when a file is missing.</para>
///
/// <para><b>Never throws.</b> The deletion has already been committed and audited by the time this
/// runs; a mail failure is logged and reported as "nobody was notified", never allowed to fail the
/// request that carried a completed, irreversible action.</para>
/// </summary>
public interface ITenantDeletionReceipts
{
    /// <summary>Returns how many administrators the receipt was sent to. Zero when there was
    /// nobody to send it to, or when sending failed.</summary>
    Task<int> SendAsync(long tenantId, long actorUserId, TenantDeletionReceipt receipt, CancellationToken ct);
}

public sealed class TenantDeletionReceiptMailer(
    ErpRfqAutomationContext db,
    IRoleGate roles,
    IEmailSender sender,
    ILogger<TenantDeletionReceiptMailer> log) : ITenantDeletionReceipts
{
    private sealed record Recipient(long Id, string Email, string Name, long RoleId);

    public async Task<int> SendAsync(long tenantId, long actorUserId, TenantDeletionReceipt receipt, CancellationToken ct)
    {
        try
        {
            var workspace = await db.BusinessUnits.AsNoTracking()
                .Where(x => x.Id == tenantId)
                .Select(x => x.BusinessUnitName)
                .SingleOrDefaultAsync(ct) ?? $"workspace {tenantId}";

            var candidates = await db.Users.AsNoTracking()
                .Where(u => u.Buid == tenantId && u.IsActive != false && u.RoleId != null
                    && u.Email != null && u.Email != "")
                .Select(u => new Recipient(u.Id, u.Email, (u.FirstName + " " + u.LastName).Trim(), u.RoleId!.Value))
                .ToListAsync(ct);

            var ownerRoles = new HashSet<long>();
            foreach (var roleId in candidates.Select(x => x.RoleId).Distinct())
                if (await roles.IsSuperAdminAsync(roleId, tenantId))
                    ownerRoles.Add(roleId);

            var recipients = candidates.Where(x => ownerRoles.Contains(x.RoleId)).ToList();
            if (recipients.Count == 0)
            {
                log.LogWarning("Deletion receipt for tenant {Tenant} had no owner-rank recipient.", tenantId);
                return 0;
            }

            var actor = candidates.FirstOrDefault(x => x.Id == actorUserId)?.Name
                ?? await db.Users.AsNoTracking().Where(u => u.Id == actorUserId)
                    .Select(u => (u.FirstName + " " + u.LastName).Trim()).SingleOrDefaultAsync(ct)
                ?? $"user {actorUserId}";

            var message = Compose(workspace, actor, receipt, tenantId);
            foreach (var recipient in recipients)
                message.AddTo(recipient.Email, recipient.Name.Length > 0 ? recipient.Name : null);

            await sender.SendAsync(message, ct);
            return recipients.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The deletion is done and audited. A receipt that could not be sent is reported as
            // zero recipients, so the screen never claims a mail that did not go out.
            log.LogWarning(exception, "Deletion receipt for tenant {Tenant} was not sent.", tenantId);
            return 0;
        }
    }

    private static EmailMessage Compose(string workspace, string actor, TenantDeletionReceipt receipt, long tenantId)
    {
        var when = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var text = new StringBuilder()
            .AppendLine($"{receipt.Headline} — {workspace}")
            .AppendLine()
            .AppendLine($"Done by: {actor}")
            .AppendLine($"When: {when}")
            .AppendLine($"Reason given: {receipt.Reason}");
        foreach (var line in receipt.Lines)
            text.AppendLine($"{line.Key}: {line.Value}");
        text.AppendLine()
            .AppendLine("This is a receipt, not a request. The deletion has already happened and cannot be undone.")
            .AppendLine("You are receiving it because you are an administrator of this workspace. If you did not expect it, speak to the person named above.")
            .AppendLine("The full record — what was deleted, what was kept and why — is in Setup → Storage & Retention and in your audit trail.");

        var html = new StringBuilder()
            .Append("<p><strong>").Append(WebUtility.HtmlEncode(receipt.Headline)).Append("</strong> — ")
            .Append(WebUtility.HtmlEncode(workspace)).Append("</p><table>")
            .Append(Row("Done by", actor)).Append(Row("When", when)).Append(Row("Reason given", receipt.Reason));
        foreach (var line in receipt.Lines)
            html.Append(Row(line.Key, line.Value));
        html.Append("</table>")
            .Append("<p>This is a receipt, not a request. The deletion has already happened and cannot be undone.</p>")
            .Append("<p>You are receiving it because you are an administrator of this workspace. If you did not expect it, speak to the person named above.</p>")
            .Append("<p>The full record — what was deleted, what was kept and why — is in Setup → Storage &amp; Retention and in your audit trail.</p>");

        return new EmailMessage
        {
            Subject = $"[Nexora] {receipt.Headline} — {workspace}",
            TextBody = text.ToString(),
            HtmlBody = html.ToString(),
            TenantId = tenantId.ToString()
        };
    }

    private static string Row(string label, string value) =>
        $"<tr><td style=\"padding:2px 12px 2px 0\"><strong>{WebUtility.HtmlEncode(label)}</strong></td><td>{WebUtility.HtmlEncode(value)}</td></tr>";
}
