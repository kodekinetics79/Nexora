/**
 * Per-user, per-browser list comforts (Simple/Spreadsheet, row density). Local on purpose: they
 * have no server contract. Column choice and order are saved on the server (useColumnPreferences).
 */
export type ViewChoice = 'simple' | 'spreadsheet';
export type DensityChoice = 'comfortable' | 'standard' | 'compact';

export const userScopedKey = (base: string): string => {
  try {
    const raw = localStorage.getItem('userData');
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && 'id' in parsed) {
        const id = (parsed as { id?: unknown }).id;
        if (typeof id === 'number' || typeof id === 'string') return `${base}:user-${id}`;
      }
    }
  } catch {
    // Corrupted or unavailable storage: a global key.
  }
  return `${base}:global`;
};

export const loadView = (base: string): ViewChoice => {
  try {
    return localStorage.getItem(userScopedKey(`${base}.view`)) === 'spreadsheet' ? 'spreadsheet' : 'simple';
  } catch {
    return 'simple';
  }
};

export const loadDensity = (base: string): DensityChoice => {
  try {
    const stored = localStorage.getItem(userScopedKey(`${base}.density`));
    return stored === 'compact' || stored === 'standard' ? stored : 'comfortable';
  } catch {
    return 'comfortable';
  }
};

export const saveChoice = (base: string, kind: 'view' | 'density', value: string): void => {
  try {
    localStorage.setItem(userScopedKey(`${base}.${kind}`), value);
  } catch {
    // The choice holds for this visit only.
  }
};
