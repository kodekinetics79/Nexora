import axiosInstance from '../axiosInstance';

export interface SetupMasterDTO {
    setupId: number;
    setupType: string;
    setupCode: string | null;
    setupName: string;
    description: string | null;
    parentSetupId: number | null;
    /**
     * Authority tier of a ROLE row (`RoleRanks`: 0 Member, 10 Manager, 20 Admin, 30 Owner).
     * Always 0 on lookup rows. Optional here only so payloads cached by an older build still type.
     */
    roleRank?: number;
    businessUnitId?: number;
    isActive: boolean;
    createdBy?: string;
    createdOn?: string;
    modifiedBy?: string;
    modifiedOn?: string;
}

export interface SetupMasterCreateDTO {
    setupType: string;
    setupCode?: string;
    setupName: string;
    description?: string;
    parentSetupId?: number | null;
    /** Role rows only. Omitted → the server stores Member (0); any value on a non-role row is a 400. */
    roleRank?: number;
    isActive?: boolean;
    createdBy: string;
}

export interface SetupMasterUpdateDTO {
    setupType: string;
    setupCode?: string;
    setupName: string;
    description?: string;
    parentSetupId?: number | null;
    /** Role rows only. Omitted → the stored tier is kept, so a save cannot silently demote a role. */
    roleRank?: number;
    isActive: boolean;
    modifiedBy: string;
}

export interface PaginatedSetupResponse {
    items: SetupMasterDTO[];
    totalItems: number;
    pageNumber: number;
    pageSize: number;
    totalPages: number;
}

const setupService = {
    getAll: async (params: { setupType?: string; pageNumber?: number; pageSize?: number; setupCode?: string; setupName?: string; isActive?: boolean | null }) => {
        const response = await axiosInstance.get<PaginatedSetupResponse>(`/api/SetupMaster`, { params });
        return response.data;
    },

    getById: async (id: number) => {
        const response = await axiosInstance.get<SetupMasterDTO>(`/api/SetupMaster/${id}`);
        return response.data;
    },

    create: async (data: SetupMasterCreateDTO) => {
        const response = await axiosInstance.post<SetupMasterDTO>(`/api/SetupMaster`, data);
        return response.data;
    },

    update: async (id: number, data: SetupMasterUpdateDTO) => {
        const response = await axiosInstance.put(`/api/SetupMaster/${id}`, data);
        return response.data;
    },

    delete: async (id: number) => {
        const response = await axiosInstance.delete(`/api/SetupMaster/${id}`);
        return response.data;
    }
};

export default setupService;
