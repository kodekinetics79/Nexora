/**
 * What a country tells you, so an operator does not have to.
 *
 * WHY THIS EXISTS. The provisioning form asked a salesperson to type the base currency, the IANA
 * time zone and the BCP-47 locale — three fields nobody outside engineering can answer, two of
 * which are free text, and one of which (`baseCurrencyCode`) is IMMUTABLE after provisioning
 * because changing it would restate every price already quoted. A rep who typed `SR` instead of
 * `SAR`, or `Asia/Jeddah` instead of `Asia/Riyadh`, produced a workspace that either failed
 * activation later or quoted in the wrong currency for ever. None of those three is a commercial
 * decision: every one of them follows from the country the customer is in.
 *
 * So they stop being questions. The country is the question, and it is a picker.
 *
 * DELIBERATELY A TABLE, NOT A LOOKUP LIBRARY. Currency and time zone by country is not a fact a
 * runtime API gives reliably — `Intl` exposes neither a country's currency nor its primary zone
 * — and a wrong-but-plausible answer here is worse than an absent one. The countries listed are
 * the ones this product is actually sold into; anything else is handled by
 * <see cref="COUNTRY_DEFAULT_FALLBACK"/>, which returns nulls so the console asks rather than
 * guesses. A missing row makes the operator confirm three fields once. A wrong row silently
 * misprices a customer.
 *
 * A country with more than one time zone is not listed unless its commercial centre is
 * unambiguous, for the same reason.
 */
export interface CountryDefaults {
  /** ISO-4217. Immutable once provisioned, which is why it must not be typed. */
  currency: string;
  /** IANA zone id. */
  timeZone: string;
  /** BCP-47 tag. */
  locale: string;
}

const TABLE: Record<string, CountryDefaults> = {
  // GCC — the product's home market.
  SA: { currency: 'SAR', timeZone: 'Asia/Riyadh', locale: 'en-SA' },
  AE: { currency: 'AED', timeZone: 'Asia/Dubai', locale: 'en-AE' },
  QA: { currency: 'QAR', timeZone: 'Asia/Qatar', locale: 'en-QA' },
  KW: { currency: 'KWD', timeZone: 'Asia/Kuwait', locale: 'en-KW' },
  BH: { currency: 'BHD', timeZone: 'Asia/Bahrain', locale: 'en-BH' },
  OM: { currency: 'OMR', timeZone: 'Asia/Muscat', locale: 'en-OM' },

  // Wider region.
  EG: { currency: 'EGP', timeZone: 'Africa/Cairo', locale: 'en-EG' },
  JO: { currency: 'JOD', timeZone: 'Asia/Amman', locale: 'en-JO' },
  TR: { currency: 'TRY', timeZone: 'Europe/Istanbul', locale: 'tr-TR' },
  PK: { currency: 'PKR', timeZone: 'Asia/Karachi', locale: 'en-PK' },
  IN: { currency: 'INR', timeZone: 'Asia/Kolkata', locale: 'en-IN' },

  // Europe and the anglosphere.
  GB: { currency: 'GBP', timeZone: 'Europe/London', locale: 'en-GB' },
  IE: { currency: 'EUR', timeZone: 'Europe/Dublin', locale: 'en-IE' },
  DE: { currency: 'EUR', timeZone: 'Europe/Berlin', locale: 'de-DE' },
  FR: { currency: 'EUR', timeZone: 'Europe/Paris', locale: 'fr-FR' },
  NL: { currency: 'EUR', timeZone: 'Europe/Amsterdam', locale: 'nl-NL' },
  ES: { currency: 'EUR', timeZone: 'Europe/Madrid', locale: 'es-ES' },
  IT: { currency: 'EUR', timeZone: 'Europe/Rome', locale: 'it-IT' },
  CH: { currency: 'CHF', timeZone: 'Europe/Zurich', locale: 'de-CH' },
  SE: { currency: 'SEK', timeZone: 'Europe/Stockholm', locale: 'sv-SE' },
  NO: { currency: 'NOK', timeZone: 'Europe/Oslo', locale: 'nb-NO' },
  SG: { currency: 'SGD', timeZone: 'Asia/Singapore', locale: 'en-SG' },
  ZA: { currency: 'ZAR', timeZone: 'Africa/Johannesburg', locale: 'en-ZA' },

  // Deliberately absent: US, CA, AU, RU, BR, ID and anywhere else spanning several zones with
  // no single commercial centre. The console asks for those rather than picking one.
};

/** Returned for a country this table does not cover: the console asks instead of guessing. */
export const COUNTRY_DEFAULT_FALLBACK = null;

/**
 * The operating defaults implied by a country, or null when the country spans several zones or
 * is not a market this table covers — in which case the caller must ask rather than assume.
 */
export function countryDefaults(code: string | null | undefined): CountryDefaults | null {
  if (!code) return COUNTRY_DEFAULT_FALLBACK;
  return TABLE[code.trim().toUpperCase()] ?? COUNTRY_DEFAULT_FALLBACK;
}

/** True when a country's operating defaults are known and need not be asked for. */
export const hasCountryDefaults = (code: string | null | undefined): boolean =>
  countryDefaults(code) !== null;
