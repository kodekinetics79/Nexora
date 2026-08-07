// ---------------------------------------------------------------------------
// Reference data for the tenant-provisioning wizard.
//
// Currency and time-zone lists come from the runtime's own ICU tables rather
// than a checked-in list, because a stale hand-maintained list is exactly how a
// customer ends up unable to pick their own currency. Country codes have no
// equivalent runtime enumeration, so the ISO-3166-1 alpha-2 set is listed here
// — codes only; the display names are resolved by Intl.
// ---------------------------------------------------------------------------

/**
 * `Intl.supportedValuesOf` is ES2022 and present in every browser this app
 * supports, but not in every test environment. Reaching it through a widened
 * type keeps the call optional instead of assuming a lib version.
 */
const intl = Intl as typeof Intl & { supportedValuesOf?: (key: string) => string[] };

const safeSupportedValues = (key: string): string[] => {
  try {
    return intl.supportedValuesOf?.(key) ?? [];
  } catch {
    return [];
  }
};

/** ISO-3166-1 alpha-2. */
export const COUNTRY_CODES: string[] = [
  'AD', 'AE', 'AF', 'AG', 'AI', 'AL', 'AM', 'AO', 'AQ', 'AR', 'AS', 'AT', 'AU', 'AW', 'AX', 'AZ',
  'BA', 'BB', 'BD', 'BE', 'BF', 'BG', 'BH', 'BI', 'BJ', 'BL', 'BM', 'BN', 'BO', 'BQ', 'BR', 'BS',
  'BT', 'BV', 'BW', 'BY', 'BZ', 'CA', 'CC', 'CD', 'CF', 'CG', 'CH', 'CI', 'CK', 'CL', 'CM', 'CN',
  'CO', 'CR', 'CU', 'CV', 'CW', 'CX', 'CY', 'CZ', 'DE', 'DJ', 'DK', 'DM', 'DO', 'DZ', 'EC', 'EE',
  'EG', 'EH', 'ER', 'ES', 'ET', 'FI', 'FJ', 'FK', 'FM', 'FO', 'FR', 'GA', 'GB', 'GD', 'GE', 'GF',
  'GG', 'GH', 'GI', 'GL', 'GM', 'GN', 'GP', 'GQ', 'GR', 'GS', 'GT', 'GU', 'GW', 'GY', 'HK', 'HM',
  'HN', 'HR', 'HT', 'HU', 'ID', 'IE', 'IL', 'IM', 'IN', 'IO', 'IQ', 'IR', 'IS', 'IT', 'JE', 'JM',
  'JO', 'JP', 'KE', 'KG', 'KH', 'KI', 'KM', 'KN', 'KP', 'KR', 'KW', 'KY', 'KZ', 'LA', 'LB', 'LC',
  'LI', 'LK', 'LR', 'LS', 'LT', 'LU', 'LV', 'LY', 'MA', 'MC', 'MD', 'ME', 'MF', 'MG', 'MH', 'MK',
  'ML', 'MM', 'MN', 'MO', 'MP', 'MQ', 'MR', 'MS', 'MT', 'MU', 'MV', 'MW', 'MX', 'MY', 'MZ', 'NA',
  'NC', 'NE', 'NF', 'NG', 'NI', 'NL', 'NO', 'NP', 'NR', 'NU', 'NZ', 'OM', 'PA', 'PE', 'PF', 'PG',
  'PH', 'PK', 'PL', 'PM', 'PN', 'PR', 'PS', 'PT', 'PW', 'PY', 'QA', 'RE', 'RO', 'RS', 'RU', 'RW',
  'SA', 'SB', 'SC', 'SD', 'SE', 'SG', 'SH', 'SI', 'SJ', 'SK', 'SL', 'SM', 'SN', 'SO', 'SR', 'SS',
  'ST', 'SV', 'SX', 'SY', 'SZ', 'TC', 'TD', 'TF', 'TG', 'TH', 'TJ', 'TK', 'TL', 'TM', 'TN', 'TO',
  'TR', 'TT', 'TV', 'TW', 'TZ', 'UA', 'UG', 'UM', 'US', 'UY', 'UZ', 'VA', 'VC', 'VE', 'VG', 'VI',
  'VN', 'VU', 'WF', 'WS', 'YE', 'YT', 'ZA', 'ZM', 'ZW',
];

let regionNames: Intl.DisplayNames | null | undefined;

/** Human-readable country name, falling back to the raw code when ICU cannot name it. */
export const countryName = (code: string | null | undefined): string => {
  if (!code) return '';
  if (regionNames === undefined) {
    try {
      regionNames = new Intl.DisplayNames(['en'], { type: 'region' });
    } catch {
      regionNames = null;
    }
  }
  const upper = code.toUpperCase();
  try {
    return regionNames?.of(upper) ?? upper;
  } catch {
    return upper;
  }
};

export const countryLabel = (code: string | null | undefined): string => {
  if (!code) return '';
  const name = countryName(code);
  const upper = code.toUpperCase();
  return name === upper ? upper : `${name} (${upper})`;
};

// A tenant that cannot be assigned its own currency is worse than a short list,
// so this falls back to the majors only when ICU enumeration is unavailable.
const FALLBACK_CURRENCIES = ['AED', 'AUD', 'CAD', 'CHF', 'CNY', 'EUR', 'GBP', 'INR', 'JPY', 'SAR', 'USD', 'ZAR'];

export const CURRENCY_CODES: string[] = (() => {
  const supported = safeSupportedValues('currency');
  return supported.length > 0 ? supported : FALLBACK_CURRENCIES;
})();

export const TIME_ZONE_IDS: string[] = (() => {
  const supported = safeSupportedValues('timeZone');
  if (supported.length > 0) return supported;
  const local = resolvedTimeZone();
  return local ? [local, 'UTC'] : ['UTC'];
})();

/** The operator's own zone — a far better default than UTC for a hand-provisioned tenant. */
export function resolvedTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** Common BCP-47 tags offered as suggestions; any valid tag is still accepted. */
export const LOCALE_SUGGESTIONS = [
  'en-US', 'en-GB', 'en-AE', 'en-IN', 'en-AU', 'en-CA', 'en-ZA',
  'fr-FR', 'de-DE', 'es-ES', 'it-IT', 'nl-NL', 'pt-BR', 'ar-AE', 'ar-SA', 'ja-JP', 'zh-CN',
];

/** True when the tag is structurally a BCP-47 language tag the runtime can canonicalise. */
export const isValidLocale = (tag: string): boolean => {
  const trimmed = tag.trim();
  if (trimmed.length === 0) return false;
  try {
    return Intl.getCanonicalLocales(trimmed).length > 0;
  } catch {
    return false;
  }
};
