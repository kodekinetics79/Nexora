# Platform Hardening — Wiring Guide

Self-contained observability + rate limiting + per-tenant structured logging.
All code lives under `Platform/Hardening/`
(namespace `ERP_RFQ_Automation.Platform.Hardening`) and was written to **not touch
any existing file**. `dotnet build` → **0 errors** (no warnings from these files).

To activate it, the lead splices the lines below into `Program.cs`. Nothing here
changes existing behavior, and every piece has safe defaults — it will **not throw
at startup when configuration is absent**.

---

## 1. Program.cs — `using`

Add near the other usings at the top:

```csharp
using ERP_RFQ_Automation.Platform.Hardening;
```

## 2. Program.cs — service registration (before `var app = builder.Build();`)

Add alongside the other `builder.Services.Add...` calls (anywhere in the service
section, e.g. right after the extraction pipeline registrations):

```csharp
// Platform Hardening (Platform/Hardening): OpenTelemetry + custom metrics, and
// tenant/IP-fair rate limiting. Both are config-driven with safe fallbacks.
builder.Services.AddPlatformObservability(builder.Configuration);
builder.Services.AddPlatformRateLimiting(builder.Configuration);
```

## 3. Program.cs — middleware pipeline (after `var app = builder.Build();`)

The current tail of the pipeline is:

```csharp
app.UseCors("DefaultCors");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
```

Change it to (two inserted lines, **exact order matters**):

```csharp
app.UseCors("DefaultCors");
app.UseAuthentication();
app.UsePlatformObservability();   // tenant + correlation logging scope — AFTER auth so the businessUnitId claim exists
app.UseAuthorization();
app.UseRateLimiter();             // built-in limiter — AFTER auth so the tenant claim partitions correctly
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
```

Why this order:
- `UsePlatformObservability()` (the tenant/correlation logging scope) and
  `UseRateLimiter()` both read the `businessUnitId` claim, so they must run **after
  `UseAuthentication()`**. Placing the logging scope right after auth makes every
  subsequent log line during the request carry tenant + correlation context.
- `app.UseRateLimiter()` is the **built-in** middleware from
  `Microsoft.AspNetCore.RateLimiting` (no extra package). It must sit before
  `MapControllers()`.
- Health checks stay after the limiter but are not rate-limited paths of concern;
  if you want `/health` exempt, it already returns before controller policies apply
  — leave as-is.

---

## 4. appsettings keys

All keys are **optional**; safe fallbacks shown in comments. Add to
`appsettings.json` (and override per-environment / via `Observability__OtlpEndpoint`
env vars on Fly.io).

```jsonc
{
  "Observability": {
    // OTLP collector endpoint (gRPC, e.g. http://otel-collector:4317).
    // Unset  + Development -> Console exporter.
    // Unset  + Production  -> no PUSH exporter (the Prometheus endpoint below
    //                         then turns on, so the process is never blind).
    // Invalid URI          -> treated as unset, logged as a config ERROR, never throws.
    "OtlpEndpoint": "",
    "ServiceVersion": "1.0.0",        // default: entry assembly version, else "1.0.0"
    "Environment": "Production",       // default: ASPNETCORE_ENVIRONMENT, else "Production"
    "Prometheus": {
      // null (default) -> ON exactly when OtlpEndpoint is unset/invalid. This is
      // the zero-dependency fallback that closes the "meter registered, nothing
      // exported" hole. Set true/false to force it.
      "Enabled": null,
      "Path": "/metrics",
      // Required as the X-Scrape-Key header when set. UNSET = open endpoint, and
      // the boot log warns for as long as it is: the exposition carries tenant ids.
      "ScrapeKey": ""
    },
    "QueueMetrics": {
      "Enabled": true,
      "PollInterval": "00:00:15",     // ONE grouped query per interval, never per request
      "QueryTimeout": "00:00:10",
      "MaxTenantSeries": 200          // cardinality cap, worst oldest-age first
    }
  },
  "RateLimiting": {
    "PermitLimit": 600,                // default 600 requests ...
    "WindowSeconds": 60,               // ... per 60s, per tenant (else per IP)
    "QueueLimit": 0,                   // default 0 (reject immediately when over budget)
    "Upload": {                        // stricter "upload" named policy
      "PermitLimit": 30,               // default 30 ...
      "WindowSeconds": 60,             // ... per 60s, per tenant (else per IP)
      "QueueLimit": 0
    }
  }
}
```

Resource attributes reported to the collector: `service.name = "nexora-api"`,
`service.version` (from `Observability:ServiceVersion`), and
`deployment.environment` (from `Observability:Environment`).

### Optional: render the logging scope in the default console logger

The tenant/correlation scope is provider-agnostic. To see it in the **default
console logger**, enable scopes (structured providers capture the fields
automatically and need nothing here):

```jsonc
"Logging": {
  "Console": { "IncludeScopes": true }
}
```

Each request log line then carries `CorrelationId`, `TenantId`, and `RequestPath`.
The correlation id is read from an inbound `X-Correlation-ID` header when present,
else `HttpContext.TraceIdentifier`, and is echoed back on the response
`X-Correlation-ID` header.

---

## 5. Rate limiting the upload endpoint (controller opt-in — do later, not now)

The stricter named policy is `"upload"`. When ready, opt the extraction upload in
by adding **one attribute** to `Controllers/ExtractionController.cs` — no change
required for the limiter to function; the global limiter already covers it:

```csharp
using Microsoft.AspNetCore.RateLimiting;   // top of file

[HttpPost("upload")]
[EnableRateLimiting("upload")]              // <-- add this line
[RequestSizeLimit(200L * 1024 * 1024)]
public async Task<IActionResult> Upload(...)
```

`RateLimitingExtensions.UploadPolicy` holds the policy name if you prefer a
constant.

---

## 6. Custom extraction metrics — `NexoraMetrics` (WIRED, emitting)

`AddPlatformObservability` registers `NexoraMetrics` as a **singleton** and wires
its Meter (`NexoraMetrics.MeterName = "Nexora.Extraction"`) into the OTel
`MeterProvider`.

> **Historical note.** This section used to say the instruments were merely
> *exposed* for the pipeline to emit "later". Later never came: for the whole life
> of the module there was exactly ONE emission site in the codebase
> (`Platform/Entitlements/TenantAccessService.cs`, TenantAccessFailOpen). Every
> other documented instrument was permanently zero — and a flat-zero dashboard
> reads as *healthy*, not as *unmeasured*. They are now emitted from the real
> paths, listed below, and covered by
> `ERP_RFQ_Automation.Tests/PlatformObservabilityMetricsTests.cs`.

### Emission sites

| Instrument | Emitted from |
|---|---|
| `nexora.extraction.jobs.enqueued` | `Extraction/ExtractionQueue.EnqueueAsync` (real insert only, never a duplicate short-circuit) |
| `nexora.extraction.jobs.claimed` | `Extraction/ExtractionQueue.ClaimAsync` |
| `nexora.extraction.claims.refused` | `Extraction/ExtractionQueue.ClaimAsync` (23514 intake-guard refusal) |
| `nexora.extraction.jobs.succeeded` + `nexora.extraction.job.duration` | `Extraction/ExtractionWorker.ProcessOnceAsync` |
| `nexora.extraction.jobs.failed` (+ duration) | `ExtractionWorker.RecordFailureMetrics`, all four failure paths |
| `nexora.extraction.jobs.deadlettered` | same, when the attempt was the last one; category from `ExtractionDeadLetterService.ClassifyFailure` |
| `nexora.extraction.leases.lost` | `ExtractionWorker.LogLeaseLost` |
| `nexora.llm.calls` + `nexora.llm.latency` | `Extraction/ChunkedExtractionService.RecordLlmCall` (both provider call sites, every outcome) |
| `nexora.llm.tokens` + `nexora.llm.cost` | `AI/AiGovernanceService.CompleteAsync`, after the settlement commits |
| `nexora.platform.tenant_access.fail_open` | `Platform/Entitlements/TenantAccessService` |

### Golden-signal gauges (ObservableGauge)

Backed by `ExtractionQueueMetricsPoller` → `IExtractionQueueSnapshotProvider`.
**A collection cycle or a Prometheus scrape costs ZERO database round-trips**;
one bounded `GROUP BY` runs per `Observability:QueueMetrics:PollInterval`.

| Gauge | Meaning |
|---|---|
| `nexora.extraction.queue.oldest_pending_age` (s, per tenant) | Age of the oldest waiting job. **The stuck-tenant signal** — depth stays flat while one tenant starves. Measured from `CreatedOn`, so exponential backoff cannot hide in it. |
| `nexora.extraction.queue.depth` (per tenant × state) | `pending` / `pending_ready` / `pending_backed_off` (poison pressure) / `in_flight` / `dead_letter` |
| `nexora.extraction.queue.expired_leases` (per tenant) | Lapsed leases awaiting reclaim |
| `nexora.extraction.queue.snapshot_age` (s) | Age of the cached snapshot — read this before trusting the others |

### Cardinality contract

Tags are limited to bounded domains: `tenant.id`, `queue.state`,
`failure.category` (closed vocabulary), `failure.reason`, `llm.provider`,
`llm.model`, `llm.direction`. Job ids, document ids, file names, storage paths,
worker ids and raw error text are **never** tags. Per-tenant series are
additionally capped at `MaxTenantSeries`, ranked worst-oldest-age first, with the
remainder reported as a single overflow series.

---

## 7. NuGet packages added (net8.0-compatible)

Added to `ERP_RFQ_Automation.csproj` (the only place new packages were introduced):

| Package | Version |
|---|---|
| OpenTelemetry.Extensions.Hosting | 1.16.0 |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.16.0 |
| OpenTelemetry.Exporter.Console | 1.16.0 |
| OpenTelemetry.Instrumentation.AspNetCore | 1.16.0 |
| OpenTelemetry.Instrumentation.Http | 1.16.0 |
| OpenTelemetry.Instrumentation.EntityFrameworkCore | 1.16.0-beta.1 |

Notes:
- The EF Core instrumentation package ships only as `-beta` (upstream has not
  stabilized it); `1.16.0-beta.1` is the current beta aligned with the 1.16.0 line.
- **Npgsql needs no package** — it publishes activities under the `"Npgsql"`
  `ActivitySource`, which the tracer subscribes to via `AddSource("Npgsql")`.
- **Rate limiting needs no package** — `Microsoft.AspNetCore.RateLimiting` and
  `System.Threading.RateLimiting` are part of the ASP.NET Core 8 shared framework.
- **Logging enrichment needs no package** — no Serilog dependency; uses the
  built-in `ILogger` scope. (Serilog would be optional and is deliberately not
  used; the default-provider solution is preferred.)

---

## New files delivered

```
Platform/Hardening/
├─ HARDENING-WIRING.md              (this file)
├─ NexoraMetrics.cs                 DI singleton; custom extraction/LLM instruments
├─ ObservabilityExtensions.cs       AddPlatformObservability + UsePlatformObservability
├─ RateLimitingExtensions.cs        AddPlatformRateLimiting (global tenant/IP + "upload")
└─ TenantLoggingMiddleware.cs       tenant + correlation-id logging scope
```
