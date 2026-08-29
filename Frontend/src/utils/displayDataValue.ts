/** Preserve meaningful falsy values (notably numeric zero) while standardising missing fields. */
export const displayDataValue = (
  value: string | number | null | undefined,
): string | number => (value === null || value === undefined || value === '' ? '—' : value);
