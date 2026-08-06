# Overnight Test Evidence — Commercial Sourcing Pilot

**Session date:** 2026-08-06. **Repo:** `Nexora-main`, branch `release/nexora-v2-v3-accelerated`.
`RFQ-Automation-Vite` was not opened or modified.

---

## Phase 0 — baseline protection and reproduction: **GATE MET**

Every lane below was executed in this session. Nothing is carried over from a prior report.

| # | Lane | Exact command | Result | Real vs mocked |
|---|---|---|---|---|
| 1 | Backend — non-PostgreSQL | `cd Backend/ERP_RFQ_Automation.Tests && dotnet test --filter "Category!=PostgreSQL" --nologo` | **Failed 0 · Passed 2064 · Skipped 0** (2 m 03 s) | in-memory / SQLite provider |
| 1b | Backend — non-PostgreSQL, **after** the outbound-guard work | same command | **Failed 0 · Passed 2079 · Skipped 0** (1 m 59 s) — **+15** new guard tests | in-memory / SQLite provider |
| 2 | Backend — PostgreSQL | `cd Backend/ERP_RFQ_Automation.Tests && dotnet test --filter "Category=PostgreSQL" --nologo` | **Failed 0 · Passed 312 · Skipped 0** (3 m 17 s baseline; **re-run after the guard work: Failed 0 · Passed 312 · Skipped 0**, 3 m 37 s) | **real PostgreSQL** — Testcontainers on a live local Docker daemon; migrations applied, including `20260806044841_RfqLineParticipationAndQuoteNumberUniqueness` |
| 3 | Frontend — typecheck | `cd Frontend && npx tsc --noEmit` | **exit 0**, no diagnostics | real compiler |
| 4 | Frontend — build | `cd Frontend && npm run build` | **exit 0**, built in 1.01 s; initial JS 1,365,767 B against an optimized budget of 1,446,856 B | real Vite/Rolldown build |
| 5 | Frontend — unit/component | `cd Frontend && npx vitest run` | **14 files · 216 passed · 0 failed** (105.86 s) | real vitest, jsdom |

**Retries used: 0. Skips: 0. Tests silently dropped at discovery: 0.**

Baseline expected by the assignment was `2064` / `312`. Both reproduced exactly, so no test has
disappeared and no count was inflated.

### Working-tree protection

| Check | Result |
|---|---|
| Branch | `release/nexora-v2-v3-accelerated`, 11 commits ahead of origin |
| `git diff --check` | **clean** — no whitespace errors, no conflict markers |
| Safety patch saved outside the repository | `…/scratchpad/nexora-safety-tracked-20260806-0130.patch` (237,733 bytes) |
| Unrelated user work | `RFQ-Automation-Vite` untouched; its changes all date from 2026-07-08 and are unrelated to this program |
| Commit created | **none** — see note below |

**No checkpoint commit was created.** The assignment permits one "only for files already
verified as part of the completed integrity repairs." Those files are interleaved in the same
working tree as 42 inherited, unreviewed changes from a prior session. Committing a subset would
mean staging individual hunks of files that also contain unreviewed work, which risks
misrepresenting what was verified. The safety patch provides the same rollback protection without
that risk. Recommend the founder review and commit the inherited work deliberately.

---

## Phase 1 — base journey prerequisite gate: **NOT MET**

The assignment (§6) makes this a hard gate before inventory and supplier sourcing.

| Prerequisite step | State | Evidence |
|---|---|---|
| Reviewed Lead | exists | `LeadDetailPage.tsx` |
| RFQ/RFP classification confirmed | **NOT ENFORCED** | nothing reads `Lead.InquiryType` (`Models/Lead.Inquiry.cs:16`); `lead.Rfqtype` copied blind at `LeadConversionIntelligence.cs:217` |
| Critical warnings resolved or audited | **NOT ENFORCED** | `NeedsAttention`/`AttentionReason` computed at `LeadConversionIntelligence.cs:437-455`; `ConvertCoreAsync` never reads them (`:224-296`); `LeadConvertPage.tsx:288-296` colours the line and leaves **Create RFQ enabled** |
| Named owner or controlled queue | **IMPOSSIBLE** | `Rfq` has **no owner column** (`Models/Rfq.cs:8-66`); conversion never reads `Lead.AssignTo`; 44/44 production leads are `Unassigned` |
| Convert to exactly one RFQ | **WORKS** | serializable tx + unique partial index `UX_RFQ_BusinessUnitID_LeadID` |
| Mark selected lines Quote | **backend done this session, NO UI** | `Rfqitem.ParticipationDecision`; no frontend control exists |
| Mark other lines No-Quote with reason | **backend done this session, NO UI** | domain + DB check constraints; no frontend control exists |
| Open exactly one Customer Quote Draft | **WORKS** | idempotent, unique partial index `UX_Quotes_BusinessUnitID_RFQID` |
| **Authenticated browser proof** | **DOES NOT EXIST** | `docs/lead-ingestion-pilot/base-journey-browser-result.md` — referenced by the assignment — **is not present in the repository**; no Playwright run has ever covered this journey |

**Consequence.** By the assignment's own §6 rule, work must not proceed into Phase 3 (ATP) until
this gate is green. Three of the eight prerequisites are unimplemented, two are backend-only with
no UI, and the browser proof has never existed. Phases 3–11 were therefore **not started** — see
`overnight-remaining-blockers.md`.

---

## Work performed this session beyond Phase 0

### Outbound email containment — the missing safety prerequisite

The bounded reuse map found **no outbound recipient allow-list and no test sink existed anywhere**
in the codebase. The assignment (§2, §12) makes both mandatory before any overnight supplier
email. They were absent, so that work could not have been performed safely; the control was built
instead.

| Lane | Command | Result |
|---|---|---|
| Guard unit tests | included in lane 1b above | **15 new tests, all passing** |
| Full non-PostgreSQL lane after the change | `dotnet test --filter "Category!=PostgreSQL"` | **Failed 0 · Passed 2079 · Skipped 0** |
| Full PostgreSQL lane after the change | `dotnet test --filter "Category=PostgreSQL"` | **Failed 0 · Passed 312 · Skipped 0** |

Covered: default stays `Live` so no existing deployment changes; `Redirect` reroutes To and
**clears Cc/Bcc** (a surviving Bcc is how a real address slips through a rehearsal) and tags the
subject; `Redirect` with no sink **refuses** rather than falling back to Live; `AllowListOnly`
fails closed on the whole message if any single recipient is unlisted; domain matching rejects
suffix attacks (`notnexora.sa`, `nexora.sa.evil.com`); `DraftOnly` transmits nothing; an
unrecognised mode falls back to Live **and warns**; a real transport with no containment warns by
name at startup.

## Phases 3–11 — not executed

No inventory/ATP, supplier recommendation, AI discovery, sourcing case, supplier RFQ email,
response ingestion, offer comparison or quote-readiness code was written. No test evidence exists
for them because no such work was performed. **Nothing in this document is estimated, projected
or carried forward.**

## Email safety

**Zero emails were sent.** No SMTP sink was started, no mailbox was contacted, no outbound
message of any kind was generated. No external network calls were made to any supplier,
search provider or LLM provider.
