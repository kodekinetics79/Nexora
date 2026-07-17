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
