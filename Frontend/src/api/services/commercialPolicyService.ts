import axiosInstance from '../axiosInstance';

/**
 * The per-business-unit commercial policy: how much supplier input tax is recoverable, the rate
 * output tax is derived at, and the tolerances that decide whether a customer PO differs from what
 * was quoted.
 *
 * Every one of these was previously unreachable — the row existed, nothing could write it, and
 * every tenant silently ran the KSA defaults. Tenant scoping is implicit: the business unit comes
 * from the JWT claim server-side and is never sent from here.
 */
export interface CommercialPolicyDTO {
  businessUnitId: number;
  /** 0-100. 100 = fully recoverable, so none of the supplier's tax is a cost of the goods. */
  supplierInputTaxRecoverablePercent: number;
  /** 0-100, or null meaning "no rate stated" — which blocks every quote send until one is set. */
  outputTaxRatePercent: number | null;
  priceTolerancePercent: number;
  priceToleranceMinimumAmount: number;
  quantityTolerancePercent: number;
  version: number;
  modifiedOn: string | null;
  modifiedBy: string | null;
  /** True while the tenant has never saved this policy and is running on the server defaults. */
  isDefault: boolean;
  /** The tax categories a quote line may carry, served so no client hardcodes the list. */
  taxCategories: string[];
}

/**
 * Patch semantics: an omitted field is unchanged. `outputTaxRatePercent: null` therefore means
 * "leave it alone" and NOT "erase it" — erasing is the separate, explicit `clearOutputTaxRate`,
 * because one null on the wire cannot mean both things.
 */
export interface CommercialPolicyUpdate {
  supplierInputTaxRecoverablePercent?: number | null;
  outputTaxRatePercent?: number | null;
  clearOutputTaxRate?: boolean;
  priceTolerancePercent?: number | null;
  priceToleranceMinimumAmount?: number | null;
  quantityTolerancePercent?: number | null;
  /** Mandatory. The server refuses the write without it and records it in the audit trail. */
  reason: string;
}

const commercialPolicyService = {
  getPolicy: async (): Promise<CommercialPolicyDTO> => {
    const { data } = await axiosInstance.get('/api/commercial-policy');
    return data;
  },

  updatePolicy: async (update: CommercialPolicyUpdate): Promise<CommercialPolicyDTO> => {
    const { data } = await axiosInstance.put('/api/commercial-policy', update, {
      // The write appends to the tenant governance ledger, so a retried save has to record one
      // change and not two.
      headers: { 'Idempotency-Key': `commercial-policy:${crypto.randomUUID()}` },
    });
    return data;
  },
};

export default commercialPolicyService;
