import axiosInstance from '../axiosInstance';

/** Whether Nexora read the whole document behind a request. */
export interface LeadReadCompletenessDTO {
  /** True when part of the document was not read. */
  partial: boolean;
  /** Lines Nexora read. */
  linesRead?: number | null;
  /** About how many lines the document prints, when its pages show it. */
  linesExpected?: number | null;
  /** Parts of the document the reading could not get through. */
  partsUnread: number;
}

const leadReadCompletenessService = {
  get: async (leadId: number): Promise<LeadReadCompletenessDTO> => {
    const response = await axiosInstance.get<LeadReadCompletenessDTO>(`/api/leads/${leadId}/read-completeness`);
    return response.data;
  },
};

export default leadReadCompletenessService;
