/**
 * Delivery time is stored as a number of days. It is said in the largest whole unit: a month is
 * 30 days, so 60 → "2 months", 28 → "4 weeks", 10 → "10 days". The server's QuoteService.DeliveryText
 * prints the PDF with the same rule, so the screen and the customer's copy never disagree.
 */
export type DeliveryUnit = 'days' | 'weeks' | 'months';

export const DAYS_PER: Record<DeliveryUnit, number> = { days: 1, weeks: 7, months: 30 };

/** The unit a stored number of days is best said in. */
export const deliveryUnitFor = (days: number): DeliveryUnit =>
  days > 0 && days % 30 === 0 ? 'months' : days > 0 && days % 7 === 0 ? 'weeks' : 'days';

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? '' : 's'}`;

/** 60 → "2 months", 7 → "1 week", 10 → "10 days". */
export const deliveryText = (days: number): string => {
  const unit = deliveryUnitFor(days);
  if (unit === 'months') return plural(days / 30, 'month');
  if (unit === 'weeks') return plural(days / 7, 'week');
  return plural(days, 'day');
};

/** Short form for tight spaces: "2 mo", "3 wk", "10 days". */
export const deliveryShortText = (days: number): string => {
  const unit = deliveryUnitFor(days);
  if (unit === 'months') return `${days / 30} mo`;
  if (unit === 'weeks') return `${days / 7} wk`;
  return plural(days, 'day');
};
