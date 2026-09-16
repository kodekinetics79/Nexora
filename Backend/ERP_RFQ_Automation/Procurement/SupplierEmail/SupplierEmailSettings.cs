using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Procurement.SupplierEmail;

/// <summary>
/// How a company words the request it emails to suppliers. One row with <see cref="UserId"/> null is
/// the company standard, set by the tenant admin; one row per sales person holds their own default
/// message and signature, which replace the company's for their emails. Blank means "use the
/// Nexora default". The part lines are never stored here: they come from the RFQ line and cannot be
/// reworded (owner decision 2026-09-16).
/// </summary>
public sealed class SupplierEmailSettings
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    /// <summary>Null for the company standard; the user's id for their own defaults.</summary>
    public long? UserId { get; set; }
    public string? Subject { get; set; }
    public string? Greeting { get; set; }
    public string? Opening { get; set; }
    public string? DefaultMessage { get; set; }
    public string? SignOff { get; set; }
    public string UpdatedBy { get; set; } = null!;
    public DateTime UpdatedOn { get; set; }
}

/// <summary>The five wordings, placeholders not yet filled.</summary>
public sealed record SupplierEmailTexts(string Subject, string Greeting, string Opening, string DefaultMessage, string SignOff);

public static class SupplierEmailDefaults
{
    public const string SupplierToken = "[Supplier name]";
    public const string CompanyToken = "[Company name]";
    public const string RfqNumberToken = "[RFQ number]";

    public const int SubjectMax = 200;
    public const int GreetingMax = 200;
    public const int OpeningMax = 1000;
    public const int MessageMax = 2000;
    public const int SignOffMax = 1000;

    public static readonly SupplierEmailTexts Texts = new(
        Subject: $"Request for Quotation {RfqNumberToken} from {CompanyToken}",
        Greeting: $"Dear {SupplierToken},",
        Opening: $"{CompanyToken} invites you to submit a quotation for the following request.",
        DefaultMessage: "Please submit your best pricing and lead times.",
        SignOff: $"Kind regards,\n{CompanyToken}");

    /// <summary>Fills the three placeholders. Matching ignores case, so "[supplier Name]" still works.</summary>
    public static string Fill(string text, string supplierName, string companyName, string rfqNumber) =>
        text.Replace(SupplierToken, supplierName, StringComparison.OrdinalIgnoreCase)
            .Replace(CompanyToken, companyName, StringComparison.OrdinalIgnoreCase)
            .Replace(RfqNumberToken, rfqNumber, StringComparison.OrdinalIgnoreCase);
}

public sealed record CompanySupplierEmailView(
    string? Subject, string? Greeting, string? Opening, string? DefaultMessage, string? SignOff,
    SupplierEmailTexts Defaults, string? UpdatedBy, DateTime? UpdatedOn);

public sealed record CompanySupplierEmailTexts(string DefaultMessage, string SignOff);

public sealed record MySupplierEmailView(string? DefaultMessage, string? SignOff, CompanySupplierEmailTexts Company);

public sealed record SaveCompanySupplierEmailCommand(
    string? Subject, string? Greeting, string? Opening, string? DefaultMessage, string? SignOff);

public sealed record SaveMySupplierEmailCommand(string? DefaultMessage, string? SignOff);

/// <summary>A company mailbox the request can be sent from.</summary>
public sealed record SendFromOption(long MailboxId, string Address, string Label, bool IsDefault);

/// <summary>
/// Where a supplier request goes out from, and where the supplier's reply comes back to.
/// <see cref="Mailboxes"/> is empty when the company has no outgoing mailbox; the request then
/// leaves from the system address under the company's name, with replies to <see cref="ReplyTo"/>.
/// </summary>
public sealed record SendFromView(IReadOnlyList<SendFromOption> Mailboxes, string? ReplyTo, string CompanyName);

public sealed class SupplierEmailValidationException(string message) : Exception(message);

public sealed class SupplierEmailSettingsService(ErpRfqAutomationContext db)
{
    public async Task<CompanySupplierEmailView> GetCompanyAsync(long businessUnitId, CancellationToken ct = default)
    {
        var row = await Company(businessUnitId).AsNoTracking().FirstOrDefaultAsync(ct);
        return ToCompanyView(row);
    }

    public async Task<CompanySupplierEmailView> SaveCompanyAsync(long businessUnitId, SaveCompanySupplierEmailCommand command, string actor, CancellationToken ct = default)
    {
        var d = SupplierEmailDefaults.Texts;
        var subject = Normalise(command.Subject, d.Subject, SupplierEmailDefaults.SubjectMax, "The subject line");
        // Replies are matched to the request by its number, so the subject always keeps it.
        if (subject is not null && !subject.Contains(SupplierEmailDefaults.RfqNumberToken, StringComparison.OrdinalIgnoreCase))
            subject = $"{subject} ({SupplierEmailDefaults.RfqNumberToken})";
        var row = await Company(businessUnitId).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new SupplierEmailSettings { BusinessUnitId = businessUnitId };
            db.Add(row);
        }
        row.Subject = subject;
        row.Greeting = Normalise(command.Greeting, d.Greeting, SupplierEmailDefaults.GreetingMax, "The greeting");
        row.Opening = Normalise(command.Opening, d.Opening, SupplierEmailDefaults.OpeningMax, "The opening sentence");
        row.DefaultMessage = Normalise(command.DefaultMessage, d.DefaultMessage, SupplierEmailDefaults.MessageMax, "The default message");
        row.SignOff = Normalise(command.SignOff, d.SignOff, SupplierEmailDefaults.SignOffMax, "The sign-off and signature");
        row.UpdatedBy = Actor(actor);
        row.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToCompanyView(row);
    }

    public async Task<MySupplierEmailView> GetMineAsync(long businessUnitId, long userId, CancellationToken ct = default)
    {
        var mine = await Mine(businessUnitId, userId).AsNoTracking().FirstOrDefaultAsync(ct);
        var company = await CompanyTextsAsync(businessUnitId, ct);
        return new MySupplierEmailView(mine?.DefaultMessage, mine?.SignOff,
            new CompanySupplierEmailTexts(company.DefaultMessage, company.SignOff));
    }

    public async Task<MySupplierEmailView> SaveMineAsync(long businessUnitId, long userId, SaveMySupplierEmailCommand command, string actor, CancellationToken ct = default)
    {
        var company = await CompanyTextsAsync(businessUnitId, ct);
        var row = await Mine(businessUnitId, userId).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new SupplierEmailSettings { BusinessUnitId = businessUnitId, UserId = userId };
            db.Add(row);
        }
        // Equal to the company's wording is the same as not having one of your own.
        row.DefaultMessage = Normalise(command.DefaultMessage, company.DefaultMessage, SupplierEmailDefaults.MessageMax, "Your default message");
        row.SignOff = Normalise(command.SignOff, company.SignOff, SupplierEmailDefaults.SignOffMax, "Your signature");
        row.UpdatedBy = Actor(actor);
        row.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new MySupplierEmailView(row.DefaultMessage, row.SignOff, new CompanySupplierEmailTexts(company.DefaultMessage, company.SignOff));
    }

    /// <summary>What this user's emails use: their own message and signature over the company's, the company's over Nexora's.</summary>
    public static async Task<SupplierEmailTexts> ResolveAsync(ErpRfqAutomationContext db, long businessUnitId, long? userId, CancellationToken ct = default)
    {
        var rows = await db.Set<SupplierEmailSettings>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && (x.UserId == null || (userId != null && x.UserId == userId)))
            .ToListAsync(ct);
        var company = rows.FirstOrDefault(x => x.UserId == null);
        var mine = userId is null ? null : rows.FirstOrDefault(x => x.UserId == userId);
        var d = SupplierEmailDefaults.Texts;
        return new SupplierEmailTexts(
            company?.Subject ?? d.Subject,
            company?.Greeting ?? d.Greeting,
            company?.Opening ?? d.Opening,
            mine?.DefaultMessage ?? company?.DefaultMessage ?? d.DefaultMessage,
            mine?.SignOff ?? company?.SignOff ?? d.SignOff);
    }

    public Task<SupplierEmailTexts> GetEffectiveAsync(long businessUnitId, long? userId, CancellationToken ct = default)
        => ResolveAsync(db, businessUnitId, userId, ct);

    /// <summary>
    /// The company's outgoing mailboxes, the one whose address the RFQ inbox also reads first,
    /// and the reply-to address: the first active intake inbox.
    /// </summary>
    public static async Task<SendFromView> GetSendFromAsync(ErpRfqAutomationContext db, long businessUnitId, CancellationToken ct = default)
    {
        var rows = await db.EmailConfigurations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.EmailAddress, x.ConfigurationName, x.Protocol })
            .ToListAsync(ct);
        var intake = rows.FirstOrDefault(x => !string.Equals(x.Protocol, "SMTP", StringComparison.OrdinalIgnoreCase))?.EmailAddress;
        var outgoing = rows.Where(x => string.Equals(x.Protocol, "SMTP", StringComparison.OrdinalIgnoreCase)).ToList();
        var preferred = outgoing.FirstOrDefault(x => intake is not null && string.Equals(x.EmailAddress, intake, StringComparison.OrdinalIgnoreCase))
                        ?? outgoing.FirstOrDefault();
        var company = await db.BusinessUnits.AsNoTracking().Where(x => x.Id == businessUnitId)
            .Select(x => x.BusinessUnitName).FirstOrDefaultAsync(ct);
        return new SendFromView(
            outgoing.Select(x => new SendFromOption(x.Id, x.EmailAddress, x.ConfigurationName, preferred?.Id == x.Id)).ToList(),
            intake,
            string.IsNullOrWhiteSpace(company) ? "Your company" : company.Trim());
    }

    private async Task<(string DefaultMessage, string SignOff)> CompanyTextsAsync(long businessUnitId, CancellationToken ct)
    {
        var texts = await ResolveAsync(db, businessUnitId, null, ct);
        return (texts.DefaultMessage, texts.SignOff);
    }

    private IQueryable<SupplierEmailSettings> Company(long businessUnitId) =>
        db.Set<SupplierEmailSettings>().Where(x => x.BusinessUnitId == businessUnitId && x.UserId == null);

    private IQueryable<SupplierEmailSettings> Mine(long businessUnitId, long userId) =>
        db.Set<SupplierEmailSettings>().Where(x => x.BusinessUnitId == businessUnitId && x.UserId == userId);

    private static CompanySupplierEmailView ToCompanyView(SupplierEmailSettings? row) => new(
        row?.Subject, row?.Greeting, row?.Opening, row?.DefaultMessage, row?.SignOff,
        SupplierEmailDefaults.Texts, row?.UpdatedBy, row?.UpdatedOn);

    private static string? Normalise(string? value, string fallback, int max, string what)
    {
        var text = value?.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > max)
            throw new SupplierEmailValidationException($"{what} can be at most {max:N0} characters; it is {text.Length:N0}.");
        return string.Equals(text, fallback.Trim(), StringComparison.Ordinal) ? null : text;
    }

    private static string Actor(string actor) => string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim()[..Math.Min(actor.Trim().Length, 255)];
}
