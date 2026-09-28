import { parseDateSafe } from './dates';

export type DeadlineTone = 'late' | 'soon' | 'near' | 'calm' | 'none';

/**
 * A deadline the way a rep reads it: "3 days left", "Due today", "7 days late". Counted in whole
 * calendar days, so a deadline of today reads "Due today" all day long.
 */
export function deadlineWords(dateStr: string | null | undefined, now: Date = new Date()): { text: string; tone: DeadlineTone } {
  const due = parseDateSafe(dateStr);
  if (!due) return { text: 'No deadline', tone: 'none' };
  const day = (d: Date) => Date.UTC(d.getFullYear(), d.getMonth(), d.getDate());
  const days = Math.round((day(due) - day(now)) / 86_400_000);
  if (days < 0) return { text: `${-days} ${-days === 1 ? 'day' : 'days'} late`, tone: 'late' };
  if (days === 0) return { text: 'Due today', tone: 'soon' };
  if (days === 1) return { text: 'Due tomorrow', tone: 'soon' };
  return { text: `${days} days left`, tone: days <= 2 ? 'soon' : days <= 7 ? 'near' : 'calm' };
}

/** Colour only reinforces the words. */
export const DEADLINE_COLOR: Record<DeadlineTone, string> = {
  late: 'error.main',
  soon: 'error.main',
  near: 'warning.dark',
  calm: 'text.secondary',
  none: 'text.disabled',
};
