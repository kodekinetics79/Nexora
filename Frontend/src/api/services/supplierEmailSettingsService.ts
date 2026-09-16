import axiosInstance from '../axiosInstance';

/**
 * The wording around a supplier RFQ email: subject, greeting, opening sentence, default message
 * and sign-off. The part lines in the middle are always written by Nexora and are not part of
 * these settings.
 *
 * Any field may contain `[Supplier name]`, `[Company name]` and `[RFQ number]`; the server fills
 * them in for each email. A null field means "use the Nexora default".
 */
export interface SupplierEmailTexts {
  subject: string;
  greeting: string;
  opening: string;
  defaultMessage: string;
  signOff: string;
}

export type SupplierEmailField = keyof SupplierEmailTexts;

export type SupplierEmailCompanyUpdate = { [K in SupplierEmailField]: string | null };

export interface SupplierEmailCompanySettings extends SupplierEmailCompanyUpdate {
  /** The Nexora defaults used for any field left blank. */
  defaults: SupplierEmailTexts;
  updatedBy: string | null;
  updatedOn: string | null;
}

export interface SupplierEmailMineUpdate {
  defaultMessage: string | null;
  signOff: string | null;
}

export interface SupplierEmailMineSettings extends SupplierEmailMineUpdate {
  /** The company's effective texts (defaults applied), used when the user's own are null. */
  company: { defaultMessage: string; signOff: string };
}

export const SUPPLIER_EMAIL_PLACEHOLDERS = ['[Supplier name]', '[Company name]', '[RFQ number]'] as const;

const unwrap = <T>(response: { data: T }): T => response.data;

const BASE = '/api/supplier-email-settings';

const supplierEmailSettingsService = {
  getCompany: async (): Promise<SupplierEmailCompanySettings> =>
    unwrap(await axiosInstance.get<SupplierEmailCompanySettings>(`${BASE}/company`)),

  saveCompany: async (body: SupplierEmailCompanyUpdate): Promise<SupplierEmailCompanySettings> =>
    unwrap(await axiosInstance.put<SupplierEmailCompanySettings>(`${BASE}/company`, body)),

  getMine: async (): Promise<SupplierEmailMineSettings> =>
    unwrap(await axiosInstance.get<SupplierEmailMineSettings>(`${BASE}/mine`)),

  saveMine: async (body: SupplierEmailMineUpdate): Promise<SupplierEmailMineSettings> =>
    unwrap(await axiosInstance.put<SupplierEmailMineSettings>(`${BASE}/mine`, body)),

  getEffective: async (): Promise<SupplierEmailTexts> =>
    unwrap(await axiosInstance.get<SupplierEmailTexts>(`${BASE}/effective`)),
};

/** Replaces the three placeholders with sample or real values. */
export const fillSupplierEmailPlaceholders = (
  text: string,
  values: { supplierName: string; companyName: string; rfqNumber: string },
): string =>
  text
    .split('[Supplier name]').join(values.supplierName)
    .split('[Company name]').join(values.companyName)
    .split('[RFQ number]').join(values.rfqNumber);

export default supplierEmailSettingsService;

/** The line Nexora always adds after the default message, asking the supplier what to send back. */
export const SUPPLIER_EMAIL_REPLY_LINE =
  'Please reply to this email with your price, availability, lead time and how long your price is valid.';
