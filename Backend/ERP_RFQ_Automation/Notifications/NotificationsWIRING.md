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

- `NotificationsOptions` (bound + validated from the `Notifications` config section) —
  now the **fallback**, see §1a
- `IEmailTemplateRenderer` → `EmailTemplateRenderer` (singleton)
- `INotificationService` → `NotificationService` (scoped)
- `IEmailSender` → `RuntimeConfiguredEmailSender` (singleton), which resolves the active
  transport per send through `OutboundEmailTransportResolver`
- `OutboundEmailTransportResolver`, `OutboundEmailProbe`, `IOutboundEmailHealth` (singletons)
- `IOptions<NotificationsOptions>` → `EffectiveNotificationsOptions`, so existing consumers
  (notably the activation-link builder) see the settings actually in force
- A named `HttpClient` (`"NotificationsSendGrid"`) via `IHttpClientFactory` for the
  SendGrid provider
- The `outbound-email` health check, tagged `ready`

Add the second line for the operator-configurable half:

```csharp
using ERP_RFQ_Automation.Platform.Notifications;

builder.Services.AddPlatformEmailSettings();   // AFTER AddNotifications
```

No other Program.cs edits are required. No middleware.

---

## 1a. Where the settings come from

The transport used to be selected **once**, in `AddNotifications`, from
`Notifications:Provider`, and registered as a singleton. Changing it meant editing
appsettings and redeploying; the default was `console`, which logs instead of sending; and
nothing said so. A pilot deployment swallowed every activation link, invoice notice and
quote delivery, and the only signal was `emailSent: false` in a provisioning response.

Precedence now:

1. `platform."PlatformEmailSettings"` — one row, saved by a platform Owner through the API
   below. Takes effect **without a restart**.
2. The `Notifications` configuration section — used when no row exists, and as the
   fallback if the row cannot be read (a database outage must not become an outbound
   outage).

The resolved transport is cached and revalidated against the row's `Version`
(a two-column read). A save in the same process invalidates immediately; other instances
converge within `OutboundEmailTransportResolver.RevalidationInterval` (30 s).

**Containment is unchanged.** `GuardedEmailSender` is still the only thing that can send:
`OutboundEmailTransportResolver.ResolveAsync` and `.BuildTransport` return the *concrete*
`GuardedEmailSender` type, so no path through them can produce an unwrapped transport, and
the concrete providers are no longer registered in DI at all.

### Operator API (all under the platform token, `/api/platform`)

- `GET /api/platform/notifications/email/settings` — policy `PlatformScope`. Current
  configuration; secrets reported as `hasSmtpPassword` / `hasSendGridApiKey` only.
- `GET /api/platform/notifications/email/status` — policy `PlatformScope`. "Is mail
  working?": provider, whether it really sends, last success, last failure with reason.
- `PUT /api/platform/notifications/email/settings` — policy `Platform.Owner`. Replaces the
  configuration. `reason` is required, and every save is audited.
- `POST /api/platform/notifications/email/test-send` — policy `Platform.Owner`. Sends one
  real message using the saved **or candidate** settings and classifies the outcome.

Secret semantics on PUT: `null` (or omitted) **keeps** the stored secret, `""` **clears**
it. The console is never given the value, so an empty field must not mean "wipe it".

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

**No UI can do any of this for the operator.** The product cannot publish DNS records for
a domain it does not control, cannot complete a provider's sender-verification flow, and
cannot create the provider account or issue its API key. What it can do — and now does —
is tell the operator the instant the channel is not working, and let them prove it works
before a customer depends on it: `POST .../test-send` reports a relay refusal as
`RelayDenied` with "verify the sending domain with the provider", which is exactly the
DNS work above.

---

## 4a. Database objects (owned by the migration author)

One table, `platform."PlatformEmailSettings"`, single row pinned by
`CK_PlatformEmailSettings_Singleton` (`"Id" = 1`). No indexes beyond the primary key —
one row is never scanned. `SmtpPassword` and `SendGridApiKey` are `varchar(2048)`
carrying the `ProtectedSecretConverter` AES-256-GCM envelope, exactly as
`Email_Configurations.Password` does.

Grants (verified against real PostgreSQL by
`PlatformEmailSettingsPostgreSqlTests`, which executes the DDL and the GRANT block it
specifies):

- `nexora_pipeline_app` — `SELECT, INSERT, UPDATE`; `DELETE`/`TRUNCATE` revoked. A
  single-row configuration that can be deleted is a way to silently revert the platform to
  the console provider.
- `nexora_tenant_app`, `nexora_identity_app` — column-level `SELECT` on the transport
  columns only, no writes. They need it because outbound mail is sent from tenant-scoped
  requests too (quote delivery, lead routing), and those resolve the transport on their own
  connection. `PlatformEmailSettingsStore` therefore **projects** every query; an
  unprojected `SELECT *` fails with 42501 on the first tenant request that sends an email
  while every SQLite test stays green.

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
