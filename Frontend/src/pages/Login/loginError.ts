import { presentableErrorMessage } from '../../utils/apiErrors';

const isRecord = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object';

/** Anonymous sign-in has different recovery instructions from an expired app session. */
export function loginErrorMessage(error: unknown): string {
  const response = isRecord(error) && isRecord(error.response) ? error.response : undefined;
  const status = response?.status;
  const problemType = isRecord(response?.data) ? response.data.type : undefined;

  if (status === 401) {
    return 'The email or password was not accepted. Check your details and try again.';
  }
  if (status === 403) {
    if (problemType === 'https://nexora.invalid/problems/tenant-not-activated') {
      return 'Your workspace is not active yet. Ask your platform administrator to complete activation.';
    }
    if (problemType === 'https://nexora.invalid/problems/tenant-suspended') {
      return 'Your organization’s access is restricted. Contact your administrator to restore access.';
    }
    return 'Sign-in is not permitted for this account. Contact your administrator.';
  }
  if (status === 429) {
    return 'Too many sign-in attempts. Wait before trying again.';
  }
  if (status === 503 && problemType === 'https://nexora.invalid/problems/tenant-access-unresolvable') {
    return 'Nexora could not confirm your workspace status. Please try again shortly.';
  }
  // Keep the shared payload-safety boundary: HTML, objects and diagnostic dumps are never UI copy.
  return presentableErrorMessage(error, 'Sign-in could not be completed. Please try again shortly.');
}
