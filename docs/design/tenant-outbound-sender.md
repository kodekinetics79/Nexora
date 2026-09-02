# Per-tenant verified outbound sender (issue #54)

Stream 3, item A. Design first; implementation follows this note.

## Problem

Every tenant's customer quotes and supplier RFQs leave from ONE platform-wide address.
With one customer this is invisible. With two, client B's quotes go out from client A's
mailbox: replies land in the wrong inbox, SPF/DKIM/DMARC align to the wrong domain, and
one tenant's sending reputation is every tenant's.

Live evidence (2026-09-02, masked):

| where | what it says |
|---|---|
| `platform.PlatformEmailSettings` (1 row) | provider `smtp`, From `i***@kodekinetics.com`, `smtpout.secureserver.net:587`, guard `Live` |
| `Email_Configurations` BU 7 (tenant 3, Active) | row 10 `Mail Box 2`, SMTP, `i***@kodekinetics.com`, `smtpout.secureserver.net:465`, `UseSSL=t`, `IsActive=t`, password `v1:…` (encrypted) |
| `Email_Configurations` BU 1 (tenant 1, Archived) | row 6, SMTP `mail.spacemail.com:465`, active |
| `/api/Mailbox/outbound-status` for BU 7 | "Quotes and supplier emails WILL be delivered through smtpout.secureserver.net" — true only by coincidence: the platform row happens to use the same host |

Two sources of truth that can disagree (system-design-review shape 1): the screen a
tenant can see reads `Email_Configurations`; the code that sends reads
`PlatformEmailSettings` and never looks at the tenant table.

## Current mechanism

| step | file:line | behaviour |
|---|---|---|
| Transport selection | `Notifications/Runtime/OutboundEmailTransportResolver.cs:120-156` | one platform row (or appsettings fallback), cached, revalidated every 30 s |
| The only `IEmailSender` | `Notifications/Runtime/RuntimeConfiguredEmailSender.cs:43-75` | resolves the platform transport, sends, records health |
| Quote delivery | `QuoteDelivery/QuoteDeliveryDispatcher.cs:57-73` | never sets `From`; tenant company address is Reply-To only |
| SMTP From fallback | `Notifications/Providers/SmtpEmailSender.cs:113-115` | `message.From ?? platform FromAddress` |
| SendGrid From fallback | `Notifications/Providers/SendGridEmailSender.cs:101-102` | same |
| Supplier RFQ gate | `Procurement/ProcurementDeliveryConfiguration.cs:37-44` | `IsConfigured` = platform provider is smtp/sendgrid, captured ONCE at construction |
| Supplier RFQ dispatch | `Procurement/ProcurementDispatchWorker.cs:103-107, 319` | dead-letters `DELIVERY_PROVIDER_NOT_CONFIGURED` from that flag; stamps `ProviderName` from it at claim |
| The one tenant-row sender | `Controllers/SmtpController.cs:76-100` | ad-hoc supplier email: reads the tenant's active SMTP row (lowest Id), From = `ConfigurationName <EmailAddress>`, sends through `IOutboundSmtpTransport` (SSRF-policed, `UseSsl ? SslOnConnect : StartTls`) |
| The banner | `Controllers/MailboxController.cs:108-138` | counts active SMTP rows and asserts they are used |
| System mail | `Platform/Onboarding/TenantAdminInvitationService.cs:135-180`, `Security/PasswordReset/PasswordResetService.cs:237-260` | `IEmailSender.SendAsync` with no From → platform |

## Proposed mechanism

**One resolver answers "who sends this message?", and both the sender and the banner
ask it.**

```
EmailMessage.OwningBusinessUnitId (long?, NEW, explicit)
        │
        ▼
IOutboundSenderResolver.ResolveAsync(businessUnitId?)            singleton, Notifications/Runtime
        │
        ├─ businessUnitId == null ──────────────► platform transport (unchanged resolver)
        │                                          Origin = Platform | Configuration
        └─ businessUnitId set
              └─ ITenantOutboundSenderSource.ResolveAsync(bu)     scoped, Mailbox/, reads Email_Configurations
                     ├─ active SMTP row (lowest Id) ─► TenantSmtpEmailSender over IOutboundSmtpTransport
                     │                                 wrapped in GuardedEmailSender (platform guard options)
                     │                                 From = "<BusinessUnitName>" <row.EmailAddress>
                     │                                 Origin = Tenant
                     └─ none ───────────────────────► platform transport (fallback), Origin = Platform
```

* `EmailMessage.OwningBusinessUnitId` is a NEW explicit field. `BusinessUnitId`/`TenantId`
  stay logging-only, as documented; silently making a correlation field affect delivery
  would change every caller's meaning. Set by: `QuoteDeliverySender`, `NotificationService`
  (parsed from `request.BusinessUnitId`), and the invitation service when the invite is
  issued by a tenant (item B). Password reset and platform-issued invitations leave it null
  → platform From, exactly as today.
* The tenant transport reuses `IOutboundSmtpTransport` (`Security/OutboundSmtpTransport.cs`)
  — the SAME connection policy the tenant's "Test connection" button and `SmtpController`
  already exercise (`UseSsl ? SslOnConnect : StartTls`, `MailEndpointPolicy` egress). Not
  `SmtpEmailSender`, whose TLS mode is derived from the port: a tenant row that tested green
  as implicit-TLS on a non-465 port would fail through that path. One authority, not two.
* Containment is preserved by type: `IOutboundSenderResolver` returns `GuardedEmailSender`,
  never `IEmailSender`, so the platform Redirect/AllowList/DraftOnly guard applies to tenant
  sends by construction.
* No per-tenant cache. "Pause outbound email" flips `IsActive`; a cached sender would keep
  sending for 30 s after the tenant paused it. One projected read per send is the honest
  cost; quote and RFQ sends are rare.
* Cross-tenant refusal in the source: explicit `BusinessUnitId == bu` predicate AND, when the
  DbContext has a scoped tenant, `ScopedTenantId == bu` or throw. A mismatch is a bug and
  must be loud, never a fallback.
* Every send logs the resolved identity:
  `[Notifications] Sending '{Subject}' for BU {bu}: from={From} origin={Origin} host={Host} mailbox={MailboxId}`.
* Supplier RFQ dispatch: `IProcurementDeliveryConfiguration.ResolveAsync(bu)` (default
  interface method so existing fakes compile) consults the same resolver. `IsConfigured` /
  `ProviderName` keep their meaning (platform) but read options lazily instead of capturing
  at construction, which today freezes the pre-warm-up appsettings value.
* `/api/Mailbox/outbound-status` calls the same resolver and reports origin, address,
  display name, host and mailbox id. `CanSendToCustomers` now means "a transmitting sender
  resolves" (tenant row OR platform), which is what the code will do.
* `POST /api/Mailbox/{id}/send-test` sends one real message through that tenant row via the
  guarded tenant sender. Recipient restricted to the signed-in user's address or the
  mailbox's own address (a tenant-operable relay to arbitrary addresses is spam surface).
  Audited as `MailboxTested`.

## What could go wrong

| risk | mitigation |
|---|---|
| Message for BU X sent from BU Y's mailbox | explicit `OwningBusinessUnitId`; source refuses scoped-tenant mismatch; test `never another tenant's` |
| Tenant pauses outbound, mail still leaves via platform fallback | by design (the brief); the banner SAYS so ("…will fall back to the platform sender"); PO decision below |
| A row that never tested green becomes the live sender the moment it is saved | Create/Update already offer `VerifyBeforeSave`; send-test added for a real end-to-end proof |
| Tenant SMTP read fails (DB outage) | the send throws before any socket opens; outbox marks the attempt uncertain and retries — NOT a silent platform fallback, which would send from the wrong identity |
| Display name: `ConfigurationName` ("Mail Box 2") is a label, not a sender name | From display = `BusinessUnit.BusinessUnitName`; PO decision below |
| Two active SMTP rows | lowest Id wins (as `SmtpController` already does); banner keeps `HasAmbiguousOutbound` |
| Containment bypass by a new transport | resolver returns the concrete `GuardedEmailSender` |
| `IsConfigured` captured before warm-up | read lazily per call |

## Tests that prove it

* `TenantOutboundSenderTests` (SQLite `TestDb` + recording `IOutboundSmtpTransport`):
  * dispatcher/sender picks the tenant row when present (From = tenant address, transport
    got the tenant `EmailConfiguration`, platform transport untouched);
  * platform when absent (platform sender invoked, tenant transport untouched);
  * never another tenant's (two tenants, two rows; each send binds its own row; a message
    for BU X under scope Y throws);
  * `ProcurementDispatchWorker.ProcessOneAsync` with platform provider `console` and a
    tenant SMTP row → SENT (revert-proof: with the old flag it dead-letters);
  * `MailboxController.GetOutboundStatus` reports the same origin/address the resolver
    hands the sender, for both the tenant-row and the platform-fallback cases.
* Fixture rows carry the fields production populates: Protocol `SMTP`, port 465,
  `UseSSL=t`, encrypted password (module initialiser installs the test key),
  `IsActive=t`, `PollingInterval`, `CreatedOn`.

## Rollout / rollback

* No schema change. No new configuration. A tenant with no SMTP row sees no change.
* Live effect on deploy: BU 7's quotes and supplier RFQs switch from the platform row to
  its own row 10 — same domain, same host, port 465 instead of 587.
* Rollback = revert the PR; nothing persisted depends on the new path.

## Product-owner decisions to confirm

1. From display name for tenant sends = business unit name (not the mailbox label).
2. Pausing the tenant mailbox falls back to the platform sender (brief), rather than
   containing the tenant. The banner states it.
3. Send-test recipient limited to the signed-in user or the mailbox's own address.
