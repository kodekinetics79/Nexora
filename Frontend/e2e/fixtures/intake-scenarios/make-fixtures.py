#!/usr/bin/env python3
"""Deterministic binary fixtures for e2e/scenarios-intake.spec.ts.

Re-run from Frontend/: python3 e2e/fixtures/intake-scenarios/make-fixtures.py
Text fixtures (CSV) are built inside the spec; only the binaries live here.
"""
import io, os, shutil, zipfile
from openpyxl import Workbook

here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.abspath(os.path.join(here, '..', '..', '..', '..'))

# 1. A structurally valid OOXML package whose worksheet XML is broken: passes the
#    door's ZIP/[Content_Types] inspection, fails the workbook parser afterwards.
wb = Workbook(); ws = wb.active; ws.title = 'RFQ'
ws.append(['rfqno', 'buyername', 'productname', 'quantity', 'manufacturerpartnumber', 'uom', 'currency'])
ws.append(['SCN-CORRUPT', 'ABC Engineering', 'Ball valve', 3, 'CORE-ATP-100', 'EA', 'SAR'])
good = io.BytesIO(); wb.save(good); good.seek(0)
src = zipfile.ZipFile(good)
corrupt = io.BytesIO()
with zipfile.ZipFile(corrupt, 'w', zipfile.ZIP_DEFLATED) as out:
    for item in src.infolist():
        data = src.read(item.filename)
        if item.filename == 'xl/worksheets/sheet1.xml':
            data = b'<?xml version="1.0"?><worksheet><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c>'  # truncated, unclosed
        out.writestr(item, data)
open(os.path.join(here, 'corrupt-sheet.xlsx'), 'wb').write(corrupt.getvalue())

# 2. Random bytes named as a workbook: no PDF/OLE/ZIP signature at all.
import random
random.seed(20260904)
open(os.path.join(here, 'garbage.xlsx'), 'wb').write(bytes(random.getrandbits(8) for _ in range(2048)))

# 3. A minimal, valid, EMPTY PDF: a real %PDF signature, one blank page, no text objects.
pdf = b"""%PDF-1.4
1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >> endobj
xref
0 4
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000115 00000 n 
trailer << /Size 4 /Root 1 0 R >>
startxref
190
%%EOF
"""
open(os.path.join(here, 'stub.pdf'), 'wb').write(pdf)

# 4. The backend test corpus already carries a genuinely password-protected PDF.
shutil.copyfile(os.path.join(repo, 'Backend', 'ERP_RFQ_Automation.Tests', 'Corpus', 'doc-password-protected.pdf'),
                os.path.join(here, 'password-protected.pdf'))
print('fixtures written:', sorted(f for f in os.listdir(here) if not f.endswith('.py')))
