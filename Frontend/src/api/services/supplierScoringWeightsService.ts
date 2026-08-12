import axiosInstance from '../axiosInstance';

/**
 * FR-QTM-03 · the four weights that decide which supplier offer the comparison recommends.
 *
 * There is exactly one weight set per business unit — no per-customer, per-category or per-RFQ
 * override, and no inheritance chain to reason about. The four numbers must total 100 so the score
 * a buyer reads is always out of a fixed ceiling, which is what makes "Weighted 82/100" a statement
 * anyone can check.
 *
 * Governed exactly like the commercial policy on the same screen: the business unit comes from the
 * JWT claim server-side and is never sent from here, a reason is mandatory, and the write carries an
 * Idempotency-Key because it appends to the tenant governance ledger.
 */
export interface SupplierScoringWeightsDTO {
  businessUnitId: number;
  /** 0-100. The landed cost, consumed as one number — never decomposed into duty/freight. */
  priceWeight: number;
  leadTimeWeight: number;
  /**
   * Scored from the warranty months on the supplier quote line. Defaults to 0 because that number is
   * new in this release and is blank on every line captured before it, and a missing criterion is
   * never guessed. The customer raises it once the months are being recorded.
   */
  warrantyWeight: number;
  paymentTermsWeight: number;
  version: number;
  modifiedOn: string | null;
  modifiedBy: string | null;
  /** True while the tenant has never saved these weights and is running on the server defaults. */
  isDefault: boolean;
  /** The total the four weights must reach, served so no client hardcodes 100. */
  requiredTotal: number;
}

/**
 * All four weights are sent on every save. They are one setting, not four: a partial update could
 * leave the set totalling something other than 100, and a score out of an unknown ceiling explains
 * nothing.
 */
export interface SupplierScoringWeightsUpdate {
  priceWeight: number;
  leadTimeWeight: number;
  warrantyWeight: number;
  paymentTermsWeight: number;
  /** Mandatory. The server refuses the write without it and records it in the audit trail. */
  reason: string;
}

const supplierScoringWeightsService = {
  getWeights: async (): Promise<SupplierScoringWeightsDTO> => {
    const { data } = await axiosInstance.get('/api/supplier-scoring-weights');
    return data;
  },

  updateWeights: async (update: SupplierScoringWeightsUpdate): Promise<SupplierScoringWeightsDTO> => {
    const { data } = await axiosInstance.put('/api/supplier-scoring-weights', update, {
      headers: { 'Idempotency-Key': `supplier-scoring-weights:${crypto.randomUUID()}` },
    });
    return data;
  },
};

export default supplierScoringWeightsService;
