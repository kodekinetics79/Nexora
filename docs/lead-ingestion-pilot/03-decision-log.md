# Decision Log — Lead / RFQ Ingestion Client Pilot

Every entry: decision, alternatives considered, challenge, rationale, owner, date, impact.
The Acting CTO decides when reviewers disagree; the disagreement is recorded, not erased.

---

## D-001 — Arabic / Hijri extraction is OUT of pilot scope

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED (founder approved)

**Decision.** Arabic and Hijri extraction (part of FR-RFQ-04) is excluded from the pilot and
recorded as an accepted limitation with an owner and a closure date. It is not removed from
the roadmap and will be stated plainly in the readiness report rather than presented as
coverage we have.

**Alternatives.** (a) Build it in scope — rejected: zero Arabic documents exist in
production, Tesseract has never executed on a single production job, and the Arabic language
pack is unverified in the container; open-ended risk that could consume the whole budget.
(b) Leave it ambiguous — rejected outright; the master prompt forbids silently dropping it.

**Rationale.** The founder's stated priorities are ingest-anything, accuracy, and speed.
Arabic is the one item that could swallow the budget without moving any of the three.

**Impact.** FR-RFQ-04 will be reported PARTIAL with the Arabic/Hijri component named as an
accepted limitation. Must appear in `07-final-readiness-report.md` §J.

---

## D-002 — Sequence by client-visible breakage, not by FR number

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED

**Decision.** Work is ordered by what fails in front of the client, not FR-RFQ-01 → 08.

**Rationale.** A complete FR-01→08 build is roughly a quarter of work. Breadth-first
produces partial coverage everywhere and a demo that works nowhere.

**Impact.** The RTM still covers all eight requirements; the P0 backlog does not.

---

## D-003 — Reuse existing numbering; do not build a parallel scheme

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED, pending client confirmation

**Decision.** Map the existing `NexoraSerial` / `CommercialCaseReference` generator to the
required client-facing format rather than introducing a second numbering system.

**Alternatives.** Build `RFQ-KSA-{YYYY}-{sequence}` as a new generator — rejected: it would
create exactly the duplicate state systems the master prompt forbids.

**Open question for the founder.** Does the client require that literal string, or simply a
unique, year-scoped, client-facing reference? If the literal string is contractual, this
decision is revisited.

---

## D-004 — Do not claim the 60-second extraction target; measure and publish

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED

**Decision.** Publish measured extraction times by document size. Do not assert BRD
compliance with ~60s for a standard multi-page RFQ without evidence.

**Rationale.** A real Aramco RFP in this corpus carries 121 MB of expanded XML. It will not
extract in 60 seconds. An unqualified target in front of a client is a false claim.

---

## D-005 — Cost per document is a readiness gate

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED

**Decision.** Add measured external-AI cost per document to the readiness gates, alongside
accuracy.

**Rationale.** Absent from the master prompt. On 2026-08-06, 1,133 external AI calls were
spent in three hours producing zero leads. At the pitched volume (~900 inquiries/month) unit
economics decide whether the proposition is a business.

---

## D-006 — Phase 1 is time-boxed and runs concurrently with fixing proven blockers

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED

**Decision.** The read-only audit does not gate remediation of defects already proven with
production evidence.

**Rationale.** A full eight-area audit plus a consultant gate before any code change could
consume the budget before a line moves. The loop that produced results on 2026-08-05/06 was
find blocker → fix → verify in production → deploy.

**Guard.** Only defects with production evidence bypass the gate. Anything discovered by
inspection alone waits for Phase 2.

---

## D-007 — Real client documents are a day-one blocking dependency

**Date:** 2026-08-06 · **Owner:** Founder · **Status:** OPEN — folder path awaited

**Decision.** Declared blocking at Phase 0 rather than discovered at Phase 5.

**Evidence.** The local corpus deduplicates to **14 genuine SEC `.doc` bid documents**. The
`.pdf` is a 45-byte stub; the `.xlsx` is synthetic; 58 CSVs are test data. There are **zero
real specimens for eight of the nine required formats**.

**Impact.** FR-RFQ-02 cannot reach VERIFIED beyond `.doc`/`.docx`/`.xls` without it. Pilot
certification (Phase 5) is blocked on it.

---

## D-008 — The external-dependency ceiling is enforced pre-egress only

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** IMPLEMENTED, deployed `8f8c84d`

**Decision.** Remove the duplicate ceiling check in
`LeadIdentity/LeadIdentityApplicationService`. Enforcement lives solely in
`AiGovernanceService.ReserveAsync`, before the model call, honouring the tenant's configured
percentage and the allow-list exemption.

**Evidence.** 1,133 successful AI calls in three hours produced zero leads. The duplicate
check used a hardcoded 10%, had no allow-list exemption (on a deployment with no local model
the external ratio is permanently 100%), and threw — dead-lettering documents — while its own
message promised "route this occurrence to human review". Jobs reached 14 attempts this way.

**Challenge considered.** Does removing it weaken governance? No: enforcing an egress ceiling
*after* egress has occurred prevents nothing. It destroyed completed, authorized, billed work
and guaranteed the retry would repeat it.

**Verified impact.** Post-deploy re-drive: Succeeded 9 → **35**, dead-letters 41 → **22** (all
22 are lost-bytes, not extraction failures), **26 leads / 495 line items** produced, **zero**
new dead-letters.

---

## D-009 — Truthful failure reporting outranks the credential fix

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** IN PROGRESS

**Decision.** The email door's *reporting* defect is P0 and is fixed now; the mailbox
credential itself is the founder's to restore and does not block the fix.

**Evidence.** Every poll cycle throws `MailKit.Security.AuthenticationException:
Authentication failed` and then logs `"Email fetch completed successfully."` 1.5 ms later.
The heartbeat beats unconditionally, so no health check reddens. Last real mailbox contact:
2026-07-30.

**Rationale.** A pilot channel that reports success while dead is more dangerous than one
that is visibly down — it is the charter's silent-loss failure mode relocated into the health
signal. The same pattern currently masks the evidence-storage warning (`/ready` reports
Unhealthy; the platform probes `/health`).

**Scope note.** Fixed together because they are one defect class in one file set: the
unconditional heartbeat, the fixed 7-day + NotSeen lookback (an outage beyond 7 days loses
messages permanently; a human opening a message first prevents ingestion forever), the
duplicate-skip keyed on `(From, To, Subject)` instead of MessageId (drops a customer's second
RFQ under a repeated subject), and skipped-attachment records written only inside a
conditional branch.

---

## D-010 — Legacy folder parsers are dead code, and stay untouched

**Date:** 2026-08-06 · **Owner:** Acting CTO · **Status:** ACCEPTED

**Decision.** `ProcessLegacyAramcoFolderAsync` / `ProcessLegacySecFolderAsync` and their
exclusive helpers (~700 lines, zero callers) are neither fixed nor deleted during the pilot
program.

**Rationale.** A prior review panel recommended "fixing" dangerous-looking parsers in this
region; they are unreachable, so the work would be pure cost. Deleting them is safe but is
churn in files other agents are reading. Recorded so no future reviewer re-litigates it.

**Revisit:** after pilot, as a standalone cleanup with its own test run.
