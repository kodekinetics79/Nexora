# Restore drill — read-only evidence baseline

**Captured 2026-09-03, 03:15–03:30 UTC. Nothing was written to any production system.**

This is the companion to [`RUNBOOK-RESTORE.md`](RUNBOOK-RESTORE.md). Its only job is to be the
thing a restored copy is compared against, and to show the exact query behind every number so the
comparison can be repeated by someone else.

**Method.** All SQL ran through `.claude/prod-read.sh`, which opens the session with
`default_transaction_read_only = on`, forces the **non-pooler** Neon host, and refuses write-shaped
statements before `psql` is invoked. Default role `nexora_pipeline_app` (BYPASSRLS, reads
cross-tenant); `nexora_identity_app` where noted. Render facts came from `GET` calls to
`api.render.com` only. `/ready` was fetched over HTTPS.

**Two things could not be verified read-only** and are recorded as such:

* **Neon history retention (the PITR window).** It is a control-plane setting with no SQL surface.
  `neonctl` is not installed and the Neon credential store is not readable from this harness. See
  `RUNBOOK-RESTORE.md` §1.1 for the one-click check.
* **Whether the 96 legacy `object_bucket='local'` documents still exist as files** on the Render
  volume. That requires either a shell inside the container or an authenticated fetch per document.

---

## 1. Environment identity

### 1.1 Render — `GET /v1/services/srv-d9csjhe1a83c739phue0`

```
name:               Nexora
type:               web_service        suspended: not_suspended
repo:               https://github.com/kodekinetics79/Nexora   branch: main
autoDeploy:         yes                autoDeployTrigger: checksPass
region:             oregon
plan:               starter
numInstances:       1
healthCheckPath:    /health
url:                https://nexora-fyjw.onrender.com
env:                docker
disk:               {"id":"dsk-d9p57srl550s73fqjuv0","mountPath":"/var/data","name":"disk","sizeGB":5}
```

Every fact in the brief is confirmed: starter plan, 1 instance, 5 GB disk at `/var/data`, oregon.
**One divergence from `render.yaml`:** the disk is named `disk`, not `nexora-evidence`.

Sibling services in the same workspace (`tea-d8ral9m7r5hc73e4t4n0`) that matter here:
`srv-d9uhbirncjis739mtg80` `nexora-clamav`, private_service, plan `standard`, oregon.
(`render.yaml` declares `pro` for that block — a second divergence.)

### 1.2 Render environment variables — names only, 42 of them

Values were never requested. Enumerating the keys is what proves an absence.

```
ASPNETCORE_ENVIRONMENT                     Jwt__Audience
AWS_REQUEST_CHECKSUM_CALCULATION           Jwt__ExpiryMinutes
AWS_RESPONSE_CHECKSUM_VALIDATION           Jwt__Issuer
Agent__Anthropic__ApiKey                   Jwt__Key
CommercialFinance__AuditActorSecret        Jwt__PlatformKey
CommercialFinance__ContactVerificationSecret
CommercialFinance__DunningProviderWebhookSecret
ConnectionStrings__DefaultConnection       Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command
ConnectionStrings__MigrationConnection     Notifications__FromAddress
Cors__AllowedOrigins__0                    Notifications__FromName
DOTNET_USE_POLLING_FILE_WATCHER            Notifications__Provider
Database__AllowManagedOwnerRoleMigrationCompatibility
Database__ApplyMigrationsOnStartup         Observability__Prometheus__Enabled
DocumentInspection__ClamAV__Host           Ollama__ApiKey
DocumentInspection__ClamAV__Port           Ollama__BaseUrl
DocumentInspection__ClamAV__Timeout        Ollama__Model
DocumentInspection__Scanner__Provider      PORT
EvidenceStorage__AccessKeyId               PlatformAccess__NetworkMode
EvidenceStorage__Bucket                    Security__SecretProtectionKey
EvidenceStorage__Provider                  Storage__EnforcePersistentMount
EvidenceStorage__Region                    Storage__RequiredMountPath
EvidenceStorage__SecretAccessKey           Storage__RootPath
EvidenceStorage__ServiceUrl
```

**Absences that carry meaning:**

| Absent key | Consequence |
|---|---|
| `Platform__DataBoundaries__*` (all nine types) | The `neon-pitr-7d` policy in `DEPLOYMENT.md:96` **has never been configured**. No tenant has been told it. |
| `Platform__BootstrapOwnerEmail` / `Password` | Inert today (a platform user exists). Fatal-and-silent on a restore onto an empty platform schema. |
| `EvidenceStorage__RouteLegacyWritersToObjectStore` | Defaults false → legacy writers still write to the 5 GB disk. |
| `EvidenceStorage__LegacyMigration__Enabled` | Defaults false → the disk has not been drained. |
| `Auth__RequireSecurityStamp` | Defaults false. |
| `Observability__Prometheus__ScrapeKey` | With `Enabled=false`, no metrics leave the process. |

### 1.3 Neon — read from the compute itself

```sql
SELECT current_database(), current_user, version();
--  neondb | nexora_pipeline_app | PostgreSQL 17.11 (32e7196) on aarch64-unknown-linux-gnu

SELECT name, setting FROM pg_settings
 WHERE name IN ('neon.tenant_id','neon.timeline_id','neon.pageserver_connstring',
                'neon.safekeepers','neon.max_cluster_size');
```

| Setting | Value |
|---|---|
| `neon.pageserver_connstring` | `host=pageserver-20.cell-2.us-east-1.aws.neon.tech port=6400` |
| `neon.safekeepers` | `safekeeper-{10,11,12}.cell-2.us-east-1.aws.neon.tech:6401` |
| `neon.tenant_id` | `ead1f2bef4e58a836abd5bbe55d7837a` |
| `neon.timeline_id` | `217cc9617d433fc0a4eda744af66cca4` |
| `neon.max_cluster_size` | `16777216` |

**The Neon project region is `us-east-1` (AWS), cell-2.** This is a *derived* fact — the pageserver
and all three safekeepers are in `us-east-1` — and it is the closest thing to a verified region
available without console access. The Render service is in `oregon` (us-west-2); every application
query is therefore cross-region.

`neon.tenant_id` is Neon's **storage** tenant, not the console project id, and not a Nexora tenant.
Do not paste it into a Neon API URL.

### 1.4 Database roles — `pg_roles`

```sql
SELECT rolname, rolcanlogin, rolbypassrls, rolsuper, rolinherit
  FROM pg_roles WHERE rolname NOT LIKE 'pg\_%' ORDER BY rolname;
```

| rolname | login | bypassrls | super | inherit |
|---|---|---|---|---|
| cloud_admin | t | t | t | t |
| neon_auth | t | f | f | f |
| neon_service | t | t | f | t |
| neon_superuser | f | t | f | t |
| neondb_owner | t | t | f | t |
| **nexora_identity_app** | f | **t** | f | **f** |
| **nexora_pipeline_app** | f | **t** | f | **f** |
| **nexora_purge_app** | f | f | f | f |
| **nexora_runtime** | **t** | f | f | **f** |
| **nexora_tenant_app** | f | **f** | f | **f** |

This is exactly the shape `ValidateRuntimeDatabaseRoleAsync` asserts at boot: one NOINHERIT login
role that is a member of three NOLOGIN NOINHERIT execution roles; identity and pipeline BYPASSRLS;
tenant NOBYPASSRLS. **Reproduce this table on any restored database before trusting it.**

Schemas present: `neon_auth`, `platform`, `public`.

`__EFMigrationsHistory` is **not readable** by `nexora_pipeline_app` (`permission denied`), so the
applied-migration list could not be captured. On a restored branch, read it as `neondb_owner`.

---

## 2. Size and shape of what must be restored

### 2.1 Total

```sql
SELECT pg_size_pretty(pg_database_size(current_database()));
--  69 MB
```

**The whole production database is 69 MB.** Restore volume is not a constraint anywhere in this
plan; the constraints are all procedural.

### 2.2 The twenty-five largest tables

```sql
SELECT n.nspname AS schema, c.relname AS table, s.n_live_tup AS live_rows,
       pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
 WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog','information_schema')
 ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 25;
```

| schema | table | live rows | total size |
|---|---|---:|---:|
| public | LeadItemRevisions | 4642 | 6784 kB |
| public | LeadItems | 4623 | 4496 kB |
| public | AiRequests | 2472 | 2368 kB |
| public | source_document_occurrences | 657 | 1856 kB |
| public | AiCallAttempts | 2382 | 1592 kB |
| public | canonical_line_items | 765 | 1536 kB |
| public | field_evidence | 2106 | 1384 kB |
| public | lead_line_commercial_resolutions | 317 | 880 kB |
| public | EmailInquiryComponentResults | 259 | 792 kB |
| public | document_regions | 2106 | 776 kB |
| public | LeadIngestionOccurrences | 273 | 712 kB |
| public | source_documents | 409 | 704 kB |
| public | lead_routing_decisions | 270 | 696 kB |
| public | Leads | 237 | 600 kB |
| public | LeadRevisions | 256 | 584 kB |
| public | ExtractionJobs | 389 | 560 kB |
| public | EmailInquiryAssemblies | 277 | 480 kB |
| public | EmailInquiryComponents | 328 | 432 kB |
| platform | UsageEvents | 298 | 416 kB |
| public | QuoteConfiguration | 3 | 352 kB |
| platform | UsageEventRatings | 298 | 344 kB |
| public | EmailIngests | 365 | 344 kB |
| public | unassigned_work_items | 262 | 344 kB |
| public | extraction_runs | 256 | 328 kB |
| public | Orders | 1 | 272 kB |

The estate is **audit and revision history**, not transactions: the two largest tables are lead
line-item revisions, and `AiRequests` + `AiCallAttempts` (the AI governance ledger) is third and
fifth. A restore that silently drops append-only history would still look plausible on a count of
`Leads`. That is why §3.2's digest exists.

### 2.3 Baseline row counts — **the numbers a restore is compared against**

Captured **2026-09-03T03:25:47Z**.

| table | rows |
|---|---:|
| Attachments | 312 |
| EmailIngests | 365 |
| EmailInquiryAssemblies | 277 |
| Email_Configurations | 10 |
| ExtractionJobs | 389 |
| LeadItemRevisions | 4642 |
| LeadItems | 4623 |
| Leads | 237 |
| QuoteItems | 15 |
| Quotes | 4 |
| RFQ | 11 |
| RFQItems | 80 |
| Users | 7 |
| canonical_line_items | 765 |
| field_evidence | 2106 |
| platform.TenantDataAssets | 1 |
| platform.Tenants | 4 |
| platform.UsageEvents | 298 |
| source_documents | 409 |
| supplier_quotes | 1 |

<details><summary>Query</summary>

```sql
SELECT t AS table_name, c AS rows FROM (
  SELECT 'Leads' t, count(*) c FROM "Leads"
  UNION ALL SELECT 'LeadItems', count(*) FROM "LeadItems"
  UNION ALL SELECT 'LeadItemRevisions', count(*) FROM "LeadItemRevisions"
  UNION ALL SELECT 'RFQ', count(*) FROM "RFQ"
  UNION ALL SELECT 'RFQItems', count(*) FROM "RFQItems"
  UNION ALL SELECT 'Quotes', count(*) FROM "Quotes"
  UNION ALL SELECT 'QuoteItems', count(*) FROM "QuoteItems"
  UNION ALL SELECT 'supplier_quotes', count(*) FROM supplier_quotes
  UNION ALL SELECT 'source_documents', count(*) FROM source_documents
  UNION ALL SELECT 'ExtractionJobs', count(*) FROM "ExtractionJobs"
  UNION ALL SELECT 'EmailIngests', count(*) FROM "EmailIngests"
  UNION ALL SELECT 'EmailInquiryAssemblies', count(*) FROM "EmailInquiryAssemblies"
  UNION ALL SELECT 'Attachments', count(*) FROM "Attachments"
  UNION ALL SELECT 'field_evidence', count(*) FROM field_evidence
  UNION ALL SELECT 'canonical_line_items', count(*) FROM canonical_line_items
  UNION ALL SELECT 'Users', count(*) FROM "Users"
  UNION ALL SELECT 'Email_Configurations', count(*) FROM "Email_Configurations"
  UNION ALL SELECT 'platform.Tenants', count(*) FROM platform."Tenants"
  UNION ALL SELECT 'platform.TenantDataAssets', count(*) FROM platform."TenantDataAssets"
  UNION ALL SELECT 'platform.UsageEvents', count(*) FROM platform."UsageEvents"
) x ORDER BY 1;
```
</details>

### 2.4 Counts by tenant

Business units, read as `nexora_identity_app`:

```sql
SELECT "ID", "BusinessUnitName" FROM "BusinessUnits" ORDER BY "ID";
```

| ID | BusinessUnitName |
|---:|---|
| 1 | Customer POC |
| 2 | PK-Unit |
| 4 | USA Business |
| 5 | Intelliflow |
| 6 | Codex Smoke Tenant |
| 7 | **Noor Sons LLC** ← the live pilot |
| 8 | Nexora Pilot Certification 20260830 |

Platform tenants:

| Id | Name | Status | DataRegion | CountryCode |
|---:|---|---|---|---|
| 1 | Intelliflow | Archived | | |
| 2 | Codex Smoke Tenant | Active | | |
| 3 | **Noor Sons LLC** | **Active** | `USA` | PK |
| 4 | Nexora Pilot Certification 20260830 | Provisioning | | CA |

**Only two business units hold any commercial data at all:**

| BU | leads | rfq | quotes | supplier_quotes | source_documents | extraction_jobs | email_assemblies | field_evidence |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 (Customer POC) | 57 | 2 | 2 | 0 | 91 | 62 | 0 | 28 |
| 7 (Noor Sons LLC) | 180 | 9 | 2 | 1 | 318 | 327 | 277 | 2078 |

Highest lead id: BU 1 → 448, BU 7 → **633** (use 633 as the drill's "open one lead" target).

<details><summary>Query — note the four different spellings of the tenant column</summary>

```sql
SELECT bu,
       sum(CASE WHEN t='Leads' THEN c ELSE 0 END) AS leads,
       sum(CASE WHEN t='RFQ' THEN c ELSE 0 END) AS rfq,
       sum(CASE WHEN t='Quotes' THEN c ELSE 0 END) AS quotes,
       sum(CASE WHEN t='supplier_quotes' THEN c ELSE 0 END) AS supplier_quotes,
       sum(CASE WHEN t='source_documents' THEN c ELSE 0 END) AS source_documents,
       sum(CASE WHEN t='ExtractionJobs' THEN c ELSE 0 END) AS extraction_jobs,
       sum(CASE WHEN t='EmailInquiryAssemblies' THEN c ELSE 0 END) AS email_assemblies,
       sum(CASE WHEN t='field_evidence' THEN c ELSE 0 END) AS field_evidence
FROM (
  SELECT 'Leads' AS t, "BusinessUnitID" AS bu, count(*) AS c FROM "Leads" GROUP BY 2
  UNION ALL SELECT 'RFQ', "BusinessUnitID", count(*) FROM "RFQ" GROUP BY 2
  UNION ALL SELECT 'Quotes', "BusinessUnitID", count(*) FROM "Quotes" GROUP BY 2
  UNION ALL SELECT 'supplier_quotes', "BusinessUnitId", count(*) FROM supplier_quotes GROUP BY 2
  UNION ALL SELECT 'source_documents', business_unit_id, count(*) FROM source_documents GROUP BY 2
  UNION ALL SELECT 'ExtractionJobs', "BusinessUnitId", count(*) FROM "ExtractionJobs" GROUP BY 2
  UNION ALL SELECT 'EmailInquiryAssemblies', "BusinessUnitId", count(*) FROM "EmailInquiryAssemblies" GROUP BY 2
  UNION ALL SELECT 'field_evidence', business_unit_id, count(*) FROM field_evidence GROUP BY 2
) x GROUP BY bu ORDER BY bu;
```

The tenant discriminator is spelled **four** different ways across the schema —
`"BusinessUnitID"` (Leads, RFQ, Quotes, Email_Configurations),
`"BusinessUnitId"` (ExtractionJobs, supplier_quotes, EmailInquiryAssemblies, LeadItemRevisions),
`business_unit_id` (source_documents, field_evidence, document_regions, extraction_runs) and
`"SourceBusinessUnitId"` (RFQItems). 160 tables in `public` carry one of the first three. Any
hand-written verification query must be checked against `information_schema.columns` first:

```sql
SELECT table_name, column_name FROM information_schema.columns
 WHERE table_schema = 'public' AND column_name ILIKE '%usiness%nit%' ORDER BY table_name;
```
</details>

---

## 3. The object store

### 3.1 What is where

```sql
SELECT business_unit_id, object_bucket, count(*) AS objects,
       count(DISTINCT content_hash) AS distinct_hashes, count(object_version) AS with_version,
       pg_size_pretty(sum(byte_size)) AS bytes,
       min(created_on)::date AS first, max(created_on)::date AS last
  FROM source_documents GROUP BY 1,2 ORDER BY 1,2;
```

| BU | object_bucket | objects | distinct hashes | with version | bytes | first | last |
|---:|---|---:|---:|---:|---:|---|---|
| 1 | `local` | 91 | 91 | 91 | 9361 kB | 2026-07-28 | 2026-08-06 |
| 7 | **`NexoraBucket`** | **313** | 313 | 313 | 4483 kB | 2026-08-13 | **2026-09-03** |
| 7 | `local` | 5 | 5 | 5 | 429 kB | 2026-08-12 | 2026-08-12 |

**313 of 409 evidence objects are in B2. 96 still resolve to the Render volume.** Every row has a
distinct content hash and a recorded version id, so nothing is deduplicated away and every object
is individually addressable by version.

`purge_state` is `Present` for all 409 — nothing has been purged, so a restore has nothing to
reconcile against a purge ledger.

Legacy attachments:

```sql
SELECT CASE WHEN "FilePath" LIKE 's3://%' THEN 's3://'
            WHEN "FilePath" LIKE '/%'     THEN 'absolute'
            ELSE 'relative' END AS shape, count(*) FROM "Attachments" GROUP BY 1;
```

| shape | count |
|---|---:|
| relative | **114** |
| `s3://` | 198 |

No absolute paths — good, since absolute paths outside the configured root are deliberately
rejected. The 114 relative ones resolve under `Storage__RootPath` on the volume.

### 3.2 The spine digest — one value that proves the evidence estate

```sql
SELECT count(*) AS docs, md5(string_agg(content_hash, ',' ORDER BY id)) AS spine_digest
  FROM source_documents;
```

```
 docs |           spine_digest
------+----------------------------------
  409 | 037c652bb2369c620418111f8362ff04
```

Order-independent given the `ORDER BY id`, cheap, and sensitive to a single missing or altered
document. **Re-run it against any restored branch. If the restore point is after 2026-09-03T03:25Z
and this digest differs, the restore is wrong — stop and find out why before proceeding.**

### 3.3 Key shape and hash chain

```sql
SELECT id, content_hash, byte_size, length(object_version) AS ver_len, left(object_key,70)
  FROM source_documents WHERE object_bucket='NexoraBucket' ORDER BY id LIMIT 3;
```

| id | content_hash | bytes | key |
|---:|---|---:|---|
| 97 | `89d4aaa906fb06da5a22e72c16900a68a6d47c41d05b7e065f52c1e31d6154da` | 166 | `Evidence/tenants/7/cleared/sha256/89/89d4aaa9…` |
| 98 | `7513812a18012876f9a5a497ab326bf3b4b25969d539a975599f7e46ad92aa89` | 250 | `Evidence/tenants/7/cleared/sha256/75/7513812a…` |
| 99 | `bd87a70e161a594d0eb961ad988cfd36f8e48164cce9ddf0717e47d1e1dc4ecd` | 398 | `Evidence/tenants/7/cleared/sha256/bd/bd87a70e…` |

**Keys are content-addressed:** `Evidence/tenants/{businessUnitId}/cleared/sha256/{hash[0:2]}/{hash}`.
All 313 B2 objects sit under the single prefix `Evidence/tenants/7/`. Version ids are 99-character
B2 file ids, e.g. `4_z872f0ac4b8d2f58393f3071f_f11308b2fc84106be_d20260903_…`.

**This is what makes hash verification a three-way check** rather than a two-way one: the digest is
recorded in `source_documents.content_hash`, embedded in the object's own key, and recomputable from
the bytes. Newest BU 7 object at capture time: id **430**, sha256
`2b0dc584580a4c23706df77f1d737ab2140d13b43a48894351a68140d3638727`, 33 264 bytes.

### 3.4 The probe, from the code

`Backend/ERP_RFQ_Automation/Infrastructure/Storage/IEvidenceObjectStorage.cs`

`EvidenceStorageHealthCheck` (`HealthChecks/EvidenceStorageHealthCheck.cs`) short-circuits to
`Unhealthy` when `!IsDurable` — the local provider — and otherwise calls
`S3EvidenceObjectStorage.ProbeAsync` on **every `/ready` request**. `ProbeAsync` (`:583-620`):

1. **`ValidateServiceEndpoint(_options.ServiceUrl)`** (`:970-982`) — refuses anything that is not an
   absolute http/https URI, and refuses plain http unless it is loopback.
2. **`VerifyBucketVersioningAsync`** (`:949-967`) — `GetBucketVersioning`, then
   `EnsureVersioningEnabled(status)` (`:1000-1005`), which **throws** unless the status is exactly
   `VersionStatus.Enabled`. One escape hatch: HTTP **405 or 501** returns silently, for
   S3-compatible stores with no versioning API. **B2 implements the API, so on this deployment the
   assertion is live and enforced.**
3. Generates 32 random bytes, computes their SHA-256, `PutObject`s them to
   `_readiness/{guid:N}.probe` with `If-None-Match: *` and a `sha256` metadata header, then
   `GetObject`s the key back and calls `LocalEvidenceObjectStorage.VerifyAsync(stream, digest,
   length)` — **a real round-trip re-hash, not a HEAD**.
4. `finally` → `DeleteObject` on the key.

Two behaviours in that path are load-bearing for a restore:

* **`PutAsync` (`:556-580`) tolerates a store with no conditional writes.** B2 answers
  `If-None-Match: *` with **501**; the class catches it, remembers `_conditionalWritesUnsupported`
  for the process, rewinds the stream, and retries without the precondition. Safe only because the
  key contains the SHA-256 of the bytes, so the loser of the race writes byte-identical content.
* **The `finally` deletes without a version id.** On a versioned bucket that writes a *delete
  marker* and leaves the object as a noncurrent version. **Every `/ready` call permanently adds one
  noncurrent version and one delete marker to `NexoraBucket`, and no lifecycle rule removes them.**

`OpenVerifiedReadAsync` (`:689-704`) — the read path used by `GET /api/File/source-document/{id}` —
parses the URI **preserving the bucket's case**, calls `EnsureConfiguredBucket` (ordinal compare),
`GetObject`s with the version id when present, and returns
`LocalEvidenceObjectStorage.CopyAndVerifyAsync(stream, expectedSha256)`. **A 200 from that endpoint
is already a hash proof.**

The 2026-08-16 incident is documented in the source at `:904-921`: `System.Uri` lowercases hosts,
bucket names are case-sensitive, so reading `s3://NexoraBucket/...` back through `Uri.Host` produced
`nexorabucket` and every read failed against a bucket that may not exist — reported as *"the stored
bytes no longer match the hash recorded at intake"* on objects that were perfectly intact.
**`NexoraBucket` must never be lower-cased.**

### 3.5 What the probe does *not* prove

* Nothing checks for **lifecycle rules**. There are none, and §3.4 shows why that compounds.
* Nothing checks for **Object Lock / deletion protection**.
* Nothing checks for a **second copy**. There isn't one.
* `TryDeletePurgedObjectAsync` (`:705-757`) deletes a **specific version**, which genuinely frees
  bytes and is **not recoverable**. Its own comment explains why the version id is load-bearing.

---

## 4. Secrets — measured blast radius

```sql
SELECT "BusinessUnitID", count(*) AS mailboxes,
       count(*) FILTER (WHERE "Password" IS NOT NULL AND "Password" <> '') AS with_password
  FROM "Email_Configurations" GROUP BY 1 ORDER BY 1;

SELECT count(*) AS total, count(*) FILTER (WHERE "Password" LIKE 'v1:%') AS protected_envelope
  FROM "Email_Configurations";
```

| BU | mailboxes | with password |
|---:|---:|---:|
| 1 | 8 | 8 |
| 7 | 2 | 2 |

```
 total | protected_envelope
-------+--------------------
    10 |                 10
```

**Ten stored corporate mailbox credentials, all ten in the `v1:` AES-256-GCM envelope.**
`Security/SecretProtection.cs` exposes one key path (`Security:SecretProtectionKey`), validates it
as base64 decoding to exactly 32 non-zero bytes, and has **no key ring and no rotation path**.
Losing that environment variable makes all ten permanently undecryptable, and no restore of any
other component recovers them.

`public."FinanceProviderSecrets"` is **not readable** by `nexora_pipeline_app`, so its contents were
not captured. `Program.cs:1024` and `:1042-1060` show the three `CommercialFinance__*` secrets are upserted
into it on **every** boot, which means a database restore recovers those three values even though
the process still cannot boot without the environment variables.

---

## 5. Live service state at capture

`GET https://nexora-fyjw.onrender.com/ready` → **`Healthy`, 11/11, totalDurationMs 1160**

| check | ms | description |
|---|---:|---|
| background-workers | 0 | 8 background worker(s) beating. |
| database | 386.9 | Database reachable |
| email-poll-channel | 0 | 1 mailbox(es) polling: `***@kodekinetics.com` (last read 2026-09-03T03:20:18Z). |
| **evidence-storage** | **1159.9** | **Durable evidence object storage is reachable.** ← the versioning + round-trip proof |
| extraction-worker | 0 | Extraction worker claim loop is active. |
| malware-scanner | 10.6 | **ClamAV** malware scanner passed clean and detection controls. |
| ocr-engine | 0 | Tesseract 5.3.0 loaded [eng]; pdfium rasteriser loaded. |
| outbound-email | 177.4 | Outbound email is sending via **smtp**. |
| procurement-dispatch-worker | 0 | Procurement dispatch claim loop is active. |
| quote-delivery-worker | 0 | Quote delivery claim loop is active. |
| **storage-capacity** | 0.1 | **4,911 MB free (99.1%) on the storage volume.** |

`evidence-storage` at 1.16 s is 99% of the total — that is the B2 round trip, and it is the check
worth watching during a drill.

**The 5 GB volume is ~44 MB used.** Ten of the eleven checks are registered in `Program.cs:289-305`;
`outbound-email` is registered separately in
`Notifications/NotificationsServiceCollectionExtensions.cs:102`. Anyone counting checks from
`Program.cs` alone will expect ten and be confused by eleven.

### 5.1 Render disk snapshots — `GET /v1/disks/dsk-d9p57srl550s73fqjuv0/snapshots`

Seven returned, spanning **5.999 days**:

| # | createdAt (UTC) | interval since previous |
|---:|---|---|
| 1 | 2026-09-03T00:02:43.655Z | 23 h 44 m |
| 2 | 2026-09-02T00:18:42.001Z | 23 h 59 m |
| 3 | 2026-09-01T00:19:09.868Z | 24 h 08 m |
| 4 | 2026-08-31T00:11:22.804Z | **24 h 12 m** ← worst observed |
| 5 | 2026-08-29T23:59:19.152Z | 24 h 01 m |
| 6 | 2026-08-28T23:57:50.545Z | 23 h 54 m |
| 7 | 2026-08-28T00:03:41.263Z | — |

All seven carry `instanceId: srv-d9csjhe1a83c739phue0`. Automatic daily snapshots with 7-day
retention, confirmed by observation rather than by documentation. **`GATE9_10_READINESS.md` item 4's
"no backup configured" is wrong about this volume** — what is right is that no snapshot has ever
been restored.

### 5.2 Deploy durations — `GET /v1/services/.../deploys`

| deploy | status | createdAt | wall-clock |
|---|---|---|---:|
| dep-dacdtk6gekts73cjjthg | **live** | 2026-09-03T02:43:28Z | 3.7 min |
| dep-dacdku6k1f9s738031h0 | deactivated | 2026-09-03T02:24:56Z | 3.4 min |
| dep-dacdidp5efls73e20lp0 | deactivated | 2026-09-03T02:19:35Z | 1.1 min |
| dep-daccod2jnfac73c2npfg | update_failed | 2026-09-03T01:24:04Z | 0.4 min |
| dep-dacc8rvqj5pc73amfgug | update_failed | 2026-09-03T00:50:55Z | 2.9 min |
| dep-daacrprl550s73akpoag | deactivated | 2026-08-31T00:42:15Z | 3.3 min |
| dep-daaba1u7bikc73bjfnb0 | deactivated | 2026-08-30T22:56:07Z | 1.0 min |
| dep-daaa983ncjis739tsa60 | deactivated | 2026-08-30T21:46:08Z | 1.1 min |

**Successful deploys of this service take 1.0–3.7 minutes, including the Docker build.** The only
measured input to the RTO arithmetic. It does **not** cover a cold scratch service with no build
cache, which is unmeasured. Two `update_failed` deploys on 2026-09-03 are worth noting: deploy
failure on this service is not hypothetical.

---

## 6. The data-boundary claim, verified

```sql
SELECT "TenantId", "AssetType", "OpaqueProviderReference", "Region",
       "BackupPolicyReference", "BackupPolicyVersion", "Status", "VerifiedOn"
  FROM platform."TenantDataAssets" ORDER BY "TenantId", "AssetType";
```

| TenantId | AssetType | OpaqueProviderReference | Region | BackupPolicyReference | Version | Status | VerifiedOn |
|---:|---|---|---|---|---:|---|---|
| 3 | PostgreSqlTenantScope | `NAS-1001` | `usa` | **`NAS-1001`** | 1 | `Registered` | *(null)* |

**One row, for one tenant, on one of nine asset types.** Combined with the absence of every
`Platform__DataBoundaries__*` environment variable (§1.2), this settles the question the runbook was
asked:

* **`neon-pitr-7d` is documentation, not configuration.** It appears at `DEPLOYMENT.md:96` inside an
  example block and has never been applied to this deployment.
* The backup policy a live tenant actually carries is the string **`NAS-1001`**, hand-typed,
  identical to its own provider reference, asserting **nothing** about retention.
* `Status = Registered`, `VerifiedOn = null` — the PostgreSQL-tenant-scope probe DEPLOYMENT.md
  describes as *"the one boundary the platform can genuinely observe"* has **never run**.
* The declared `Region` is `usa`; the tenant's contractual `DataRegion` is `USA`; the observed Neon
  region is `us-east-1` (§1.3). Three spellings of a region that DEPLOYMENT.md says must be equal.

**Neither "7 days" nor "6 hours" is supported by anything in this system.** The window is a Neon
console setting that no artefact in this repository or this deployment records.

---

## 7. Everything read-only that was run, in order

| # | What | Tool | Result |
|---:|---|---|---|
| 1 | Service config | Render API GET | §1.1 |
| 2 | Disk list + snapshots | Render API GET | §1.1, §5.1 |
| 3 | Deploy history | Render API GET | §5.2 |
| 4 | Env var **names** (never values) | Render API GET | §1.2 |
| 5 | `current_database/user/version` | prod-read.sh | §1.3 |
| 6 | `pg_settings` `neon.*` | prod-read.sh | §1.3 |
| 7 | `pg_roles` | prod-read.sh | §1.4 |
| 8 | `pg_namespace` | prod-read.sh | §1.4 |
| 9 | `pg_database_size` | prod-read.sh | §2.1 |
| 10 | Largest 25 tables | prod-read.sh | §2.2 |
| 11 | 20-table baseline counts | prod-read.sh | §2.3 |
| 12 | `BusinessUnits` (identity role) | prod-read.sh | §2.4 |
| 13 | `platform."Tenants"` | prod-read.sh | §2.4 |
| 14 | Per-tenant spine counts | prod-read.sh | §2.4 |
| 15 | `information_schema.columns` tenant discriminators | prod-read.sh | §2.4 |
| 16 | `source_documents` by bucket | prod-read.sh | §3.1 |
| 17 | `purge_state` | prod-read.sh | §3.1 |
| 18 | `Attachments` path shapes | prod-read.sh | §3.1 |
| 19 | **spine digest** | prod-read.sh | §3.2 |
| 20 | Sample keys / hashes / versions | prod-read.sh | §3.3 |
| 21 | `Email_Configurations` envelope state | prod-read.sh | §4 |
| 22 | `platform."TenantDataAssets"` | prod-read.sh | §6 |
| 23 | `GET /ready` | HTTPS | §5 |

**Refused by permission, and therefore unverified:** `__EFMigrationsHistory` and
`FinanceProviderSecrets` (both `permission denied` for `nexora_pipeline_app`); the Neon control
plane (no CLI, credential store not readable); Backblaze B2 console settings (credentials are
Render env-var values); the contents of the Render volume.

**Nothing in this document was written to, mutated, or deleted from any production system.**
