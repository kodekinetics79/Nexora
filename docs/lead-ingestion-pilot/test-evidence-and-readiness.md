# Test Evidence and Readiness — Checkpoint

**Measured:** 2026-08-06, against the **current uncommitted working tree** of `Nexora-main`.
**Why re-measured:** the baseline in `05-test-and-evidence-matrix.md` was taken 02:00–02:30 UTC.
Every working-tree change post-dates it (files written 22:00–23:40; migrations timestamped
`20260806025029` / `20260806025406`). **The recorded baseline does not cover the current tree.**

---

## 1. Commands executed and results

| Lane | Exact command | Result | Duration | Backing |
|---|---|---|---|---|
| L1 backend — SQLite | `cd Backend/ERP_RFQ_Automation.Tests && dotnet test --filter "Category!=PostgreSQL" --nologo` | **Failed: 1, Passed: 2040, Skipped: 0, Total: 2041** | 1 m 51 s | In-memory / SQLite provider |
| L2 backend — PostgreSQL | `cd Backend/ERP_RFQ_Automation.Tests && dotnet test --filter "Category=PostgreSQL" --nologo` | **Failed: 0, Passed: 312, Skipped: 0, Total: 312** | 3 m 45 s | **Real PostgreSQL** — Testcontainers on a live local Docker daemon (`docker info` confirmed available before the run) |
| L3 frontend — vitest | *not re-run* | baseline 216 passed / 0 failed stands | — | `Nexora-main/Frontend` has **zero** working-tree modifications (`git status --porcelain Frontend` → 0 lines), so the baseline is still valid |
| L4 e2e — Playwright | *not run* | no evidence | — | Requires a deployed backend; none exercised this checkpoint |

**No test was retried. No test was mocked into passing. No skip attribute was added.**
Build artifacts produced by these runs did not leak into version control: of 42 `git status`
entries, **0** match `bin/` or `obj/`.

### Delta against the recorded baseline

| | Baseline (02:00–02:30 UTC) | Now | Delta |
|---|---|---|---|
| SQLite lane | 1997 passed / 0 failed | 2040 passed / **1 failed** | **+43 tests, +1 failure** |
| PostgreSQL lane | 308 passed / 0 failed | 312 passed / 0 failed | +4 tests, still green |
| Combined | 2521 / 0 failed / 0 skipped | 2352 measured / **1 failed** (L3 not re-run) | — |

The baseline's stated corollary — *"the baseline is 2521/2521; if a lane returns anything other
than `Failed: 0`, the delta is yours"* — applies. **The failure below is newly introduced by
this program's uncommitted work. There are no pre-existing failures.**

---

## 2. The one failure — newly introduced, and it is a real contradiction

```
ERP_RFQ_Automation.Tests.EmailIngestEnqueuerTests
  .ASkippedAttachmentIsRecordedWhenTheBodyDoesProduceAJobToo   [FAIL]
Assert.Equal() Failure: Values differ
Expected: 1
Actual:   2
  at EmailIngestEnqueuerTests.cs:line 207
```

**Cause.** Two parts of the same uncommitted change set disagree about `.msg`.

- `Security/DocumentInspection/DocumentIntakeAllowList.cs:48` now admits `".html", ".htm",
  ".eml", ".msg"` — `.msg` became a **supported** attachment, backed by the new
  `OutlookMsgReader` / `EmailContainerReader` / `OleCompoundFile`.
- `EmailIngestEnqueuerTests.cs:198-211` still encodes the **old** contract: it attaches
  `notes.msg`, asserts `Queued == 1` ("the body only"), and asserts `notes.msg` appears in
  `SkippedAttachmentsJson`. `.msg` is now enqueued, so `Queued == 2`.

**This is a stale test, not a broken feature** — but it is not safe to simply delete. The test
also asserted the FR-RFQ-02 *"nothing disappears silently"* contract (the `skippedAttachments`
persistence window). The replacement must keep that assertion using an extension that is still
genuinely unsupported (e.g. `.pptx`, as its sibling test at `:175-195` already does), and add a
new positive test asserting `.msg` now yields a queued job **and** a parsed envelope.

---

## 3. Test-hygiene regression — 6 test cases silently dropped at discovery

```
[xUnit.net] Skipping test case with duplicate ID …
  DocumentIntakeAllowListTests.EmailFilter_EqualsInspectionAllowList(extension: ".msg")
  … same for ".eml", ".html"  × EmailFilter and ManualUploadFilter   = 6 cases
```

`DocumentIntakeAllowListTests.cs:49-57` builds its `TheoryData` by concatenating
`DocumentIntakeAllowList.Extensions` with a hard-coded negative-probe list that still contains
`".msg", ".eml", ".html"`. Now that those three are in the allow-list, the rows collide and
xUnit drops one of each pair.

**These 6 do not appear in `Skipped: 0`** — they are dropped before the run, so the summary
reads clean while coverage silently shrank. That is precisely the erosion the program's
zero-skips control exists to prevent. Fix: de-duplicate `ProbeExtensions()`
(`data.Add` over a `HashSet`, or remove the three from the negative list).

---

## 4. What these 2,352 passing tests do *not* prove

Carried forward from the baseline audit and still true — the passing count is necessary,
not sufficient:

| Gap | Consequence |
|---|---|
| **Corpus.** 8 of 9 required formats have zero real specimens (A7). The `.pdf` fixture is a 45-byte, 0-page stub; the 58 `.csv` are synthetic. | No test can prove PDF, scanned PDF, XLSX, HTML, JPEG, PNG parsing. The new `.msg`/`.eml` readers are proven only against `Support/CompoundFileBuilder.cs` — a **hand-built** compound file, not an Outlook-produced one. |
| **OCR.** No test constructs a `TesseractEngine`. `OcrEngineStartupProbe` is untested and undeployed. | The native Tesseract/pdfium bind in the `aspnet:8.0` container remains **unverified**. |
| **The mailbox.** `EmailChannelTruthfulnessTests` proves the ledger records a failure; it does not prove the mailbox authenticates. | FR-RFQ-01(a) stays DEFECTIVE until a real poll succeeds against the real mailbox. |
| **The review queue.** `LeadMatchCandidates` = 0 in production. The `ProposedLeadSnapshotJson` fix has unit coverage but has never run against a real operator decision. | FR-RFQ-06 remains undemonstrated end-to-end. |
| **Routing.** Nothing tests an assigned outcome, because `sales_rep_profiles` = 0 and no write path exists. | FR-RFQ-07 cannot be tested into readiness; it needs code. |
| **Browser.** No Playwright run. No screenshot against a real backend at this checkpoint. | No UI claim in this checkpoint is browser-verified. |

---

## 5. Readiness verdict

**NOT READY for the pilot journey.**

The journey *Manual RFQ Upload → Durable Ingestion Occurrence → Source Stored → Parse/OCR →
Extract Header and Lines → Human Review → Accept as Lead/RFQ → Assign to Sales Queue → Ready
for Quotation* terminates at **Assign to Sales Queue**: 44 of 44 production leads are
`Unassigned` with `NO_MATCH_EVIDENCE`, and there is no write path to create the sales-rep
profile that would change that.

Before any further feature work, the tree must return to `Failed: 0` and the 6 dropped
cases must be restored.
