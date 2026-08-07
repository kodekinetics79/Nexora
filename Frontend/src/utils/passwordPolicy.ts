// ---------------------------------------------------------------------------
// The password rules a human-chosen credential must satisfy.
//
// Twelve characters is the same floor the platform-owner bootstrap enforces
// server-side; keeping the client on the identical number means a customer
// never gets a rejection from the API for a rule the form told them was fine.
//
// The upper bound exists because the hash is BCrypt, which silently ignores
// everything past 72 bytes — a longer passphrase would be accepted here and
// then quietly truncated, so the tail the user typed would not actually
// protect anything.
// ---------------------------------------------------------------------------

export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 64;

export interface PasswordRule {
  id: string;
  label: string;
  satisfied: (password: string) => boolean;
}

export const PASSWORD_RULES: PasswordRule[] = [
  {
    id: 'length',
    label: `At least ${PASSWORD_MIN_LENGTH} characters`,
    satisfied: (p) => p.length >= PASSWORD_MIN_LENGTH,
  },
  { id: 'lower', label: 'A lowercase letter', satisfied: (p) => /[a-z]/.test(p) },
  { id: 'upper', label: 'An uppercase letter', satisfied: (p) => /[A-Z]/.test(p) },
  { id: 'digit', label: 'A number', satisfied: (p) => /[0-9]/.test(p) },
  { id: 'symbol', label: 'A symbol (for example ! ? # $ %)', satisfied: (p) => /[^A-Za-z0-9]/.test(p) },
];

export const unmetPasswordRules = (password: string): PasswordRule[] =>
  PASSWORD_RULES.filter((rule) => !rule.satisfied(password));

export const isPasswordAcceptable = (password: string): boolean =>
  password.length <= PASSWORD_MAX_LENGTH && unmetPasswordRules(password).length === 0;

/**
 * A single sentence naming the FIRST unmet requirement, for use as inline field
 * text. The full checklist is still rendered alongside — this is the summary a
 * screen reader hears when the field is announced as invalid.
 */
export const passwordProblem = (password: string): string | null => {
  if (password.length === 0) return null;
  if (password.length > PASSWORD_MAX_LENGTH) {
    return `Use at most ${PASSWORD_MAX_LENGTH} characters.`;
  }
  const unmet = unmetPasswordRules(password);
  return unmet.length === 0 ? null : `Still needed: ${unmet.map((r) => r.label.toLowerCase()).join(', ')}.`;
};

export type PasswordStrength = 'weak' | 'fair' | 'good' | 'strong';

export interface PasswordStrengthReading {
  strength: PasswordStrength;
  /** 0–100, for a progress bar. */
  score: number;
  label: string;
}

/**
 * Strength is length-led on purpose: character-class variety is what the policy
 * already mandates, so scoring it again would rate a 12-character minimum-legal
 * password as "strong" and stop encouraging the thing that actually helps.
 */
export const readPasswordStrength = (password: string): PasswordStrengthReading => {
  if (password.length === 0) return { strength: 'weak', score: 0, label: 'Enter a password' };

  const classes = PASSWORD_RULES.slice(1).filter((rule) => rule.satisfied(password)).length;
  const lengthPoints = Math.min(4, Math.floor(password.length / 6)); // 0–4 across 0–24+ chars
  const distinct = new Set(password).size;
  const varietyPoints = distinct >= 10 ? 1 : 0;
  const raw = classes + lengthPoints + varietyPoints; // 0–9

  if (!isPasswordAcceptable(password)) {
    return { strength: 'weak', score: Math.min(35, raw * 5), label: 'Too weak' };
  }
  if (raw >= 8) return { strength: 'strong', score: 100, label: 'Strong' };
  if (raw >= 7) return { strength: 'good', score: 78, label: 'Good' };
  return { strength: 'fair', score: 55, label: 'Fair' };
};
