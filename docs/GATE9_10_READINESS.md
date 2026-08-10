# Gates 9 and 10 — what is code, and what is yours

Gates 0 through 8 are engineering. **Gates 9 and 10 are mostly not.** This page separates the two so
nobody discovers at pilot that the remaining work needed a purchase order rather than a pull request.

Written under standing delegation (decisions R27 and R28). Everything here is stated so it can be
checked, not taken on trust.

---

## Gate 9 — Arabic/RTL, security, performance, availability, backup, recovery

### The code half — being completed

| Item | State |
|---|---|
| Tenant isolation: query filters, row-level security, grants on all tenant tables | Being closed by the consolidated migration; two permanent guards now exist — see below |
| The security remediation from three independent reviews | Landed: cross-tenant ingest predicate, worker tenant scoping, spend-cap denomination, FX approval maker-checker, forgeable audit attribution, impersonation allow-list |
| Transactional correctness under retry and multiple instances | Landed: execution-strategy wrapping, change-tracker clearing across 66 sites, SLA send-once with uncertain-not-resent semantics |
| Performance work measurable locally | Partially done; index coverage lands with the migration |
| **Arabic / RTL** | **Deferred by your decision R6.** The delivery note and quote PDF name it as a gap rather than half-rendering it |

**Two guards were added that make a whole class of defect impossible to ship again**, and they are
worth more than any single fix in this gate:

- `HasPendingModelChanges()` — catches a model change with no migration. `GetPendingMigrationsAsync`
  structurally cannot see this: it compares migrations *authored* against migrations *applied*, so a
  table living only in `OnModelCreating` is invisible to it. That is how an entire gate's schema
  reached a green build.
- **Row-level security without a grant** — the existing test only proved that tables *without* RLS
  hold no grant. Nothing proved the inverse. A policy with no grant is not a tighter boundary, it is
  a table nobody can read: PostgreSQL raises `42501` on the grant check before it evaluates any row
  predicate. Three tables shipped exactly that, and every test passed.

### The certification half — **blocked on you**, four items

None of these can be closed by writing code. Each needs money, a signature, or both.

#### 1. Saudi PDPL data residency — **cannot be met on the current stack at all**

`render.yaml` sets no region and defaults to US-Oregon; `vercel.json` sets none; the Neon region is
undocumented. **None of Render, Vercel or Neon offers a Saudi region.**

This is not a configuration change. It is either an accepted transfer mechanism with legal sign-off,
or a re-platforming programme onto a KSA-resident host — STC Cloud, Oracle Jeddah, or AWS Bahrain.
Both have lead time measured in weeks, and the second has real cost.

**Decide:** accept with a documented transfer mechanism, or fund the move. **Blocks the gate either
way, so deciding late is the expensive option.**

#### 2. Real malware scanning — **off, roughly $85/month**

The configured scanner is a structural inspector that, in its own words, *detects no real malware*,
and logs a reduced-security-posture warning on every boot. `render.yaml` marks it pre-signoff.

For a product whose primary input is customer-supplied documents arriving by email from outside the
company, this is the cheapest material risk reduction available anywhere in the build.

**Decide:** approve the spend and enable before pilot.

#### 3. 99.5% availability — **structurally unreachable as deployed**

A single Render instance with no `plan` and no `numInstances` means **every restart and every deploy
is downtime**. The application is already written to survive multiple instances — work claims,
advisory locks and send-once ledgers are all in place — so this is hosting spend, not rework.

**Decide:** approve multi-instance hosting and a communicated maintenance window, or renegotiate the
availability target to something the deployment can actually meet.

#### 4. Backup and tested restore — **none exists**

The 5 GB volume holding every source document has **no backup configured**, and the configuration
file itself states that losing the volume is unrecoverable data loss. There is no restore runbook and
no restore has ever been tested against the stated RPO of 24 hours and RTO of 8 hours.

An untested backup is not a backup. **This is the single largest unmanaged risk in the deployment**,
and it is larger than anything found in three security reviews, because every other finding is
recoverable and this one is not.

**Decide:** assign an owner, configure it, and schedule a **restore rehearsal**. The rehearsal is the
deliverable, not the backup job.

---

## Gate 10 — history migration, training, parallel run, pilot acceptance

**Staged, not attempted.** Simulating this would be worthless — the whole point of a parallel run is
that real people put real work through it.

### What is genuinely blocked, and on what

| Item | Blocked on |
|---|---|
| **Two-year quotation history load** | **E47.** Closing the commercial-case spine means the uploader now refuses any historical quotation with no Nexora RFQ behind it. That refusal is correct — a back-door that skips the case is how the spine rots. Gate 10 needs a governed import that lands the RFQ and the quotation **together, in one transaction, inheriting one case.** Needs your confirmation it is funded |
| **Master-data validation harness** | Not built. Needs the real customer, supplier and product extracts to validate against |
| **Parallel run** | A pilot environment with a live mailbox, real users, and a period where both systems run on the same work |
| **Pilot acceptance** | The above, plus agreed acceptance criteria |
| **Training** | A stable build and a pilot environment |

### What a pilot environment actually requires

Three gate criteria have never been met and cannot be met from a development machine:

1. **A real rendered-browser path** — every gate so far reports UI work as verified by typecheck,
   lint and build. That is not the same as a person clicking through it.
2. **A live mailbox** — `zahid@naspakinc.com` is available; the ingest path needs to run against real
   arriving mail rather than fixtures.
3. **A working malware scanner** — see Gate 9 item 2. Documents arriving from outside must be scanned
   before a human opens them.

---

## The short version

**Four decisions and one environment.** Residency, malware spend, hosting plan, backup owner — and a
pilot environment with a live mailbox.

Everything else on the board is engineering, and engineering is being finished. These five are not,
and no amount of agent capacity moves them. They have lead time, so the cost of deciding them late is
paid in calendar days at the end of the project, when it is most expensive.
