# Runbook — ClamAV malware scanner on Render

Owner: platform / on-call backend.
Applies to: `nexora-fyjw` (web) and `nexora-clamav` (private service), defined in
[`render.yaml`](../render.yaml).

## 1. Why this exists

Every uploaded document is streamed to ClamAV over the INSTREAM protocol before
it can enter evidence storage
(`Backend/ERP_RFQ_Automation/Security/DocumentInspection/MalwareScanners.cs`).

**Scanning fails closed.** If `clamd` cannot be reached, `ClamAvInstreamMalwareScanner`
returns `Unavailable` and the file is quarantined — it is *not* let through. The
same scanner backs the `malware-scanner` readiness check, so an unreachable
`clamd` also makes `GET /ready` return **503** for the whole API.

Before this runbook existed there was no `clamd` deployed anywhere:
`Backend/Dockerfile` installs only Tesseract, and
`DocumentInspection__ClamAV__Host` was `sync: false` (unset), so the scanner fell
back to its `127.0.0.1:3310` default and every upload was quarantined.

## 2. Topology

```text
Vercel SPA ──HTTPS──► nexora-fyjw (Render web service, Docker)
                          │
                          ├── Neon Postgres (public internet, TLS)
                          ├── S3-compatible evidence bucket (public internet)
                          └── TCP 3310 ──► nexora-clamav (Render private service)
                                              docker.io/clamav/clamav:1.5
                                          [Render private network — same region,
                                           same workspace, no public URL]
```

`nexora-clamav` is a **private service** (`type: pserv`). Private services get an
internal hostname on Render's private network and no `onrender.com` subdomain, and
they may listen on almost any port with any protocol — which is what a raw TCP
service like `clamd` needs. Background workers would not work here: they cannot
*receive* private-network traffic.
(<https://render.com/docs/private-services>, <https://render.com/docs/private-network>)

## 3. Environment variables

### Set by the Blueprint — do not override in the Dashboard

| Service | Key | Value |
| --- | --- | --- |
| `nexora-fyjw` | `DocumentInspection__Scanner__Provider` | `ClamAV` |
| `nexora-fyjw` | `DocumentInspection__ClamAV__Host` | `fromService` reference → resolves to the scanner's private hostname (e.g. `nexora-clamav-a1b2`) |
| `nexora-fyjw` | `DocumentInspection__ClamAV__Port` | `3310` |
| `nexora-fyjw` | `DocumentInspection__ClamAV__Timeout` | `00:00:30` |
| `nexora-clamav` | `PORT` | `3310` |
| `nexora-clamav` | `CLAMD_CONF_StreamMaxLength` | `64M` |
| `nexora-clamav` | `CLAMD_CONF_ConcurrentDatabaseReload` | `no` |
| `nexora-clamav` | `CLAMD_CONF_MaxThreads` | `4` |
| `nexora-clamav` | `FRESHCLAM_CHECKS` | `2` |
| `nexora-clamav` | `CLAMAV_NO_MILTERD` | `true` |

If someone types a literal value into `DocumentInspection__ClamAV__Host` in the
Dashboard it **overrides the `fromService` reference** and will silently go stale.
Clear it and re-sync the Blueprint instead.

### You must still set these by hand (`sync: false`)

These are unrelated to ClamAV but `/ready` will not go green without the storage
ones, because the `evidence-storage` check is also part of `/ready`:

`EvidenceStorage__ServiceUrl`, `EvidenceStorage__AccessKeyId`,
`EvidenceStorage__SecretAccessKey`, `EvidenceStorage__Bucket`,
`ConnectionStrings__DefaultConnection`, `ConnectionStrings__MigrationConnection`,
`Jwt__Key`, `Jwt__PlatformKey`,
`CommercialFinance__DunningProviderWebhookSecret`,
`CommercialFinance__ContactVerificationSecret`,
`CommercialFinance__AuditActorSecret`, `Ollama__BaseUrl`, `Ollama__ApiKey`.

## 4. Before the first sync — check the region

**This is the one setting that will silently break everything.** Private
networking only works between services in the **same region** *and* the same
workspace, and a service's region cannot be changed after creation.

1. Render Dashboard → `nexora-fyjw` → **Settings** → note the **Region**.
2. If it is not `oregon`, edit `region:` on the `nexora-clamav` service in
   `render.yaml` to match, then sync.
3. If you get this wrong you must **delete and recreate** `nexora-clamav` in the
   correct region — there is no in-place move.

## 5. First deploy — what to expect, and for how long

There is no dependency ordering in a Render Blueprint: both services start
deploying at once. Expect this sequence:

| t | `nexora-clamav` | `nexora-fyjw` |
| --- | --- | --- |
| 0:00 | Pull `clamav/clamav:1.5` (~150 MB) | Docker build + push |
| ~1:00 | Container starts, entrypoint runs | App boots, EF migrations run |
| ~1:00-4:00 | `clamd` loads ~1.5-2 GB of signatures into RAM; socket not yet accepting | `/ready` returns **503** (`malware-scanner` unhealthy) — **this is expected** |
| ~2:00-5:00 | Log line `socket found, clamd started.` | `/ready` flips to **200** on the next probe |

**Budget 2-5 minutes** for `clamd` to answer after the container starts. Cisco's
own image sets `HEALTHCHECK --start-period=6m` for exactly this reason.

Render waits **up to 15 minutes** for a web service's health check to pass before
cancelling the deploy, so `clamd` normally wins this race and no intervention is
needed. If the web deploy *is* cancelled ("health check failed"), wait for
`nexora-clamav` to log `socket found, clamd started.` and then **Manual Deploy →
Deploy latest commit** on `nexora-fyjw`. Nothing is lost; Render keeps routing to
the previous instances during a failed deploy.
(<https://render.com/docs/health-checks>)

**No health-check timing config needs changing.** Render's thresholds (5 s probe
timeout, 15 s divert, 60 s restart, 15 min deploy window) are not configurable,
and the 15-minute window already covers ClamAV's cold start.

One thing to be aware of: while `clamd` is unreachable, each `/ready` probe holds
a TCP connect attempt open for up to `DocumentInspection__ClamAV__Timeout`
(30 s). Render gives up at 5 s and scores the probe as a failure, which is the
correct outcome, but it does mean `/ready` responds slowly during an outage.
Do not lower that timeout to "fix" the probe — it is the same deadline used for
real 25 MB uploads.

## 6. Verifying the scanner

### 6a. Is `clamd` itself alive?

Dashboard → `nexora-clamav` → **Shell** (or `render ssh <service-id>`):

```sh
echo PING | nc localhost 3310        # expect: PONG
clamdcheck.sh                        # expect: Clamd is up  (the image's own HEALTHCHECK)
echo VERSION | nc localhost 3310     # expect: ClamAV 1.5.x/<db-version>/<db-build-date>
```

The third command is the one that matters for signature freshness — it is the
daemon's own view of the loaded database. If `<db-build-date>` is more than a few
days old, freshclam is not updating; check the logs for `freshclam` errors.

Check the **Logs** tab for `socket found, clamd started.` and for freshclam lines
like `main.cvd database is up-to-date`.

### 6b. Is it reachable *from the backend*?

Dashboard → `nexora-fyjw` → **Shell**. The ASP.NET runtime image has no `nc` or
`curl`, but it is Debian-based so bash's `/dev/tcp` works with no extra tooling:

```sh
# Substitute the scanner's internal hostname (Dashboard → nexora-clamav →
# "Service Address", e.g. nexora-clamav-a1b2)
HOST="$DocumentInspection__ClamAV__Host"

# 1. Plain TCP reachability
timeout 5 bash -c "cat < /dev/null > /dev/tcp/$HOST/3310" && echo "TCP OK"

# 2. Application-level ping (clamd's z-command form is NUL-terminated)
exec 3<>/dev/tcp/$HOST/3310
printf 'zPING\0' >&3
head -c 5 <&3          # expect: PONG
exec 3<&-; exec 3>&-
```

If step 1 hangs or refuses, it is a **network/region** problem (see §4), not a
ClamAV problem.

### 6c. End-to-end via `/ready`

```sh
curl -s -o /dev/null -w '%{http_code}\n' https://nexora-fyjw.onrender.com/ready
# expect: 200
```

`/ready` is the authoritative check: `MalwareScannerHealthCheck` streams an empty
buffer (must come back `Clean`) **and** the EICAR test string (must come back
`Infected`) through the real scanner on every probe. A 200 means `clamd` is
reachable *and* its signatures actually detect. A 503 could be any of
`database`, `evidence-storage`, `malware-scanner`, `extraction-worker`,
`quote-delivery-worker`, `procurement-dispatch-worker` — read the service logs to
see which check reported unhealthy.

`/health` (liveness) only includes `database`, so `/health` 200 + `/ready` 503 is
the signature of a dependency problem rather than a dead app.

The `malware-scanner` entry's description carries `Provider=` and `Endpoint=`, so
`/ready` is also the quickest way to confirm the Blueprint wiring resolved —
`Endpoint=nexora-clamav-xxxx:3310`, never `127.0.0.1:3310`.

### 6d. The startup log (fastest signal of all)

`MalwareScannerStartupProbe` runs one EICAR scan shortly after boot and writes a
single decisive line to `nexora-fyjw`'s logs. Search for `Malware scanner`:

| Log line | Meaning |
| --- | --- |
| `Malware scanner provider selected: ClamAV. ... Source=Configuration Endpoint=nexora-clamav-xxxx:3310` | Wiring is correct. |
| `Malware scanner startup probe passed.` | `clamd` answered and flagged EICAR. Green. |
| `MALWARE SCANNER UNREACHABLE AT STARTUP` (Critical) | No `clamd` at that endpoint. Go to §7. |
| `MALWARE SCANNER DETECTION CONTROL FAILED` (Error) | `clamd` answered but missed EICAR — signature DB missing/stale on the scanner. |
| `REDUCED SECURITY POSTURE` (Warning) | Someone set `DocumentInspection__Scanner__Provider=BuiltIn`. **Not** an anti-virus engine. Revert to `ClamAV`. |
| `... endpoint 127.0.0.1:3310 is a loopback address` (Warning) | `DocumentInspection__ClamAV__Host` did not resolve — the Blueprint reference was overridden or never synced. |

The probe is fire-and-forget and never delays the listener, so a black-holed
scanner endpoint cannot stall the API's startup.

## 7. When the scanner is down

**Blast radius:** every document upload is quarantined (fail-closed, by design —
nothing unscanned reaches evidence storage), and `/ready` returns 503.

**The compounding risk:** because `healthCheckPath` is `/ready`, Render diverts
traffic from a `nexora-fyjw` instance after 15 s of consecutive failures and
**restarts the instance after 60 s**. A `clamd` outage therefore also puts the
API into a restart cycle — read requests that have nothing to do with uploads go
down too.

Triage in this order:

1. **Is `nexora-clamav` running?** Dashboard → Events. Look for `Out of memory`
   / exit code 137. If so, see §8 — it needs a bigger instance, not a restart.
2. **Region/hostname drift.** Re-run §6b. If TCP fails but the service is up,
   confirm both services are in the same region and that
   `DocumentInspection__ClamAV__Host` has not been manually overridden.
3. **`clamd` up but scans erroring.** If `/ready` logs
   `ClamAV rejected the scan: INSTREAM size limit exceeded`, then
   `CLAMD_CONF_StreamMaxLength` is below the app's 25 MB upload ceiling —
   confirm it is `64M`.
4. **Restart the scanner:** Dashboard → `nexora-clamav` → Manual Deploy →
   *Deploy latest reference*. Expect another 2-5 min of 503s on `/ready`.

### After the scanner is back — release the held files

Files blocked during the outage are held with their immutable source object
intact, so **nobody has to re-upload anything**. Once `/ready` is 200:

```sh
# What is still held, per batch (authenticated tenant call)
GET  /api/LeadIngestion/blocked-files

# Replay every held file for the tenant. Capped per call — repeat while the
# response's `moreRemaining` is true.
POST /api/LeadIngestion/retry-blocked-files

# Or scope it to one batch
POST /api/LeadIngestion/batches/{batchId}/retry-blocked-files
```

Tell affected users not to re-upload: the tenant-facing error text already says
"Your file was stored safely and is being held for scanning — do not upload it
again."

### Emergency levers (both are deliberate posture changes — get sign-off)

**Lever 1 — stop the API flapping (low risk).** Change `healthCheckPath` to
`/health` in `render.yaml`. Uploads stay quarantined, but the API stops
restarting itself every 60 s, so everything unrelated to ingestion keeps serving.
Revert to `/ready` the moment the scanner is back.

**Lever 2 — accept uploads without anti-virus (high risk, rarely justified).**
Setting `DocumentInspection__Scanner__Provider=BuiltIn` on `nexora-fyjw` swaps in
the in-process inspector: structural/type/archive checks plus the EICAR reference
string **only**. It detects no real malware and has no signature updates. The app
logs `REDUCED SECURITY POSTURE` on every boot while it is set. Do not use it to
paper over an outage on a tenant handling customer documents; fix the scanner.

Strict readiness is a stated pilot gate in
`docs/nexora/releases/module-01-governed-ingestion-lead-intelligence.md`, so
record either lever as a temporary exception with an owner and an end date.

## 8. Sizing and cost

| Item | Instance | RAM | Price |
| --- | --- | --- | --- |
| `nexora-clamav` (this change) | `pro` | 4 GB / 2 CPU | **$85/mo** |
| `nexora-fyjw` | unchanged | — | unchanged |
| Private-network traffic between them | — | — | **$0** (not billed) |

**Cost delta: +$85/month.**
(<https://render.com/pricing>, <https://render.com/docs/compute-plans>,
<https://render.com/docs/outbound-bandwidth>)

Why `pro` and not `standard` ($25/mo, 2 GB): ClamAV's signature set is roughly
1.5-2 GB resident, and ClamAV's own documentation recommends **4 GB** for
containers, warning that "if your container does not have enough RAM you can
expect that the OS (or Docker) may kill your clamd process."
(<https://docs.clamav.net/manual/Installing/Docker.html>)

**Cost-down variant (accepted risk, ~$60/mo saved).** `standard` can work if you
give up in-place signature updates:

```yaml
plan: standard
envVars:
  - key: CLAMAV_NO_FRESHCLAMD
    value: "true"        # no update daemon => no reload memory spike
```

Signatures then only refresh when you redeploy the service (the `1.5` tag is
rebuilt daily with fresh databases, so a redeploy is enough). Schedule a weekly
manual redeploy if you take this option, and watch memory metrics — 2 GB leaves
almost no headroom and an OOM kill means every upload quarantines.

**Optional: persistent disk.** Mounting a disk at `/var/lib/clamav`
(`sizeGB: 2`, +$0.50/mo) survives signature downloads across restarts. Note the
trade-off: the mount *shadows* the databases baked into the image, so the very
first boot runs a full foreground `freshclam` download and takes considerably
longer than the 2-5 min above. Not enabled by default.

## 9. Rejected alternatives (for the record)

- **`clamd` co-installed in the backend image with a supervisor.** Forces the
  backend onto a ≥4 GB instance for a workload it does not otherwise need,
  couples scanner restarts to API restarts, and every backend deploy pays the
  full signature-load cold start. Rejected.
- **Hosted scanning API (VirusTotal, Cloudmersive, etc.).** No infra to run, but
  it means shipping customer RFQ documents to a third party, adds an egress
  dependency on the upload hot path, and would require replacing
  `ClamAvInstreamMalwareScanner` with an HTTP client — a C# change, outside this
  change's scope. Rejected.


## Deferred configuration (paste-ready)

ClamAV is currently disabled; the backend runs `DocumentInspection__Scanner__Provider=BuiltIn`.
To re-enable real malware scanning, restore both blocks below into `render.yaml`, set
`DocumentInspection__Scanner__Provider` back to `ClamAV`, and sync the Blueprint.

### 1. The private service (goes under `services:`)

```yaml
  - type: pserv
    name: nexora-clamav
    runtime: image
    region: oregon
    # 4 GB / 2 CPU. ClamAV's own docs recommend 4 GB for containers: the
    # signature set is ~1.5-2 GB resident and clamd briefly needs more while a
    # freshclam update is loaded. `standard` (2 GB) OOM-kills clamd during
    # updates. See the cost-down variant below.
    plan: pro
    image:
      # Minor-version pinned. Cisco-Talos rebuilds this tag daily with refreshed
      # signature databases baked in, so a restart starts from a recent DB
      # instead of downloading the full set. The `_base` tags ship NO databases
      # and would add several minutes of freshclam download to every cold start.
      url: docker.io/clamav/clamav:1.5
    envVars:
      # Render must detect a bound port to give this service an internal
      # address. clamd binds 3310 (EXPOSE 3310 in the upstream image); declaring
      # PORT here stops Render from probing its 10000 default.
      # Note: 10000, 18012, 18013 and 19099 cannot be used on the private
      # network (https://render.com/docs/private-network#port-restrictions).
      - key: PORT
        value: "3310"

      # The app accepts uploads up to 25 MB (DocumentInspectionOptions
      # .DefaultMaximumFileBytes). clamd's default StreamMaxLength is also 25M,
      # so a max-size upload sits exactly on the limit and clamd would answer
      # "INSTREAM size limit exceeded" - which the client treats as an error and
      # quarantines. Give it headroom.
      - key: CLAMD_CONF_StreamMaxLength
        value: 64M
      # Concurrent reload builds a SECOND copy of the signature engine while the
      # old one drains, roughly doubling RSS for a few minutes after every
      # freshclam update. Disabling it trades a short scan pause for staying
      # inside 4 GB.
      - key: CLAMD_CONF_ConcurrentDatabaseReload
        value: "no"
      # Default is 20 worker threads; each concurrent scan costs memory. The
      # backend is a single web service doing document uploads, not a mail relay.
      - key: CLAMD_CONF_MaxThreads
        value: "4"
      # Check for signature updates twice a day (default 1).
      - key: FRESHCLAM_CHECKS
        value: "2"
      # We only speak INSTREAM over TCP; no need for the milter listener.
      - key: CLAMAV_NO_MILTERD
        value: "true"
```

### 2. The host reference (goes in the web service `envVars:`)

```yaml
      - key: DocumentInspection__ClamAV__Host
        fromService:
          name: nexora-clamav
          type: pserv
          property: host
```
