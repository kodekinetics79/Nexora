import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as XLSX from 'xlsx';
import ExportExcelButton, { loadAllPages, type ExportColumn } from './ExportExcelButton';

const written = vi.hoisted(() => ({ book: null as unknown, fileName: '' }));

vi.mock('react-hot-toast', () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));
vi.mock('xlsx', async (importOriginal) => {
  const actual = await importOriginal<typeof import('xlsx')>();
  return {
    ...actual,
    writeFile: (book: unknown, fileName: string) => { written.book = book; written.fileName = fileName; },
  };
});

type Row = { no: string; customer?: string; total: number };
const columns: ExportColumn<Row>[] = [
  { header: 'Quote #', value: (r) => r.no },
  { header: 'Customer', value: (r) => r.customer },
  { header: 'Total', value: (r) => r.total },
];

beforeEach(() => {
  written.book = null;
  written.fileName = '';
});

describe('loadAllPages', () => {
  it('keeps asking for the next page until a short page comes back', async () => {
    const fetchPage = vi.fn(async (page: number, size: number) => ({
      items: Array.from({ length: page < 3 ? size : 7 }, (_, i) => ({ id: (page - 1) * size + i })),
    }));

    const rows = await loadAllPages(fetchPage);

    expect(fetchPage).toHaveBeenCalledTimes(3);
    expect(rows).toHaveLength(1007);
  });
});

describe('ExportExcelButton', () => {
  it('saves every row under the list headings in one click', async () => {
    render(<ExportExcelButton name="Quotes" columns={columns}
      loadRows={async () => [{ no: 'QT-1', customer: 'SEC', total: 1250.5 }, { no: 'QT-2', total: 0 }]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Export to Excel' }));

    await waitFor(() => expect(written.book).not.toBeNull());
    expect(written.fileName).toMatch(/^Quotes-\d{4}-\d{2}-\d{2}\.xlsx$/);
    const book = written.book as XLSX.WorkBook;
    const rows = XLSX.utils.sheet_to_json<unknown[]>(book.Sheets.Quotes, { header: 1, defval: '' });
    expect(rows).toEqual([
      ['Quote #', 'Customer', 'Total'],
      ['QT-1', 'SEC', 1250.5],
      ['QT-2', '', 0],
    ]);
  });
});
