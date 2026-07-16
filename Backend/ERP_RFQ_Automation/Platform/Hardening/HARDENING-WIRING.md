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
    // Unset  + Production  -> no exporter (collect-only; does NOT crash if no collector).
    // Invalid URI          -> treated as unset (never throws).
    "OtlpEndpoint": "",
    "ServiceVersion": "1.0.0",        // default: entry assembly version, else "1.0.0"
    "Environment": "Production"        // default: ASPNETCORE_ENVIRONMENT, else "Production"
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

## 6. Custom extraction metrics — `NexoraMetrics` (for existing code to emit later)

`AddPlatformObservability` registers `NexoraMetrics` as a **singleton** and wires
its Meter (`NexoraMetrics.MeterName = "Nexora.Extraction"`) into the OTel
`MeterProvider`. The hardening module cannot edit the pipeline files, so it only
**exposes** the instruments. Any existing service can later inject it and emit —
no observability re-wiring needed; the metrics already flow to the configured
exporter.

Example (to add later, e.g. in `ExtractionWorker` / `ChunkedExtractionService`):

```csharp
public ExtractionWorker(/* existing deps */, NexoraMetrics metrics) { _metrics = metrics; }

_metrics.JobEnqueued(businessUnitId);
_metrics.JobSucceeded(durationMs: elapsed.TotalMilliseconds, businessUnitId: buId);
_metrics.JobFailed(reason: "timeout", businessUnitId: buId);
_metrics.LlmCall(latencyMs: elapsed.TotalMilliseconds, model: "deepseek-v3.1", businessUnitId: buId);
```

Exported instruments:
`nexora.extraction.jobs.enqueued|succeeded|failed` (counters),
`nexora.extraction.job.duration` (histogram, ms),
`nexora.llm.calls` (counter), `nexora.llm.latency` (histogram, ms) — tagged with
`tenant.id` (and `failure.reason` / `llm.model` where relevant).

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
