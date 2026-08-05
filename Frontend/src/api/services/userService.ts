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
  /**
   * Server-owned provenance. RESPONSE ONLY — `handleSave` builds its FormData field by field and
   * deliberately never appends CreatedBy/ModifiedBy: attribution comes from the caller's claims.
   */
  createdOn?: string;
  createdBy?: string;
  modifiedOn?: string;
  modifiedBy?: string;
}

/** One module's effective grant for the signed-in caller. */
export interface MePermissionDTO {
  moduleId: number;
  moduleName: string;
  canView: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
}

/**
 * Response of `GET /api/User/me/permissions`.
 *
 * Authoritative identity + grants for the CALLER only. `roleId` / `businessUnitId` are echoed from
 * the token claims — the client never supplies them — so this is also the single source of truth
 * for `isSuperAdmin`, replacing the separately-derived value that could disagree with the server.
 */
export interface MePermissionsResponse {
  userId: number;
  roleId: number;
  roleName: string;
  businessUnitId: number;
  isSuperAdmin: boolean;
  isManager: boolean;
  permissions: MePermissionDTO[];
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

  /**
   * Loads the caller's OWN identity and grants.
   *
   * Authenticated-only by contract — reading your own permissions is not a privileged act. The
   * previous bootstrap read the whole role-permission table via an endpoint gated on
   * "Roles & Permissions: View", so any role that had not yet been granted that module got a 403,
   * ended up with zero permissions, and saw an empty sidebar plus Access Denied everywhere. That
   * failure is unrecoverable by the user, so callers must surface it rather than defaulting to [].
   */
  getMyPermissions: async () => {
    const response = await axiosInstance.get<MePermissionsResponse>('/api/User/me/permissions');
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
