import axiosInstance from '../axiosInstance';

export interface StateDTO {
    stateId: number;
    stateCode: string;
    stateName: string;
    countryId: number;
    countryName?: string;
    buid: number;
    description: string | null;
    isActive: boolean;
    createdBy?: string;
    createdDate?: string;
    modifiedBy?: string;
    modifiedDate?: string;
}

const stateService = {
    getAll: async (buid: number) => {
        const response = await axiosInstance.get<StateDTO[]>(`/api/State`, { params: { buid } });
        return response.data;
    },
    getById: async (id: number) => {
        const response = await axiosInstance.get<StateDTO>(`/api/State/${id}`);
        return response.data;
    },
    create: async (data: any) => {
        const response = await axiosInstance.post<StateDTO>(`/api/State`, data);
        return response.data;
    },
    update: async (id: number, data: any) => {
        const response = await axiosInstance.put<StateDTO>(`/api/State/${id}`, data);
        return response.data;
    },
    delete: async (id: number) => {
        const response = await axiosInstance.delete(`/api/State/${id}`);
        return response.data;
    }
};

export default stateService;
