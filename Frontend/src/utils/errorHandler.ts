import { enqueueSnackbar } from 'notistack';
import { toPresentableError } from './apiErrors';

/**
 * Shared snackbar error handler.
 *
 * The exported signature is unchanged so existing call sites need no edit, but the presentation
 * decision now belongs to `toPresentableError`. Previously this function rendered `data.message`
 * without checking it was a string and fell through to `error.message` — which put axios transport
 * text and the API hostname in front of users.
 */
export const handleApiError = (error: unknown): void => {
  const presented = toPresentableError(error);
  enqueueSnackbar(presented.message, {
    variant: presented.severity === 'info' ? 'info' : presented.severity,
  });
};
