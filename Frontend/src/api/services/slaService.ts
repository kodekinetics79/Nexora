import axiosInstance from '../axiosInstance';

// Per-business-unit SLA / deadline policy (WP-A2). GET returns the stored row
// or the server-side conservative default; PUT patches only supplied fields.
export interface SlaPolicyDTO {
  businessUnitId: number;
  unassignedHours: number;
  warnDaysBeforeClose: number;
  criticalDaysBeforeClose: number;
  staleQuoteDays: number;
  quoteAutoExpireDays: number;
  approvalEscalationHours: number;
  deadlineBufferHours: number;
  /** FR-QTM-07 trigger 2: days after submission with no customer response. */
  quoteNoResponseExpiryDays: number;
  /** FR-SPO-07: working days before a committed ship date to remind the buyer. */
  supplierShipDateReminderDays: number;
  /** FR-SPO-07: working hours without a supplier acknowledgement before escalating. */
  supplierAckEscalationHours: number;
  /**
   * FR-SBF-01: working days before a bid closes at which RFQ lines still carrying no
   * Quote/No-Quote decision are chased. 0 means NOT CONFIGURED — the sweep sends nothing —
   * rather than "chase immediately".
   */
  quoteDecisionReminderDays: number;
}

export type SlaPolicyUpdate = Partial<Omit<SlaPolicyDTO, 'businessUnitId'>>;

const slaService = {
  getPolicy: async (): Promise<SlaPolicyDTO> => {
    const { data } = await axiosInstance.get('/api/sla/policy');
    return data;
  },

  updatePolicy: async (update: SlaPolicyUpdate): Promise<SlaPolicyDTO> => {
    const { data } = await axiosInstance.put('/api/sla/policy', update);
    return data;
  },
};

export default slaService;
