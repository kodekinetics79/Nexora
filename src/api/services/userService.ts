import axiosInstance from '../axiosInstance';

export interface RoleDTO {
  setupId: number;
  setupName: string;
}

export interface UserDTO {
  id: number;
  firstName: string;
  middleName?: string;
  lastName: string;
  email: string;
  imageUrl?: string;
  roleId: number;
  roleName?: string;
  teamId?: number;
  teamName?: string;
  timezone?: string;
  region?: string;
  managerId?: number;
  buid: number;
  businessUnitName?: string;
  userGroupId?: number;
  userGroupName?: string;
  isActive: boolean;
  lastLogin?: string;
  createdOn?: string;
  createdBy?: string;
}

export interface UserPaginatedResponse {
  items: UserDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

export interface UserFilters {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  userName?: string;
  email?: string;
  roleId?: number;
  isActive?: boolean;
}

const userService = {
  getAll: async (params: UserFilters) => {
    const response = await axiosInstance.get<UserPaginatedResponse>('/api/User', { params });
    return response.data;
  },

  getById: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.get<UserDTO>(`/api/User/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  create: async (formData: FormData) => {
    const response = await axiosInstance.post<UserDTO>('/api/User', formData, {
      headers: { 'Content-Type': 'multipart/form-data' }
    });
    return response.data;
  },

  update: async (id: number, formData: FormData) => {
    const response = await axiosInstance.put(`/api/User/${id}`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' }
    });
    return response.data;
  },

  delete: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.delete(`/api/User/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  changePassword: async (id: number, data: any) => {
    const response = await axiosInstance.post(`/api/User/${id}/ChangePassword`, data);
    return response.data;
  },

  getRoles: async () => {
    const response = await axiosInstance.get<RoleDTO[]>('/api/User/Roles');
    return response.data;
  },

  getTeams: async (businessUnitId?: number) => {
    const response = await axiosInstance.get<any[]>('/api/User/Teams', { params: { businessUnitId } });
    return response.data;
  },

  getUserGroups: async (businessUnitId?: number) => {
    const response = await axiosInstance.get<any[]>('/api/User/UserGroups', { params: { businessUnitId } });
    return response.data;
  },

  getBusinessUnits: async () => {
    const response = await axiosInstance.get<any[]>('/api/User/BusinessUnits');
    return response.data;
  }
};

export default userService;
