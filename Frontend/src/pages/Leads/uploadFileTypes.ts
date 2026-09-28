/**
 * The file types the lead upload screen accepts — exactly the server's intake allow-list
 * (Backend/ERP_RFQ_Automation/Security/DocumentInspection/DocumentIntakeAllowList.cs; a backend
 * test keeps the two in step). The screen used to lag it: Aramco and Marafiq forward their RFQs
 * as .msg, SEC portals export .html, and the screen refused them although the server reads them.
 *
 * .zip is NOT here: the server does not open archives. A drawings zip has to be unpacked first.
 */
export const SUPPORTED_EXTENSIONS = [
  '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.xlsm', '.csv', '.txt',
  '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.tif', '.tiff', '.webp',
  '.html', '.htm', '.eml', '.msg',
];

export const ACCEPTED_FILE_TYPES = SUPPORTED_EXTENSIONS.join(',');

export const extensionOf = (name: string): string => {
  const separator = name.lastIndexOf('.');
  return separator >= 0 ? name.slice(separator).toLowerCase() : '';
};

export const isSupportedFile = (name: string): boolean => SUPPORTED_EXTENSIONS.includes(extensionOf(name));

/** Names the refused files, so the rep knows which one to take out ("Doc C001831499.zip can't be read"). */
export const describeUnsupported = (names: string[]): string | null => {
  if (names.length === 0) return null;
  const shown = names.slice(0, 2).join(', ');
  const more = names.length > 2 ? ` and ${names.length - 2} more` : '';
  const zip = names.some((name) => extensionOf(name) === '.zip') ? ' (unzip it first)' : '';
  return `${shown}${more} can't be read${zip}`;
};
