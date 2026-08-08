import platformHttp from '../api/platformHttp';

export interface PlatformMfaStatus {
  enabled: boolean;
  enabledAtUtc: string | null;
  recoveryCodesRemaining: number;
}

export interface PlatformMfaEnrollment {
  secret: string;
  otpAuthUri: string;
}

export interface PlatformMfaEnrollmentResult {
  enabledAtUtc: string;
  recoveryCodes: string[];
}

export const getPlatformMfaStatus = async (): Promise<PlatformMfaStatus> =>
  (await platformHttp.get<PlatformMfaStatus>('/api/platform/auth/mfa')).data;

export const beginPlatformMfaEnrollment = async (): Promise<PlatformMfaEnrollment> =>
  (await platformHttp.post<PlatformMfaEnrollment>('/api/platform/auth/mfa/enrollment')).data;

export const confirmPlatformMfaEnrollment = async (totpCode: string): Promise<PlatformMfaEnrollmentResult> =>
  (await platformHttp.post<PlatformMfaEnrollmentResult>('/api/platform/auth/mfa/enrollment/confirm', { totpCode })).data;
