/**
 * The tax treatment a quote line may carry. Mirrors
 * `ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories` and the database check constraint on
 * `QuoteItems.TaxCategory`.
 *
 * The user picks one; the system infers nothing from an address or a delivery term, because a
 * Riyadh-registered customer can still buy for export. Before this existed, a correctly zero-rated
 * export and a domestic sale where the rep forgot the 15% were byte-identical records.
 *
 * The server also serves this list on the commercial policy endpoint (`taxCategories`), which is
 * the authority; these labels exist so the UI has human wording for each code.
 */
export const TAX_CATEGORY_STANDARD = 'STANDARD';
export const TAX_CATEGORY_ZERO_RATED_EXPORT = 'ZERO_RATED_EXPORT';
export const TAX_CATEGORY_EXEMPT = 'EXEMPT';
export const TAX_CATEGORY_OUT_OF_SCOPE_RCM = 'OUT_OF_SCOPE_RCM';

export interface TaxCategoryOption {
  code: string;
  label: string;
  /** Shown in the picker so the choice is made on meaning, not on an acronym. */
  help: string;
}

export const TAX_CATEGORIES: TaxCategoryOption[] = [
  {
    code: TAX_CATEGORY_STANDARD,
    label: 'Standard rated',
    help: 'Taxed at your business unit’s output tax rate.',
  },
  {
    code: TAX_CATEGORY_ZERO_RATED_EXPORT,
    label: 'Zero-rated export',
    help: 'Exported goods taxed at 0%. Keep the proof of export.',
  },
  {
    code: TAX_CATEGORY_EXEMPT,
    label: 'Exempt',
    help: 'An exempt supply. No output tax, and no input tax recovery against it.',
  },
  {
    code: TAX_CATEGORY_OUT_OF_SCOPE_RCM,
    label: 'Reverse charge / out of scope',
    help: 'The customer accounts for the tax, not you.',
  },
];

export const taxCategoryLabel = (code: string | null | undefined): string =>
  TAX_CATEGORIES.find((option) => option.code === (code || TAX_CATEGORY_STANDARD))?.label
  ?? (code || TAX_CATEGORY_STANDARD);

/** Anything other than standard rated is an assertion that needs evidence behind it. */
export const taxCategoryRequiresReason = (code: string | null | undefined): boolean =>
  (code || TAX_CATEGORY_STANDARD) !== TAX_CATEGORY_STANDARD;
