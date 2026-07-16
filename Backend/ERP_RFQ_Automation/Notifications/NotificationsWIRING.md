# Notifications + Transactional Email — Wiring Guide

Self-contained module under `Backend/ERP_RFQ_Automation/Notifications/`. It adds real
transactional email to the RFQ → quote → order flow. Nothing outside the
`Notifications/` folder was modified, so wiring it in is a **one-line** change plus a
config block.

---

## 1. Program.cs — add ONE line

Add the `using` and call `AddNotifications(...)` anywhere in the service-registration
section (e.g. right after the other `builder.Services.Add...` calls, near line 139
where `IEmailService` is registered).

```csharp
using ERP_RFQ_Automation.Notifications;   // top of file, with the other usings

// ...inside the service registration block:
builder.Services.AddNotifications(builder.Configuration);
```

That single call registers:

- `NotificationsOptions` (bound + validated from the `Notifications` config section)
- `IEmailTemplateRenderer` → `EmailTemplateRenderer` (singleton)
- `INotificationService` → `NotificationService` (scoped)
- `IEmailSender` → the provider chosen by `Notifications:Provider`
  (`smtp` | `sendgrid` | `console`; **defaults to `console`** when unset/unknown)
- A named `HttpClient` (`"NotificationsSendGrid"`) via `IHttpClientFactory` for the
  SendGrid provider.

No other Program.cs edits are required. No middleware, no endpoints.

---

## 2. appsettings — add the `Notifications` section

Add this block to `appsettings.json` (safe defaults for dev/pilot: the **console**
provider only logs emails, it does not send). Provide real secrets via
`appsettings.Development.json`, user-secrets, or environment variables — do **not**
commit real credentials.

```jsonc
"Notifications": {
  "Provider": "console",                    // "console" | "smtp" | "sendgrid"
  "FromAddress": "no-reply@your-domain.com",
  "FromName": "Nexora",
  "ReplyToAddress": "",                     // optional
  "AppBaseUrl": "https://app.your-domain.com",  // used to build CTA links in emails

  "Smtp": {
    "Host": "",                             // e.g. smtp.sendgrid.net / smtp.office365.com
    "Port": 587,
    "Username": "",
    "Password": "",
    "EnableSsl": true,
    "TimeoutMs": 30000
  },

  "SendGrid": {
    "ApiKey": "",                           // SG.xxxxx
    "ApiBaseUrl": "https://api.sendgrid.com"   // change for EU residency if needed
  }
}
```

### Environment-variable equivalents (double underscore)

```
Notifications__Provider=smtp
Notifications__FromAddress=no-reply@your-domain.com
Notifications__FromName=Nexora
Notifications__AppBaseUrl=https://app.your-domain.com
Notifications__Smtp__Host=smtp.your-host.com
Notifications__Smtp__Port=587
Notifications__Smtp__Username=apikey
Notifications__Smtp__Password=__SECRET__
Notifications__SendGrid__ApiKey=__SECRET__
```

`AddNotifications` logs a startup warning if the chosen provider is missing required
config (e.g. `Provider=smtp` with no `Smtp:Host`, or `Provider=sendgrid` with no
`SendGrid:ApiKey`). These warnings are non-fatal — the app still starts.

---

## 3. Using the service (for the engineers wiring the business flow)

Inject `INotificationService` into any controller/service. Every method is
**resilient**: a send failure is caught and logged and never throws, so a
notification problem can't break the business transaction. Each returns `bool`
(`true` = dispatched, `false` = failed-and-swallowed).

```csharp
public class QuoteWorkflow
{
    private readonly INotificationService _notifications;
    public QuoteWorkflow(INotificationService notifications) => _notifications = notifications;

    public async Task DeliverQuoteAsync(/* ... */)
    {
        await _notifications.SendQuoteToBuyerAsync(new QuoteToBuyerNotification
        {
            ToEmail        = buyer.Email,
            ToName         = buyer.Name,
            TenantId       = tenantId,
            BusinessUnitId = buId,
            BuyerName      = buyer.Name,
            SupplierCompany= "Nexora",
            QuoteNumber    = quote.QuoteNo,
            RfqNumber      = quote.RfqNo,
            TotalAmount    = "$12,450.00",
            ValidUntil     = "2026-08-01",
            CtaPath        = $"quotes/{quote.Id}",              // relative → combined with AppBaseUrl
            Attachments    = { new EmailAttachment("quote.pdf", pdfBytes, "application/pdf") }
        });
    }
}
```

Available intent methods on `INotificationService`:

| Method                          | Template            | Purpose                                 |
|---------------------------------|---------------------|-----------------------------------------|
| `NotifyLeadNeedsReviewAsync`    | `lead-needs-review` | Internal: a document needs human review |
| `SendRfqToSupplierAsync`        | `rfq-to-supplier`   | RFQ invitation to a supplier            |
| `SendQuoteToBuyerAsync`         | `quote-to-buyer`    | Deliver a quotation to a buyer          |
| `SendOrderConfirmationAsync`    | `order-confirmation`| Order confirmation to a customer        |

Each request type carries `ToEmail`, `ToName`, `TenantId`, `BusinessUnitId`,
`CtaPath` (relative path or absolute URL), and optional `Attachments`.

---

## 4. Deliverability — REQUIRED owner action before real sending

Transactional email from a custom `FromAddress` will land in spam (or be rejected)
unless the **sending domain's DNS** is configured. The domain owner must set:

- **SPF** — a TXT record authorizing the provider's servers to send for the domain
  (e.g. SendGrid: `v=spf1 include:sendgrid.net ~all`; for SMTP relays, include that
  host's SPF).
- **DKIM** — publish the provider's DKIM CNAME/TXT records so outbound mail is
  cryptographically signed. SendGrid generates these under *Sender Authentication*;
  most SMTP hosts provide DKIM selectors too.
- **DMARC** — a `_dmarc` TXT record (start with `p=none` to monitor, then tighten to
  `quarantine`/`reject`) tying SPF + DKIM alignment together.

Also: verify the `FromAddress` / sending domain in the provider console, and keep
`FromAddress` on a domain you control (not a free mailbox) so alignment passes.

Until these are in place, keep `Provider=console` (logs only) so nothing is sent from
an unauthenticated domain.

---

## 5. Notes / deviations

- **No NuGet packages were added.** SMTP uses the built-in `System.Net.Mail`;
  SendGrid uses a raw `HttpClient` POST to the v3 `/v3/mail/send` JSON endpoint. The
  project already references MailKit/MimeKit, but this module deliberately avoids them
  per the built-in-first requirement.
- The full solution build currently fails due to **unrelated, parallel work** in
  `Platform/Hardening/` (untracked; references OpenTelemetry packages that are
  commented out in the `.csproj`). This module was verified to compile with **0 errors
  and 0 warnings** in isolation; it does not touch that code.
- `System.Net.Mail.SmtpClient.SendMailAsync` (net8) has no `CancellationToken`
  overload, so the SMTP provider honors cancellation cooperatively (checks the token
  before the blocking send) rather than mid-transmission.
