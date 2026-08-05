import axiosInstance from '../axiosInstance';

export interface RolePermissionDTO {
  id: number;
  roleId: number;
  roleName?: string;
  moduleId: number;
  moduleName?: string;
  businessUnitId: number;
  /**
   * Read access. Before the `CanView` column existed the mere existence of a row was the read
   * grant, so an "all false" row silently granted read. `canView` makes the grant explicit; a row
   * with all four flags false now means NO access and is retained only for provenance.
   * Optional on the wire so a pre-migration backend (which omits the field) is still parseable —
   * every consumer must treat `undefined` as "unknown, assume false" rather than "granted".
   */
  canView?: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  /**
   * Server-owned provenance. RESPONSE ONLY — never send these back. Attribution is derived from
   * the caller's claims server-side; a client-supplied value is ignored by the API and, before it
   * was ignored, was forgeable. `stripServerOwnedFields` enforces this on every write.
   */
  createdOn?: string;
  createdBy?: string;
  modifiedOn?: string;
  modifiedBy?: string;
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

/** One module's complete desired grant. Every flag is explicit — there is no "leave unchanged". */
export interface RolePermissionBulkEntry {
  moduleId: number;
  canView: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
}

/**
 * Bulk apply. `businessUnitId` is deliberately absent: the server takes it from the caller's
 * claim and ignores anything in the body, so putting it here would only invite the belief that
 * the client chooses the tenant.
 */
export interface RolePermissionBulkRequest {
  roleId: number;
  reason?: string;
  entries: RolePermissionBulkEntry[];
}

export interface RolePermissionBulkResult {
  applied: number;
  created: number;
  updated: number;
}

/**
 * Removes fields the server owns from an outgoing write payload.
 *
 * The permission matrix edits a row it fetched, so `{ ...existing, canEdit: true }` would echo the
 * server's own `createdBy`/`modifiedBy` back at it. The API ignores them now, but sending
 * attribution the client cannot vouch for is exactly the pattern that made provenance forgeable.
 */
const stripServerOwnedFields = <T extends Record<string, unknown>>(data: T): Partial<T> => {
  const { createdBy, createdOn, modifiedBy, modifiedOn, ...rest } = data ?? {};
  void createdBy; void createdOn; void modifiedBy; void modifiedOn;
  return rest as Partial<T>;
};

const rolePermissionService = {
  getAll: async (params: RolePermissionFilters) => {
    const response = await axiosInstance.get<RolePermissionPaginatedResponse>('/api/RolePermission', { params });
    return response.data;
  },

  getById: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.get<RolePermissionDTO>(`/api/RolePermission/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  create: async (data: Record<string, unknown>) => {
    const response = await axiosInstance.post<RolePermissionDTO>('/api/RolePermission', stripServerOwnedFields(data));
    return response.data;
  },

  update: async (id: number, data: Record<string, unknown>) => {
    const response = await axiosInstance.put(`/api/RolePermission/${id}`, stripServerOwnedFields(data));
    return response.data;
  },

  delete: async (id: number, businessUnitId?: number) => {
    const response = await axiosInstance.delete(`/api/RolePermission/${id}`, { params: { businessUnitId } });
    return response.data;
  },

  /**
   * Applies a whole column / whole matrix change in ONE transactional request. The per-module loop
   * this replaces issued ~51 sequential writes: any denial part-way left the role half-configured,
   * and the UI still reported success. The server applies all entries or none.
   */
  bulkApply: async (data: RolePermissionBulkRequest) => {
    const response = await axiosInstance.post<RolePermissionBulkResult>('/api/RolePermission/bulk', data);
    return response.data;
  },

  getPermissionsByRole: async (roleId: number, businessUnitId: number) => {
    const response = await axiosInstance.get<RolePermissionPaginatedResponse>('/api/RolePermission', {
      params: { roleId, businessUnitId, pageSize: 1000 }
    });
    return response.data.items;
  }
};

export default rolePermissionService;
