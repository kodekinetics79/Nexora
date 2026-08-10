import axiosInstance from '../axiosInstance';

export interface BusinessUnitDTO {
  id: number;
  businessUnitCode: string;
  businessUnitName: string;
  description?: string;
  /**
   * This entity's own VAT/tax registration number — the claimant on any input-tax reclaim, and the
   * seller VAT number on its outgoing tax invoices. Distinct from the SaaS control-plane tenant's
   * tax number, which identifies who pays for Nexora.
   */
  taxRegistrationNumber?: string;
  isActive: boolean;
  createdOn?: string;
  createdBy?: string;
  modifiedOn?: string;
  modifiedBy?: string;
}

export interface BusinessUnitPaginatedResponse {
  items: BusinessUnitDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

const businessUnitService = {
  getAll: async (params?: any) => {
    const response = await axiosInstance.get<BusinessUnitPaginatedResponse>('/api/BusinessUnit', { params });
    return response.data;
  },

  getById: async (id: number) => {
    const response = await axiosInstance.get<BusinessUnitDTO>(`/api/BusinessUnit/${id}`);
    return response.data;
  },

  getDropdown: async () => {
    const response = await axiosInstance.get<BusinessUnitDTO[]>('/api/BusinessUnit/Dropdown');
    return response.data;
  },

  create: async (data: any) => {
    const response = await axiosInstance.post<BusinessUnitDTO>('/api/BusinessUnit', data);
    return response.data;
  },

  update: async (id: number, data: any) => {
    const response = await axiosInstance.put(`/api/BusinessUnit/${id}`, data);
    return response.data;
  },

  /**
   * Sets (or clears, with null) this business unit's own tax registration number.
   *
   * Separate from `update` because that route is control-plane-only and forbids tenant callers:
   * code, name and activation state are provisioning facts. A VAT registration is not — it is a
   * statutory identifier only the trading entity can state, and without it the entity deducting
   * recoverable input VAT from landed cost cannot name itself as the claimant.
   */
  updateTaxRegistration: async (id: number, taxRegistrationNumber: string | null) => {
    const response = await axiosInstance.put<BusinessUnitDTO>(
      `/api/BusinessUnit/${id}/tax-registration`,
      { taxRegistrationNumber },
    );
    return response.data;
  },

  delete: async (id: number) => {
    const response = await axiosInstance.delete(`/api/BusinessUnit/${id}`);
    return response.data;
  }
};

export default businessUnitService;
