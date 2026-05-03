import axiosInstance from '../axiosInstance';

export interface ModuleDTO {
  id: number;
  moduleName: string;
  description?: string;
  isActive: boolean;
  createdOn?: string;
  createdBy?: string;
}

export interface ModulePaginatedResponse {
  items: ModuleDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

const moduleService = {
  getAll: async (params?: any) => {
    const response = await axiosInstance.get<ModulePaginatedResponse>('/api/Module', { params });
    return response.data;
  },

  getById: async (id: number) => {
    const response = await axiosInstance.get<ModuleDTO>(`/api/Module/${id}`);
    return response.data;
  },

  create: async (data: any) => {
    const response = await axiosInstance.post<ModuleDTO>('/api/Module', data);
    return response.data;
  },

  update: async (id: number, data: any) => {
    const response = await axiosInstance.put(`/api/Module/${id}`, data);
    return response.data;
  },

  delete: async (id: number) => {
    const response = await axiosInstance.delete(`/api/Module/${id}`);
    return response.data;
  }
};

export default moduleService;
