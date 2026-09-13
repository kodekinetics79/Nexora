import { Alert, type SxProps, type Theme } from '@mui/material';
import dayjs from 'dayjs';

export interface RefreshFailedNoticeProps {
  /** `dataUpdatedAt` of the query whose last good answer is still on screen. */
  updatedAt: number;
  /** What happens next, in the reader's words. */
  retryHint?: string;
  sx?: SxProps<Theme>;
}

/**
 * A background re-read failed, but what was already on screen is still true enough to work from.
 *
 * TanStack Query keeps the previous data when a refetch fails and reports `isError` alongside it.
 * Screens that tested `isError` first threw that data away and swapped the whole list for an error
 * panel — every backend deploy made self-refreshing screens blink to an error and back. This line
 * says the refresh failed without taking anything away. It is a polite status, not an alert: the
 * reader's work is not in danger.
 */
export default function RefreshFailedNotice({
  updatedAt,
  retryHint = 'It will try again on its own.',
  sx,
}: RefreshFailedNoticeProps) {
  const time = updatedAt > 0 ? dayjs(updatedAt).format('HH:mm') : null;
  return (
    <Alert severity="warning" variant="outlined" role="status" sx={{ mb: 1.5, py: 0, ...sx }}>
      {`Couldn't refresh just now${time ? ` — showing what was loaded at ${time}` : ''}. ${retryHint}`}
    </Alert>
  );
}
