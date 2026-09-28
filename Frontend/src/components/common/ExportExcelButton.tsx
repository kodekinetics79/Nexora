import { useState } from 'react';
import { Button, CircularProgress } from '@mui/material';
import { FileDownloadOutlined as DownloadIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';

export interface ExportColumn<T> {
  header: string;
  value: (row: T) => string | number | null | undefined;
}

/** Far more than any list a person reads; a runaway loop stops here instead of at the browser. */
export const EXPORT_ROW_LIMIT = 10_000;
const PAGE_SIZE = 500;

/**
 * Every row of a paged list, fetched with the same call (and so the same filters and the same
 * rep/manager scope) the grid itself uses. Stops at the first short page.
 */
export async function loadAllPages<T>(fetchPage: (pageNumber: number, pageSize: number) => Promise<{ items: T[] }>): Promise<T[]> {
  const rows: T[] = [];
  for (let page = 1; rows.length < EXPORT_ROW_LIMIT; page++) {
    const { items } = await fetchPage(page, PAGE_SIZE);
    rows.push(...items);
    if (items.length < PAGE_SIZE) break;
  }
  return rows.slice(0, EXPORT_ROW_LIMIT);
}

interface Props<T> {
  /** "Leads" → Leads-2026-09-28.xlsx */
  name: string;
  columns: ExportColumn<T>[];
  loadRows: () => Promise<T[]>;
}

/**
 * One button that saves what the list shows as an Excel file: every row that matches the
 * current search and filters, not just the page on screen. Offered to everyone who can open the list.
 */
function ExportExcelButton<T>({ name, columns, loadRows }: Props<T>) {
  const [busy, setBusy] = useState(false);

  const run = async () => {
    setBusy(true);
    try {
      const rows = await loadRows();
      const XLSX = await import('xlsx');
      const sheet = XLSX.utils.aoa_to_sheet([
        columns.map((c) => c.header),
        ...rows.map((row) => columns.map((c) => c.value(row) ?? '')),
      ]);
      sheet['!autofilter'] = { ref: XLSX.utils.encode_range({ s: { r: 0, c: 0 }, e: { r: rows.length, c: columns.length - 1 } }) };
      sheet['!cols'] = columns.map((c, i) => ({
        wch: Math.min(50, Math.max(c.header.length, ...rows.slice(0, 200).map((r) => String(columns[i].value(r) ?? '').length)) + 2),
      }));
      const book = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(book, sheet, name.slice(0, 31));
      const today = new Date().toISOString().slice(0, 10);
      XLSX.writeFile(book, `${name.replace(/[^\w.-]+/g, '-')}-${today}.xlsx`);
      if (rows.length >= EXPORT_ROW_LIMIT) toast(`Only the first ${EXPORT_ROW_LIMIT.toLocaleString()} rows were exported. Narrow the search to get the rest.`);
    } catch {
      toast.error("We couldn't make the Excel file. Please try again.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Button
      variant="outlined"
      startIcon={busy ? <CircularProgress size={16} color="inherit" /> : <DownloadIcon />}
      onClick={run}
      disabled={busy}
      sx={{ fontWeight: 800, borderRadius: 2, whiteSpace: 'nowrap' }}
    >
      {busy ? 'Exporting…' : 'Export to Excel'}
    </Button>
  );
}

export default ExportExcelButton;
