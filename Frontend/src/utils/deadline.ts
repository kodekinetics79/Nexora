import { calendarDaysUntil } from './dates';

export type DeadlineTone = 'late' | 'soon' | 'near' | 'calm' | 'none';

/**
 * A deadline the way a rep reads it: "3 days left", "Due today", "7 days late". Counted in whole
 * calendar days, so a deadline of today reads "Due today" all day long.
 */
export function deadlineWords(dateStr: string | null | undefined, now: Date = new Date()): { text: string; tone: DeadlineTone } {
  // The shared count (see calendarDaysUntil): the deadline's own day, not the day it lands on
  // once moved into the reader's zone.
  const days = calendarDaysUntil(dateStr, now);
  if (days == null) return { text: 'No deadline', tone: 'none' };
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
