import { describe, expect, it } from 'vitest';
import { ACCEPTED_FILE_TYPES, describeUnsupported, isSupportedFile } from './uploadFileTypes';

describe('upload file types', () => {
  it.each(['Fw_ Marafiq RFQ No 6000595208_ Ref No HFE-26-204_.msg', 'RFx 5500804759.MSG', 'forwarded.eml', 'SEC print.html', 'print.htm'])(
    'accepts %s, which the server reads',
    (name) => {
      expect(isSupportedFile(name)).toBe(true);
    },
  );

  it('still accepts the office, pdf and image files', () => {
    for (const name of ['a.pdf', 'b.doc', 'c.docx', 'd.xlsx', 'e.xlsm', 'f.csv', 'g.png', 'h.tiff']) expect(isSupportedFile(name)).toBe(true);
    expect(ACCEPTED_FILE_TYPES).toContain('.msg');
  });

  it('refuses a zip and names it', () => {
    expect(isSupportedFile('Doc C001831499.zip')).toBe(false);
    expect(describeUnsupported(['Doc C001831499.zip'])).toBe("Doc C001831499.zip can't be read (unzip it first)");
  });

  it('names at most two refused files', () => {
    expect(describeUnsupported(['a.exe', 'b.exe', 'c.exe'])).toBe("a.exe, b.exe and 1 more can't be read");
    expect(describeUnsupported([])).toBeNull();
  });
});
