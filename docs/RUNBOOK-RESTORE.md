# Disaster recovery runbook — restore and rehearsal

**Status: WRITTEN, NEVER EXECUTED.** No restore of any component of this deployment has ever
been performed or timed. Every RPO and RTO in section 2 is either measured from a read-only
observation made on **2026-09-03** or marked **UNKNOWN**, and none of them is measured from a
completed restore. `docs/GATE9_10_READINESS.md` item 4 is closed by *running the drill in
section 3*, not by this file existing.

The read-only evidence behind every claim here — the queries, their exact output, and today's
row-count baseline — is in **[`RUNBOOK-RESTORE-EVIDENCE.md`](RUNBOOK-RESTORE-EVIDENCE.md)**.

One correction to the readiness page before anything else. It says *"The 5 GB volume holding every
source document has no backup configured."* **Two halves of that are now wrong.** The volume *does*
have daily snapshots — seven of them are listed in section 1.2, taken automatically, retained seven
days — and it no longer holds *every* source document: 313 of 409 live in Backblaze B2 and only 96
still resolve to the disk. What remains true, and is the whole point of this file, is that **nothing
has ever been restored from any of it.**

---

## 1. What must be restored, and from where

Five stateful things. They fail independently and they are recovered by five different mechanisms,
three of which are unowned.

| # | Component | Where it lives | Backup mechanism | Retention | Verified? |
|---|---|---|---|---|---|
| 1 | Postgres | Neon `neondb`, project region **`us-east-1`** | Neon branch / point-in-time restore | **UNVERIFIED — see 1.1** | region verified, retention not |
| 2 | Evidence bytes (governed) | Backblaze B2 `NexoraBucket` via S3 API | **Object versioning only** | **none — versions are kept forever, nothing else exists** | versioning verified live |
| 3 | Evidence bytes (legacy) | Render disk `dsk-d9p57srl550s73fqjuv0` at `/var/data` | Render automatic disk snapshots | **7 days, verified empirically** | verified |
| 4 | Boot secrets | Render service env vars **only** | **NONE** | n/a | verified absent |
| 5 | Deployment shape | `render.yaml` (contract) + the live dashboard (truth) | git, for the contract half | n/a | verified divergent |

### 1.1 Neon Postgres — the `neon-pitr-7d` claim is not true as written

`DEPLOYMENT.md:96` shows

```ini
Platform__DataBoundaries__PostgreSqlTenantScope__BackupPolicyReference=neon-pitr-7d
```

and the operator's recollection was six hours. **Both are unverified, and the document is
misleading in a way that matters more than the number.**

What is verified, read-only, today:

* **`Platform__DataBoundaries__*` is not set on the production service at all.** All 42 environment
  variables on `srv-d9csjhe1a83c739phue0` were enumerated by name; not one of them begins with
  `Platform__DataBoundaries`. The block at `DEPLOYMENT.md:93-98` is an *example*, not a description
  of production. **No tenant has ever been told `neon-pitr-7d`.**
* **What tenants *were* told is worse than a wrong number.** `platform."TenantDataAssets"` holds
  exactly one row in production:

  | TenantId | AssetType | OpaqueProviderReference | Region | BackupPolicyReference | Status | VerifiedOn |
  |---|---|---|---|---|---|---|
  | 3 (Noor Sons LLC) | PostgreSqlTenantScope | `NAS-1001` | `usa` | **`NAS-1001`** | `Registered` | *(null)* |

  The backup policy this tenant carries is the string `NAS-1001` — hand-typed, identical to the
  provider reference, and asserting nothing whatsoever about retention. `Status` is `Registered`,
  not `Verified`, and `VerifiedOn` is null, so the probe DEPLOYMENT.md describes has never run.
* **The Neon project region is `us-east-1`,** read from the compute itself:
  `neon.pageserver_connstring = host=pageserver-20.cell-2.us-east-1.aws.neon.tech`. The Render
  service is in `oregon` (us-west-2). The declared boundary says `usa`, which equals neither.
* Postgres 17.11, database `neondb`, **69 MB total.** Roles `nexora_runtime` (the only one that can
  log in), `nexora_tenant_app`, `nexora_identity_app`, `nexora_pipeline_app`, `nexora_purge_app`.

**So: the retention window is UNVERIFIED, and this runbook refuses to guess it.** Neon's own default
differs by plan (24 h on Free, longer on paid tiers, configurable), so inferring it from "we are on
a paid plan" would be exactly the invented number this document is not allowed to contain.

**How to settle it in one click** — do this before the drill, and write the answer into the results
table in section 5:

> Neon Console → the project → **Settings → Storage → History retention**. The value shown there is
> the PITR window, and the same page is where it is changed.

Or, with a Neon API key (`https://console.neon.tech/app/settings/api-keys`), without a browser:

```bash
curl -sS -H "Authorization: Bearer $NEON_API_KEY" \
  https://console.neon.tech/api/v2/projects/<project-id> \
  | python3 -c 'import json,sys; p=json.load(sys.stdin)["project"]; \
print("history_retention_seconds:", p["history_retention_seconds"], \
"=", p["history_retention_seconds"]/3600, "hours"); print("region:", p["region_id"])'
```

**No Neon API key or CLI is available on the operator workstation today** — `neonctl` is not
installed and the console credential store is not readable from the agent harness. Provisioning a
read-only Neon API key is a prerequisite for running the drill unattended; without it, every Neon
step in section 3 is a dashboard click.

**What is NOT protected on the database side, regardless of the retention number:**

* No logical dump exists anywhere. There is no `pg_dump` on a schedule, to any destination.
* Neon PITR is *inside* Neon. A Neon account compromise, a billing lapse, or a project deletion
  takes the backups with the database. **There is no copy of this data outside Neon.**
* Neon PITR restores to a *branch*, which consumes the same project's storage quota. Restoring does
  not protect against the project itself being lost.

### 1.2 The Render disk — snapshots exist, and the readiness page says they do not

Verified by API on 2026-09-03. Disk `dsk-d9p57srl550s73fqjuv0`, `/var/data`, 5 GB, created
2026-08-04, attached to `srv-d9csjhe1a83c739phue0`. Seven snapshots are retained:

| Snapshot taken (UTC) | Interval since previous |
|---|---|
| 2026-09-03T00:02:43Z | 23 h 44 m |
| 2026-09-02T00:18:42Z | 23 h 59 m |
| 2026-09-01T00:19:09Z | 24 h 08 m |
| 2026-08-31T00:11:22Z | 24 h 12 m |
| 2026-08-29T23:59:19Z | 24 h 01 m |
| 2026-08-28T23:57:50Z | 23 h 54 m |
| 2026-08-28T00:03:41Z | — (oldest) |

Seven snapshots spanning 5.999 days, roughly daily around 00:00 UTC, oldest evicted. **That is
Render's automatic daily snapshot with 7-day retention, empirically confirmed.**

```bash
# List them yourself (read-only):
API_KEY="$(grep -A2 '^api:' ~/.render/cli.yaml | grep -m1 'key:' | sed 's/.*key:[[:space:]]*//' | tr -d '"'\''  \r')"
curl -sS -H "Authorization: Bearer $API_KEY" -H 'Accept: application/json' \
  "https://api.render.com/v1/disks/dsk-d9p57srl550s73fqjuv0/snapshots?limit=20" \
  | python3 -c 'import json,sys; [print(s["createdAt"]) for s in json.load(sys.stdin)]'
```

**Restore is a dashboard click.** Render Dashboard → `Nexora` → **Disks** → the disk → **Restore**
→ pick a snapshot. There is no supported public API verb for restoring a disk snapshot; the API
lists them and nothing more. **Restoring overwrites the live volume in place** — there is no
"restore to a new disk" option — so a disk restore on the production service is destructive to
anything written since the snapshot. This is why section 3 never restores the production disk.

**What is still on this disk:** `/ready` reports *4,911 MB free (99.1%)*, so roughly **44 MB in
use**. 96 `source_documents` rows still carry `object_bucket = 'local'` (91 for BU 1, 5 for BU 7)
and 114 of 312 `Attachments` rows carry a relative, non-`s3://` path. Both sets resolve to this
volume. `EvidenceStorage__RouteLegacyWritersToObjectStore` and
`EvidenceStorage__LegacyMigration__Enabled` are **not set** on the service, so they default false
and the legacy writers are still writing here.

**Not protected:** the 91 BU-1 documents were written 2026-07-28 → 2026-08-06, and `render.yaml`
records that `Storage__RootPath` once pointed at `/tmp` with mount enforcement disabled. Whether
those bytes actually survive on the volume today **has never been checked** and cannot be checked
read-only from outside the container. Section 3's pre-flight includes the check.

### 1.3 Backblaze B2 `NexoraBucket` — versioning is real and is the only control

The bucket name is mixed case **on purpose** and must stay exactly `NexoraBucket`
(`EnsureConfiguredBucket` compares ordinally; incident 2026-08-16).

**Versioning is verified enabled, live, right now** — and the proof is stronger than a console
screenshot. `EvidenceStorageHealthCheck` calls `S3EvidenceObjectStorage.ProbeAsync` on **every**
`/ready` request, and `ProbeAsync` does four things in order
(`Backend/ERP_RFQ_Automation/Infrastructure/Storage/IEvidenceObjectStorage.cs:583-620`):

1. `ValidateServiceEndpoint` — refuses a non-HTTPS, non-loopback endpoint.
2. `VerifyBucketVersioningAsync` → `GetBucketVersioning`, then `EnsureVersioningEnabled`, which
   **throws** unless the status is exactly `Enabled` (`:949-967` and `:1000-1005`).
3. Writes 32 random bytes to `_readiness/<guid>.probe` with `If-None-Match: *` and a `sha256`
   metadata header, `GetObject`s it back, and re-hashes the returned stream against that digest.
4. Deletes the probe object in a `finally`.

`/ready` returned `evidence-storage: Healthy` at 2026-09-03T03:2x UTC. Step 2 has one escape hatch —
it returns silently on HTTP 405/501 for stores that do not implement the versioning API — but B2
does implement it, and `render.yaml` says so explicitly. **Healthy therefore means the bucket's
versioning status is `Enabled`,** proven by the running service and not by anyone's memory.

**What is NOT protected, and none of it is a code change:**

* **No lifecycle rules.** Nothing expires a noncurrent version, and nothing cleans up delete
  markers. This is not only a cost question: `ProbeAsync`'s cleanup calls `DeleteObject` **without**
  a version id, which on a versioned bucket only writes a delete marker. **Every single `/ready`
  hit permanently adds one noncurrent version plus one delete marker to the production bucket, and
  nothing ever removes them.** `/ready` is polled. This grows without bound.
* **No deletion protection / object lock.** B2 supports Object Lock; nothing in this deployment
  requests it. A compromised `EvidenceStorage__SecretAccessKey` can delete versions permanently —
  and the application already has code that does exactly that
  (`TryDeletePurgedObjectAsync` deletes a *specific version*, which genuinely frees bytes).
* **No second copy.** No replication, no cross-account, no cross-region, no offline copy. B2 is a
  single point of failure for 313 of the 409 evidence objects.
* **No credential rotation path.** `EvidenceStorage__AccessKeyId` / `SecretAccessKey` exist only as
  Render env vars.

### 1.4 The boot secrets — the only truly unrecoverable component

**Correction to the brief: it is six, not four.** `DEPLOYMENT.md`'s reference table names six
secrets that exist only as Render environment variables and that the process refuses to boot
without. All six were confirmed present on the service by name:

| Env var | What breaks on loss | Recoverable? |
|---|---|---|
| `Security__SecretProtectionKey` | **All 10 stored customer mailbox passwords become permanently undecryptable.** AES-256-GCM, single key, `v1:` envelope, **no key ring and no rotation path** (`Security/SecretProtection.cs`). Verified: `Email_Configurations` holds 10 rows, all 10 in the `v1:` envelope. | **NO** |
| `Jwt__Key` | Every live tenant session invalidated | Yes — mint a new one, everyone re-authenticates |
| `Jwt__PlatformKey` | Platform console sessions invalidated. Must be present **and different from `Jwt__Key`** or `AddPlatformJwtBearer` throws at startup | Yes, same way |
| `CommercialFinance__AuditActorSecret` | HMAC binding actors to governed mutations; already-written audit attributions no longer verify | Partially — it is mirrored into `public."FinanceProviderSecrets"` by `SyncFinanceProviderSecretsAsync` on **every** boot, so a database restore recovers the *value*, but the process still cannot boot without the env var |
| `CommercialFinance__ContactVerificationSecret` | as above | as above |
| `CommercialFinance__DunningProviderWebhookSecret` | as above | as above |

Also env-var-only, and needed to reach the data at all: `ConnectionStrings__DefaultConnection`,
`ConnectionStrings__MigrationConnection`, `EvidenceStorage__AccessKeyId`,
`EvidenceStorage__SecretAccessKey`, `Ollama__ApiKey`, `Agent__Anthropic__ApiKey`.

**There is no export of any of these anywhere.** Losing the Render service, the Render account, or
the workspace loses `Security__SecretProtectionKey`, and losing that loses ten corporate mailbox
credentials with no way back. **This is the single largest unmanaged risk in the deployment, and it
is a five-minute fix: put those six values in a password manager today, before the drill.**

`Platform__BootstrapOwnerEmail` / `Platform__BootstrapOwnerPassword` are **not set** on the live
service. That is correct and inert now (a platform user exists, and `PlatformOwnerSeeder` skips
forever once any row is present) — but a restore onto a **fresh, empty** platform schema with those
two unset produces a deployment nobody can sign into, silently, with `/health` green. Section 4
covers it.

### 1.5 The deployment shape

`render.yaml` is a reviewed contract, not the truth; the service was created by hand. Verified
divergences today:

| | `render.yaml` | Live service |
|---|---|---|
| disk name | `nexora-evidence` | `disk` |
| service plan | *(implied by ClamAV block: `pro` for the pserv)* | web `starter`, ClamAV pserv `standard` |
| instances | not declared | 1 |
| `Platform__DataBoundaries__*` | documented in DEPLOYMENT.md | **absent** |
| `Observability__Prometheus__ScrapeKey` | commented out | absent (Enabled=false) |

Region `oregon`, `autoDeployTrigger: checksPass`, `healthCheckPath: /health` all match.

---

## 2. RPO and RTO — measured, or stated as unknown

**Nothing here is a target. These are observations, and the blanks are blanks.**

### RPO — how much work is lost

| Component | Arithmetic | Result |
|---|---|---|
| Postgres | Neon PITR window, granularity ≈ seconds within the window | **UNKNOWN.** Cannot be stated until 1.1's one-click check is done. If the window is ≥ the detection-to-decision time, RPO ≈ minutes; if it is 6 h and the loss is noticed on day 2, RPO = **total loss**. |
| B2 evidence | Versioning retains every prior version indefinitely; a *version* delete is permanent and immediate | **0 for overwrite/accidental-current-delete. ∞ (unrecoverable) for a version delete.** No lifecycle rule protects it and no second copy exists. |
| Render disk | Longest observed inter-snapshot interval **24 h 12 m** (2026-08-29T23:59 → 2026-08-31T00:11), measured over 7 snapshots | **worst case 24 h 12 m** of legacy-evidence writes |
| Boot secrets | No backup exists | **∞ — total, permanent** |
| Deployment shape | git for the contract; the divergences in 1.5 exist only in the dashboard | **partial**; the divergences are lost |

**Overall worst case RPO: unbounded, because of the secrets.** For data alone, and assuming the Neon
window turns out to be ≥ 24 h, the binding component is the disk at **24 h 12 m** — which is inside
the stated 24 h target only if you round, and outside it if you do not.

### RTO — how long until service is restored

| Step | Arithmetic | Result |
|---|---|---|
| Detect + decide | no alerting on data loss exists; `/ready` is not polled by anything that pages a human | **UNKNOWN, and plausibly hours** |
| Neon PITR branch | Neon creates branches copy-on-write | **UNMEASURED** (documented as near-instant; never timed here) |
| Repoint a service at the restored branch | env var change + redeploy. **Measured**: last 6 successful deploys of this service took 1.0, 1.1, 1.1, 3.3, 3.4 and 3.7 minutes wall clock, including Docker build | **1–4 min**, for an existing service with a warm build cache |
| Cold scratch service (no cache) | not measured | **UNMEASURED** |
| Render disk snapshot restore | dashboard only, overwrites in place | **UNMEASURED** |
| B2 version recovery | no tooling exists in this repo; would be a hand-written `ListObjectVersions` + `CopyObject` loop per key | **UNMEASURED, and unwritten** |
| Secret re-entry | 6 values, by hand, in the dashboard | ~10 min **if a copy exists.** Today no copy exists, so this step is **impossible**, not slow |
| Verification (section 3 step 6) | ~15 min of assertions | **UNMEASURED** |

**Overall worst case RTO: UNKNOWN.** The stated 8-hour target has never been tested against
anything. The honest statement to put in front of the board is: *"the recovery time for this system
is unknown, and one of its components has a recovery time of infinity."*

---

## 3. The drill

Written to be followed at 2am by someone who did not write it. **It never touches production.** It
creates a Neon branch (copy-on-write, non-destructive), points a *scratch* Render service at it,
proves the restored copy, and destroys both.

**Prerequisites** (do these in daylight, not at 2am):

* A Neon API key, or a browser session on the Neon console.
* The Render API key from `~/.render/cli.yaml`, and dashboard access.
* The six boot secrets from section 1.4, in hand. **If you cannot produce them, stop — the drill
  cannot pass, and you have just found the finding.**
* A copy of `docs/RUNBOOK-RESTORE-EVIDENCE.md` for the baseline numbers.

Throughout: **`[DASHBOARD]`** marks a step with no API path. Everything else is a command.

### Step 0 — record the target time and the pre-drill truth

```bash
export DRILL_T0="$(date -u +%FT%TZ)"          # the point in time you will restore to
echo "restore target: $DRILL_T0"
cd /Users/zackkhan/Nexora/Nexora-main

# Live baseline, from production, read-only. Compare everything later against this.
.claude/prod-read.sh "
SELECT t AS table_name, c AS rows FROM (
  SELECT 'Leads' t, count(*) c FROM \"Leads\"
  UNION ALL SELECT 'LeadItems', count(*) FROM \"LeadItems\"
  UNION ALL SELECT 'RFQ', count(*) FROM \"RFQ\"
  UNION ALL SELECT 'RFQItems', count(*) FROM \"RFQItems\"
  UNION ALL SELECT 'Quotes', count(*) FROM \"Quotes\"
  UNION ALL SELECT 'supplier_quotes', count(*) FROM supplier_quotes
  UNION ALL SELECT 'source_documents', count(*) FROM source_documents
  UNION ALL SELECT 'ExtractionJobs', count(*) FROM \"ExtractionJobs\"
  UNION ALL SELECT 'EmailIngests', count(*) FROM \"EmailIngests\"
  UNION ALL SELECT 'Attachments', count(*) FROM \"Attachments\"
  UNION ALL SELECT 'Email_Configurations', count(*) FROM \"Email_Configurations\"
  UNION ALL SELECT 'platform.Tenants', count(*) FROM platform.\"Tenants\"
) x ORDER BY 1;"

# The single value that proves the evidence estate is intact, order-independent:
.claude/prod-read.sh \
  "SELECT count(*) AS docs, md5(string_agg(content_hash, ',' ORDER BY id)) AS spine_digest
     FROM source_documents;"
```

Baseline as of 2026-09-03T03:25Z: **409 docs, `spine_digest = 037c652bb2369c620418111f8362ff04`.**

Also check, now, whether the legacy disk files still exist — this has never been verified:

```bash
# Every source_document that still resolves to the Render volume rather than B2.
.claude/prod-read.sh \
  "SELECT business_unit_id, count(*) FROM source_documents
    WHERE object_bucket = 'local' GROUP BY 1 ORDER BY 1;"   # expect 1 -> 91, 7 -> 5
```

Then, for a sample of those ids, fetch `GET /api/File/source-document/{id}` from **production**
with a valid tenant token. A 200 with matching bytes proves the file is on the volume; a 404 or a
digest error proves the 2026-07/08 legacy estate is **already lost** and no restore will bring it
back. Record the answer in the results table.

### Step 1 — [DASHBOARD] confirm the Neon history-retention window

Do the one-click check from section 1.1. **If `$DRILL_T0` minus the window is in the future — i.e.
the window is shorter than the age of the point you want — the drill target must move.** Write the
number down; it is the answer to the question this whole runbook was written to settle.

### Step 2 — create a Neon branch restored to a point in time

**[DASHBOARD]** Neon Console → project → **Branches** → **New branch** → source `main` (or
`production`), **Include data up to** → pick a specific time → name it `drill-YYYYMMDD`.

With an API key instead:

```bash
curl -sS -X POST -H "Authorization: Bearer $NEON_API_KEY" -H 'Content-Type: application/json' \
  "https://console.neon.tech/api/v2/projects/<project-id>/branches" \
  -d "{\"branch\":{\"name\":\"drill-$(date -u +%Y%m%d)\",\"parent_timestamp\":\"$DRILL_T0\"},
       \"endpoints\":[{\"type\":\"read_write\"}]}"
```

Capture the branch's **direct** connection URI from the response (or the console's *Connection
details*, with **Pooled connection** switched **off** — see failure mode 4.5). Note the endpoint
host and set:

```bash
export DRILL_DIRECT="ep-....us-east-1.aws.neon.tech"    # NO -pooler
```

**Time this step. It is the first real RTO number this project will ever have.**

### Step 3 — prove the restored branch before any application touches it

Run these against the branch as `neondb_owner` (the branch's owner role), *not* through
`prod-read.sh`, which is hard-wired to production.

```bash
export DRILL_URL="postgresql://neondb_owner:<pw>@${DRILL_DIRECT}/neondb?sslmode=require"

# 3a. The five execution roles must exist. pg_dump never emits roles — they are cluster-scoped —
#     so if this restore path had been a logical dump instead of a branch, this returns 0.
psql "$DRILL_URL" -c \
  "SELECT rolname, rolcanlogin, rolbypassrls, rolinherit FROM pg_roles
    WHERE rolname LIKE 'nexora\_%' ORDER BY 1;"
# expect exactly 5: nexora_identity_app, nexora_pipeline_app, nexora_purge_app,
#                   nexora_runtime, nexora_tenant_app
# and: only nexora_runtime has rolcanlogin; nexora_tenant_app is NOT bypassrls;
#      identity + pipeline ARE bypassrls; all NOINHERIT.

# 3b. RLS policies survived and still name the tenant role.
psql "$DRILL_URL" -c \
  "SELECT count(*) AS policies FROM pg_policies WHERE schemaname IN ('public','platform');"

# 3c. Row counts. Compare every line against step 0.
psql "$DRILL_URL" -c \
  "SELECT 'Leads' t, count(*) c FROM \"Leads\"
     UNION ALL SELECT 'source_documents', count(*) FROM source_documents
     UNION ALL SELECT 'Attachments', count(*) FROM \"Attachments\"
     UNION ALL SELECT 'Email_Configurations', count(*) FROM \"Email_Configurations\"
     UNION ALL SELECT 'platform.Tenants', count(*) FROM platform.\"Tenants\" ORDER BY 1;"

# 3d. THE assertion. One value, order-independent, covers the whole evidence estate.
psql "$DRILL_URL" -c \
  "SELECT count(*) AS docs, md5(string_agg(content_hash, ',' ORDER BY id)) AS spine_digest
     FROM source_documents;"
# MUST equal step 0's digest if $DRILL_T0 is after the newest document.

# 3e. The mailbox envelopes are intact (they decrypt with the env-var key, not with anything here).
psql "$DRILL_URL" -c \
  "SELECT count(*) total, count(*) FILTER (WHERE \"Password\" LIKE 'v1:%') enveloped
     FROM \"Email_Configurations\";"   # expect 10 / 10
```

### Step 4 — [DASHBOARD] stand up a scratch Render service

There is no supported API for cloning a service's environment. **[DASHBOARD]** Render → **New** →
**Web Service** → same repo `kodekinetics79/Nexora`, branch `main`, Docker context `Backend`,
Dockerfile `Backend/Dockerfile`, region `oregon`, plan `starter`, name `nexora-restore-drill`.

**Attach no disk.** Instead set the emergency-deployment storage posture from `DEPLOYMENT.md`:

```text
Storage__RootPath=/tmp/nexora/uploads
Storage__EnforcePersistentMount=false
# and OMIT Storage__RequiredMountPath entirely
```

Copy every other env var from production **except** these, which must change:

```text
ConnectionStrings__DefaultConnection   -> the DRILL branch, direct host, user nexora_runtime
ConnectionStrings__MigrationConnection -> the DRILL branch, direct host, owner role
Database__ApplyMigrationsOnStartup     -> false      # see failure mode 4.2 — this is not optional
Cors__AllowedOrigins__0                -> the scratch service's own URL
DocumentInspection__Scanner__Provider  -> BuiltIn    # do not wire the drill to the live ClamAV pserv
Notifications__Provider                -> console    # NOTHING MAY LEAVE THIS SERVICE BY EMAIL
```

Copy `EvidenceStorage__*` **unchanged**, including `Bucket=NexoraBucket`, exactly — mixed case. The
drill deliberately reads the *production* bucket, because reading is what needs proving and B2 has
no clone. **It will also write and delete one probe object per `/ready` call** (section 1.3); that
is acceptable and is itself a finding to note.

Copy the six secrets from section 1.4 **verbatim**. `Security__SecretProtectionKey` in particular:
a different value there makes every mailbox row undecryptable on the scratch service and you will
mis-diagnose it as data loss.

**Time from "create service" to first green deploy. That is the second real RTO number.**

### Step 5 — verify the application layer

```bash
export DRILL_HOST="https://nexora-restore-drill.onrender.com"

# 5a. Liveness — database reachable through the request path.
curl -sS "$DRILL_HOST/health"          # expect: Healthy

# 5b. Readiness — ALL 11 CHECKS. Anything but "Healthy" on any of them fails the drill.
curl -sS "$DRILL_HOST/ready" | python3 -m json.tool
```

The eleven, and what each proves here:

| # | Check | What a green means for the restore |
|---|---|---|
| 1 | `database` | the branch is reachable through the runtime role |
| 2 | `evidence-storage` | **B2 reachable, versioning `Enabled`, and a write→read→re-hash→delete round trip passed** |
| 3 | `storage-capacity` | the scratch volume is writable |
| 4 | `malware-scanner` | inspector loaded (BuiltIn on the drill service) |
| 5 | `extraction-worker` | claim loop running against the restored schema |
| 6 | `quote-delivery-worker` | as above |
| 7 | `procurement-dispatch-worker` | as above |
| 8 | `background-workers` | 8 workers beating |
| 9 | `email-poll-channel` | **expect this to name a mailbox only if the credentials decrypted** — a green here is a live proof that `Security__SecretProtectionKey` is correct |
| 10 | `ocr-engine` | Tesseract + pdfium loaded |
| 11 | `outbound-email` | registered by `NotificationsServiceCollectionExtensions`; on `console` it reports sending to the log |

```bash
# 5c. Deployment identity — prove which revision answered.
curl -sS "$DRILL_HOST/build-identity"
```

### Step 6 — open one lead and fetch its evidence document

This is the step that exercises the object store end to end, and it is the one that cannot be
faked. Sign in to the scratch service as a tenant user of BU 7 (Noor Sons LLC), obtain a bearer
token, then:

```bash
# 6a. Pick a lead and a document from the RESTORED database.
psql "$DRILL_URL" -c \
  "SELECT id, business_unit_id, content_hash, byte_size, object_key
     FROM source_documents
    WHERE business_unit_id = 7 AND object_bucket = 'NexoraBucket'
    ORDER BY id DESC LIMIT 1;"
export DOC_ID=<id>; export DOC_SHA=<content_hash>; export DOC_BYTES=<byte_size>

# 6b. Open the lead through the API (proves RLS + query filters + the tenant token together).
curl -sS -H "Authorization: Bearer $TOKEN" "$DRILL_HOST/api/Lead/633"    # BU 7's newest lead id

# 6c. Fetch the evidence document. This route calls OpenVerifiedReadAsync, which re-hashes the
#     stream on the way out — so a 200 is ALREADY a hash proof by the application.
curl -sS -H "Authorization: Bearer $TOKEN" \
  "$DRILL_HOST/api/File/source-document/$DOC_ID" -o /tmp/drill-evidence.bin -w '%{http_code}\n'
```

**Verifying the hash yourself — three values that must all agree.** The keys are content-addressed
(`Evidence/tenants/{bu}/cleared/sha256/{first2}/{full-sha256}`), so the object's own path carries
its digest:

```bash
# (i) the bytes you just downloaded
shasum -a 256 /tmp/drill-evidence.bin | awk '{print $1}'
# (ii) what the restored database recorded at intake
echo "$DOC_SHA"
# (iii) the last path segment of the object key, which the writer derived from the bytes
psql "$DRILL_URL" -tAc \
  "SELECT split_part(object_key,'/',7) FROM source_documents WHERE id = $DOC_ID;"
# and the size:
stat -f%z /tmp/drill-evidence.bin ; echo "$DOC_BYTES"
```

**All three digests identical, and the byte count matching, is the proof that a restored evidence
object is byte-for-byte the object that was ingested.** Any two agreeing and the third differing
tells you exactly which layer lied: (i)≠(ii) is corruption in transit or at rest, (ii)≠(iii) is a
database restore that landed on the wrong point in time, (i)=(ii)≠(iii) is impossible unless
someone rewrote a key.

Repeat for at least one BU 1 document (`object_bucket = 'local'`) **only if** step 0 proved those
files still exist. Expect it to fail on the scratch service, which has no copy of the volume — and
record that as the finding it is.

### Step 7 — tear down

```bash
# 7a. [DASHBOARD] Render -> nexora-restore-drill -> Settings -> Delete Service.
#     Delete the SERVICE. Do not touch srv-d9csjhe1a83c739phue0.

# 7b. Delete the Neon branch (leaves `main` untouched).
curl -sS -X DELETE -H "Authorization: Bearer $NEON_API_KEY" \
  "https://console.neon.tech/api/v2/projects/<project-id>/branches/<branch-id>"
#     or [DASHBOARD] Neon -> Branches -> drill-YYYYMMDD -> Delete.

# 7c. Clean the probe objects the drill added to the production bucket. There is no tool for
#     this. List them and decide:
#     aws s3api list-object-versions --bucket NexoraBucket --prefix _readiness/ \
#       --endpoint-url https://s3.us-east-005.backblazeb2.com
#     (Deleting a VERSION is permanent. Do it only for _readiness/ keys.)

# 7d. Confirm production is untouched.
curl -sS https://nexora-fyjw.onrender.com/ready | python3 -c \
  'import json,sys; d=json.load(sys.stdin); print(d["status"], len(d["checks"]), "checks")'
cd /Users/zackkhan/Nexora/Nexora-main && .claude/prod-read.sh \
  "SELECT count(*) AS docs, md5(string_agg(content_hash, ',' ORDER BY id)) AS spine_digest
     FROM source_documents;"
```

---

## 4. The failure modes to expect

Every one of these is drawn from the code and will bite a restore specifically.

### 4.1 The app fails fast on missing configuration — and the failure is invisible in the app log

`Program.cs:75-105` throws **before the host is built** on: a missing or `__DB_`-placeholder
`ConnectionStrings:DefaultConnection`; a `Jwt:Key` that is missing, contains `__JWT_`, or is under
32 bytes; any of the three `CommercialFinance:*` secrets missing or under 32 bytes; and a
`Security:SecretProtectionKey` that is missing, not base64, not exactly 32 decoded bytes, or
all-zero (`Security/SecretProtection.cs:63-104`). `AddPlatformJwtBearer` throws separately when
`Jwt:PlatformKey` is absent **or equal to `Jwt__Key`**.

**Symptom during a restore:** the container exits during startup, the reason is on stdout, and
**nothing reaches the application log**. An operator watching the app log sees silence and concludes
the database is wrong. **Read the Render deploy log, not the service log.**

### 4.2 `Database__ApplyMigrationsOnStartup` will migrate your restored database

`Program.cs:992-1014`: the default is `app.Environment.IsProduction()`, and production sets it
`"true"` explicitly. **A scratch service booting against a restored branch will apply migrations to
that branch on first boot.**

Two ways that ruins a drill:

* If the image is *newer* than the restore point, the restore is silently schema-upgraded and you
  are no longer testing the restore — you are testing the restore plus a migration. Your row counts
  can still match while the schema no longer does.
* If a migration fails, the service will not start, on a database with no rollback, and **the image
  cannot be rolled back to a schema that no longer exists** (`render.yaml`'s own warning). On a
  drill branch that is survivable. On a real recovery it is a second outage inside the first.

**Set `Database__ApplyMigrationsOnStartup=false` on the drill service, and deploy the exact commit
SHA that was live at the restore point** — read it from `GET /build-identity` before you start.

### 4.3 The runtime/migration role split will refuse to start

`render.yaml` sets `Database__AllowManagedOwnerRoleMigrationCompatibility=true`. `Program.cs:999`
then **throws** unless `ConnectionStrings:MigrationConnection` is supplied *separately*:

> `Managed owner migration compatibility requires an explicit ConnectionStrings:MigrationConnection
> separate from the least-privilege runtime connection.`

Populating only `DefaultConnection` produces a deploy that fails at boot with no request served.
Note that this fires **even when `ApplyMigrationsOnStartup` is false** — the check is on the
compatibility flag, not on whether migrations run. Supply both, or clear the compatibility flag too.

Three further things open connections with `migrationConnection` regardless: the mailbox-credential
backfill, `SyncFinanceProviderSecretsAsync` (which **writes** to `FinanceProviderSecrets`), and
nothing else. **`SyncFinanceProviderSecretsAsync` writes on every boot** — so a drill service booted
with placeholder finance secrets will overwrite those rows *on the restored branch*. Harmless on a
branch; catastrophic if anyone ever points a misconfigured service at production.

### 4.4 RLS execution roles must exist on the restored database — and `pg_dump` does not carry them

The comment at the top of `MigrationsBaseline/Sql/00_execution_roles.sql` is the warning:

> *"pg_dump never emits roles (they are cluster-scoped, not database-scoped) and every GRANT and
> every `TO nexora_tenant_app` policy below depends on them existing."*

**A Neon branch carries the roles. A logical dump/restore does not.** If a future recovery ever goes
through `pg_dump`/`pg_restore` instead of a branch, every `GRANT` and every RLS policy referencing
these roles fails to restore, and the symptom is not "no isolation" — it is
**`42501: permission denied`** on every query, because PostgreSQL evaluates the grant check *before*
any row predicate. A policy with no grant is a table nobody can read.

Then, in Production only, `ValidateRuntimeDatabaseRoleAsync` (`Program.cs:1062-1099`) asserts the
whole shape before serving traffic: the login role NOINHERIT, non-superuser, **non-BYPASSRLS**, a
member of all three execution roles; `nexora_identity_app` and `nexora_pipeline_app` NOLOGIN
NOINHERIT **BYPASSRLS**; `nexora_tenant_app` NOBYPASSRLS; and **no `nexora_ai_maintenance` role
present**. Any deviation throws.

`TenantAccessGrantContract.AssertReadableAsync` then runs in every environment and throws if the
tenant plane cannot read the columns tenant-status and plan limits resolve from. It **no-ops when
the roles are absent**, logging *"the tenant plane is not role-separated here"* — so a role-less
restore does not fail here, it fails later and less clearly. Watch for that log line: on a restored
database it means the roles did not come across.

### 4.5 The non-pooler host — `SET ROLE` versus `SET LOCAL ROLE`

Two different things are often conflated. Get this right or you will chase a phantom grants problem:

* **The application is pooler-safe.** `TenantRlsCommandInterceptor:351` issues
  `SET LOCAL ROLE <role>` inside a transaction, which survives PgBouncer transaction mode by design.
* **The operator tooling is not.** `.claude/prod-read.sh` issues a session-level `SET ROLE`, which
  PgBouncer in transaction mode silently discards between statements. The script therefore rewrites
  `-pooler.` out of the host itself. **Do the same for every psql session in the drill.**
* **Migrations and DDL are not.** `DEPLOYMENT.md` and `NEON-SETUP.md` both require the **direct**
  endpoint; `Program.cs:1039` even has `ResolveDirectMigrationConnection`, which strips `-pooler.`
  from the runtime string when no explicit migration connection is given.
* Production's `DefaultConnection` already uses the direct endpoint. The pooled endpoint would
  additionally need `Max Auto Prepare=0` (ADR-0005 Ph2), which is not configured.

**Rule for the drill: never use a `-pooler` host, anywhere.** A silently-discarded `SET ROLE` shows
up as `permission denied` on tables whose grants are perfectly correct.

### 4.6 The others

* **`PlatformOwnerSeeder` on an empty platform schema.** `Platform__BootstrapOwnerEmail` /
  `Platform__BootstrapOwnerPassword` are unset in production. A restore onto an empty platform
  schema with them unset creates **no** platform user and **logs nothing** — the console is
  unreachable, no tenant can be provisioned, and `/health` is green throughout. Do not "fix" it with
  `DemoUser__Enabled` + `PlatformOwner__*`: `DemoUserSeeder` **refuses to run** under
  `ASPNETCORE_ENVIRONMENT=Production`, logs an error, and creates nothing.
* **`Storage__EnforcePersistentMount=true` with no disk.** `LocalFileStorage` write-probes the mount
  in its constructor and **refuses to boot** if it is missing. Correct, loud, and exactly what
  happens if you copy production's env wholesale onto a diskless scratch service. Section 3 step 4
  handles it.
* **The bucket name.** `NexoraBucket`, mixed case, ordinal comparison. A lower-cased "fix" breaks
  every read while every write keeps succeeding, and surfaces as *"the stored bytes no longer match
  the hash recorded at intake"* on documents that are perfectly intact.
* **Deploys are outages.** 1 instance, disk attached — a service with a disk cannot be replaced
  zero-downtime. Every restore step that redeploys production is downtime.
* **`autoDeployTrigger: checksPass`.** During a recovery, a push to `main` can deploy over your
  hand-set configuration once CI goes green. Consider suspending auto-deploy for the duration.

---

## 5. Pre-flight checklist and results

### Before the drill — tick every box or do not start

- [ ] The six boot secrets (1.4) are in a password manager, **verified by reading them back**
- [ ] A read-only Neon API key exists, or a console session is open
- [ ] The Neon history-retention window has been read from the console and written down
- [ ] The Render API key reads, and `GET /v1/services/srv-d9csjhe1a83c739phue0` returns 200
- [ ] Production `/ready` is 11/11 Healthy **before** the drill (baseline)
- [ ] The step-0 baseline and `spine_digest` are captured and saved
- [ ] The commit SHA live at the restore point is recorded from `GET /build-identity`
- [ ] Someone other than the drill operator knows the drill is running
- [ ] `Notifications__Provider=console` is confirmed on the drill service **before** first boot
- [ ] The drill service has **no** disk and `Storage__EnforcePersistentMount=false`
- [ ] `Database__ApplyMigrationsOnStartup=false` on the drill service

### Before declaring the drill passed — every one must be a yes

- [ ] Neon branch created at a chosen point in time, and the time it took is recorded
- [ ] All five `nexora_*` roles present on the branch, with the exact attribute shape in 3a
- [ ] RLS policy count on the branch matches production
- [ ] Row counts for the twelve named tables match the step-0 baseline
- [ ] `spine_digest` on the branch equals the production digest
- [ ] `Email_Configurations`: 10 rows, 10 in the `v1:` envelope
- [ ] Scratch service deployed green, and the deploy duration is recorded
- [ ] `/health` = `Healthy`
- [ ] **`/ready` = Healthy on all 11 checks, each name confirmed individually**
- [ ] `/build-identity` reports the intended SHA
- [ ] A lead opened through the API as a BU 7 tenant user
- [ ] An evidence document fetched, HTTP 200
- [ ] **All three SHA-256 values agree** (downloaded bytes, `content_hash`, object-key segment) and the byte count matches
- [ ] The fate of the 96 `object_bucket='local'` documents is established, either way
- [ ] Scratch Render service deleted
- [ ] Neon drill branch deleted
- [ ] `_readiness/` probe objects reviewed
- [ ] Production `/ready` still 11/11 and `spine_digest` unchanged

### Results — fill in on the day

| Measurement | Target | Measured | Notes |
|---|---|---|---|
| Drill date / operator | — | ________ | |
| **Neon history retention (the answer)** | — | ________ h | from the console, not inferred |
| Restore point chosen (`$DRILL_T0`) | — | ________ | |
| **RPO, database** | 24 h | ________ | = now − furthest reachable restore point |
| **RPO, Render disk** | 24 h | 24 h 12 m *(observed, pre-drill)* | longest inter-snapshot interval |
| **RPO, B2 evidence** | 24 h | 0 / ∞ *(pre-drill)* | 0 for overwrite; ∞ for a version delete |
| **RPO, secrets** | 24 h | ∞ *(pre-drill)* | no backup exists |
| Time: decision → branch created | — | ________ | |
| Time: branch → scratch service green | — | ________ | |
| Time: green → all assertions passed | — | ________ | |
| **RTO, total (measured)** | 8 h | ________ | sum of the three above + detection |
| Row counts matched? | all | ________ | |
| `spine_digest` matched? | yes | ________ | |
| `/ready` checks green | 11 / 11 | ____ / 11 | |
| Hash triple-agreement | yes | ________ | |
| Legacy `local` documents still present? | — | ________ | 96 rows; never previously checked |
| Production unchanged after teardown? | yes | ________ | |
| **Drill verdict** | PASS | ________ | |

---

## 6. What this runbook does NOT cover — and who must decide it

**Engineering cannot close any of these. Each needs an owner, money, or a signature.**

| Gap | Why it is not in here | Who decides |
|---|---|---|
| **Backing up the six boot secrets** | There is nowhere to put them. No secret manager is provisioned for this project. This is the highest-value, lowest-cost item on the page. | **Owner + a password manager. Today.** |
| **A copy of the database outside Neon** | Neon PITR protects against *our* mistakes, not against losing Neon. No scheduled `pg_dump` to independent storage exists, and no destination for one has been chosen. | Budget + a destination |
| **A second copy of the B2 evidence** | Versioning protects against overwrite, not against losing the bucket or the account. No replication is configured. | Budget |
| **B2 lifecycle rules and Object Lock** | Nothing expires the noncurrent versions and delete markers that every `/ready` call adds, and nothing prevents a permanent version delete by a leaked key. | Owner (console change, no code) |
| **Recovering a permanently deleted B2 version** | There is no path. Deleting a specific version frees the bytes; they are gone. | Accept, or fund Object Lock |
| **Restoring the Render disk without destroying it** | Render restores a snapshot **in place**. There is no "restore to a new disk", so a real disk recovery is destructive and untestable without downtime. | Accept, or finish the S3 cutover so the disk stops mattering |
| **Finishing the legacy-evidence cutover** | The four-step removal order is in `render.yaml`'s `disk:` block and is not started (`RouteLegacyWritersToObjectStore` and `LegacyMigration__Enabled` are both unset). Completing it deletes this entire risk class. | Engineering, once scheduled |
| **Alerting** | Nothing pages a human on data loss, on a failed snapshot, or on `/ready` going red. Detection time is the largest unmeasured term in the RTO. `Observability__Prometheus__Enabled=false` and no OTLP collector is configured. | Owner + a monitoring spend |
| **Whether 24 h RPO / 8 h RTO are the right targets** | They appear in the readiness doc with no derivation and no customer contract behind them. | Commercial owner |
| **Saudi PDPL residency of the backups** | The Neon project is `us-east-1`; the Render service is `oregon`; snapshots and object versions inherit those regions. Gate 9 item 1 is unresolved and the backups are inside it. | Legal + the residency decision |
| **The `NAS-1001` boundary claim** | One tenant carries a backup-policy reference that asserts nothing, `Registered` and never `Verified`. Correcting it is a *contractual* statement to a customer, not a config change. | Commercial owner |
| **A restore of the ClamAV pserv, Vercel, or GitHub** | Stateless, or owned elsewhere. Vercel rebuilds from git; `VITE_API_BASE_URL` is baked at build time and must be re-pointed if the backend URL changes. | — |

**The single decision that unblocks the most:** name an owner for the six boot secrets and have them
stored before the drill runs. Everything else on this page degrades gracefully. That one does not.
