# Representative RFQ corpus

Real files on disk — not synthesized at test runtime — each with its expected classification
and extracted values defined **in advance** in `corpus-manifest.json`. The acceptance tests
(`CorpusAcceptanceTests`, `AcceptanceJourneyTests`) read the bytes from this directory and
assert against the manifest; nothing in the test run regenerates or mutates the corpus.

## Inventory

| File | What it represents |
| --- | --- |
| `email-simple-english.eml` | Simple English body-only RFQ (RFQ-CORPUS-001, 40 nos) |
| `email-simple-arabic.eml` | Mixed Arabic/English body-only RFQ |
| `email-with-pdf.eml` | Attachment-only message carrying `doc-native-rfq.pdf` |
| `email-duplicate.eml` | The byte-identical duplicate pair — delivered twice in tests |
| `email-revised-rfq.eml` | Revision B of RFQ-CORPUS-001 (qty 40 → 60), In-Reply-To the original |
| `email-conflicting-quantities.eml` | Body says 40, attached PDF says 45 |
| `email-missing-part-numbers.eml` | Lines with descriptions but no part numbers |
| `email-forwarded-inner.eml` | Forward with an inner `message/rfc822` |
| `email-multi-attachment.eml` | Three supported attachments (.xlsx, .docx, .xls) on one message |
| `email-mixed-protected.eml` | Password-protected PDF + good PDF on the same message |
| `email-noise-newsletter.eml` | Bulk newsletter (List-Id / List-Unsubscribe / Precedence) |
| `email-noise-auto-reply.eml` | Out-of-office (Auto-Submitted: auto-replied) |
| `email-noise-no-reply.eml` | Unattended sender (no-reply@) |
| `doc-native-rfq.pdf` | Native-text PDF RFQ, two lines |
| `doc-good-simple.pdf` | Native-text PDF RFQ, one line |
| `doc-conflicting-qty.pdf` | The "Qty 45" sheet attached to the conflicting email |
| `doc-scanned-rfq.pdf` | Image-only PDF — OCR required |
| `doc-multisheet-rfq.xlsx` | Two sheets, both in a recognized column layout |
| `doc-large-rfq.xlsx` | 300-line RFQ in a recognized layout |
| `doc-legacy-rfq.xls` | Legacy BIFF workbook (same bytes as `Fixtures/recognized-layout-rfq.xls`) |
| `doc-rfq-table.docx` | Word table RFQ |
| `doc-outlook-rfq.msg` | Real OLE compound file with MAPI property streams |
| `doc-password-protected.pdf` | RC4-40 encrypted PDF (user/owner password `nexora`) |
| `doc-corrupted.docx` | Valid docx truncated to 512 bytes |
| `doc-unsupported.pptx` | Zip bytes under a refused extension |

## Regeneration

The bytes are produced by `Support/CorpusGenerator.cs` and written ONCE, then committed. To
regenerate (only when the corpus itself must change — e.g. a new scenario):

```bash
cd Backend
NEXORA_REGENERATE_CORPUS=1 \
NEXORA_CORPUS_OUT="$(pwd)/ERP_RFQ_Automation.Tests/Corpus" \
dotnet test ERP_RFQ_Automation.Tests --filter "FullyQualifiedName~CorpusRegenerationTests"
```

Then re-run the corpus acceptance tests and update `corpus-manifest.json` for any value you
deliberately changed. Notes:

- MIME boundaries and zip timestamps are not byte-stable across regenerations; the fields the
  ingest keys on (Message-Id, Date, From, subject, planted values) are. Regenerating rewrites
  every file — review the diff, do not regenerate casually.
- The password-protected PDF is written by `CorpusGenerator.EncryptedPdf()` — a hand-rolled
  PDF 1.7 Standard-security (R2, RC4-40) writer, because neither QuestPDF nor PdfPig can
  produce an encrypted PDF. The regeneration test proves PdfPig refuses to open it without
  the password, which is exactly the check `ProductionDocumentReader` relies on.
- `doc-legacy-rfq.xls` is copied from `Fixtures/recognized-layout-rfq.xls` (a real BIFF file
  already certified by `ProductionDocumentReaderSpreadsheetFallbackTests`).

Total corpus size is deliberately small (well under 10 MB).
