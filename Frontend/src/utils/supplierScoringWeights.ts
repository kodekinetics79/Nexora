/**
 * FR-QTM-03 · the four weights behind every supplier recommendation, and the rules the Commercial
 * Policy screen enforces before one can be saved.
 *
 * Held here rather than inside the page because these numbers are the control itself. "Cheapest
 * wins" has to stay exactly 100/0/0/0 — it is the behaviour this product had before weights
 * existed, and a customer who does not want the recommendation to change must be able to say so in
 * one click. A silent drift in that preset would silently re-rank every supplier comparison in
 * every tenant that chose it, which is precisely the kind of change a test should refuse to allow.
 *
 * Weights are carried as strings so a half-typed value does not become NaN mid-keystroke.
 */
export const WEIGHT_TOTAL = 100;

export interface SupplierWeightsForm {
  priceWeight: string;
  leadTimeWeight: string;
  warrantyWeight: string;
  paymentTermsWeight: string;
}

export interface WeightCriterion {
  key: keyof SupplierWeightsForm;
  label: string;
  helper: string;
}

/** Exactly four criteria, per FR-QTM-03. Coverage is a tiebreak, not a weight, and is not here. */
export const WEIGHT_CRITERIA: WeightCriterion[] = [
  {
    key: 'priceWeight',
    label: 'Price',
    helper: 'The landed cost of the offer — the price with freight, duty and conformity already in it.',
  },
  {
    key: 'leadTimeWeight',
    label: 'Lead time',
    helper: 'Days the supplier quoted. Raise this when a delivery date is the binding constraint.',
  },
  {
    key: 'warrantyWeight',
    label: 'Warranty',
    helper: 'Warranty is captured as free text today, so leave this at zero until it is a number.',
  },
  {
    key: 'paymentTermsWeight',
    label: 'Payment terms',
    helper: 'The credit days recorded on the supplier. Longer credit scores higher.',
  },
];

export interface WeightPreset {
  id: string;
  label: string;
  caption: string;
  weights: SupplierWeightsForm;
}

export const WEIGHT_PRESETS: WeightPreset[] = [
  {
    id: 'CHEAPEST',
    label: 'Cheapest wins',
    caption: 'Ranks on landed cost alone. This is exactly how supplier offers were ordered before weights existed.',
    weights: { priceWeight: '100', leadTimeWeight: '0', warrantyWeight: '0', paymentTermsWeight: '0' },
  },
  {
    id: 'BALANCED',
    label: 'Balanced',
    caption: 'Cost leads, but a materially shorter lead time can still change the order. This is the starting point for a new account.',
    weights: { priceWeight: '80', leadTimeWeight: '20', warrantyWeight: '0', paymentTermsWeight: '0' },
  },
  {
    id: 'SPEED',
    label: 'Speed matters',
    caption: 'Lead time outweighs price. For work where a missed delivery date costs more than the goods.',
    weights: { priceWeight: '40', leadTimeWeight: '60', warrantyWeight: '0', paymentTermsWeight: '0' },
  },
];

/** Null for anything that is not a finite number, so a blank box is an error and never a zero. */
export const parseWeight = (raw: string): number | null => {
  const trimmed = raw.trim();
  if (trimmed === '') return null;
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : null;
};

export const weightTotal = (weights: SupplierWeightsForm): number =>
  WEIGHT_CRITERIA.reduce((total, { key }) => total + (parseWeight(weights[key]) ?? 0), 0);

export const weightFieldError = (raw: string): string | null => {
  const value = parseWeight(raw);
  if (value === null) return 'Enter a whole number between 0 and 100.';
  if (!Number.isInteger(value)) return 'Weights are whole numbers.';
  if (value < 0 || value > WEIGHT_TOTAL) return 'Must be between 0 and 100.';
  return null;
};

export const sameWeights = (left: SupplierWeightsForm, right: SupplierWeightsForm): boolean =>
  WEIGHT_CRITERIA.every(({ key }) => parseWeight(left[key]) === parseWeight(right[key]));

/** The preset these numbers are, or undefined — which the screen shows as "Custom". */
export const matchingPreset = (weights: SupplierWeightsForm): WeightPreset | undefined =>
  WEIGHT_PRESETS.find((preset) => sameWeights(preset.weights, weights));

/**
 * The sentence under the running total. Null when the four numbers are individually valid and
 * total 100 — the only state in which a save is allowed.
 */
export const weightTotalError = (weights: SupplierWeightsForm): string | null => {
  if (WEIGHT_CRITERIA.some(({ key }) => weightFieldError(weights[key]) !== null)) return null;
  const total = weightTotal(weights);
  return total === WEIGHT_TOTAL ? null : `The four weights total ${total}. They must total 100.`;
};
