import axiosInstance from '../axiosInstance';

export interface RolePermissionDTO {
  id: number;
  roleId: number;
  roleName?: string;
  moduleId: number;
  moduleName?: string;
  businessUnitId: number;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  createdOn?: string;
  createdBy?: string;
}

export interface RolePermissionPaginatedResponse {
  items: RolePermissionDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

export interface RolePermissionFilters {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  roleId?: number;
  moduleId?: number;
}

const rolePermissionService = {
  getAll: async (params: RolePermissionFilters) => {
    const response = await axiosInstance.get<RolePermissionPaginatedResponse>('/api/RolePermission', { params });
    return response.data;
  },

  getById: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.get<RolePermissionDTO>(`/api/RolePermission/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  create: async (data: any) => {
    const response = await axiosInstance.post<RolePermissionDTO>('/api/RolePermission', data);
    return response.data;
  },

  update: async (id: number, data: any) => {
    const response = await axiosInstance.put(`/api/RolePermission/${id}`, data);
    return response.data;
  },

  delete: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.delete(`/api/RolePermission/${id}`, { params: { businessUnitId } });
    return response.data;
  }
};

export default rolePermissionService;
