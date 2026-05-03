import axiosInstance from '../axiosInstance';

export interface BusinessUnitDTO {
  id: number;
  businessUnitCode: string;
  businessUnitName: string;
  description?: string;
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

  delete: async (id: number) => {
    const response = await axiosInstance.delete(`/api/BusinessUnit/${id}`);
    return response.data;
  }
};

export default businessUnitService;
