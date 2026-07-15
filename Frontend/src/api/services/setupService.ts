import axiosInstance from '../axiosInstance';

export interface SetupMasterDTO {
    setupId: number;
    setupType: string;
    setupCode: string | null;
    setupName: string;
    description: string | null;
    parentSetupId: number | null;
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
    isActive?: boolean;
    createdBy: string;
}

export interface SetupMasterUpdateDTO {
    setupType: string;
    setupCode?: string;
    setupName: string;
    description?: string;
    parentSetupId?: number | null;
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
