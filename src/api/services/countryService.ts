import axiosInstance from '../axiosInstance';

export interface CountryDTO {
    countryId: number;
    countryCode: string;
    countryName: string;
    description: string | null;
    buid: number;
    isActive: boolean;
    createdBy?: string;
    createdDate?: string;
    modifiedBy?: string;
    modifiedDate?: string;
}

const countryService = {
    getAll: async (buid: number) => {
        const response = await axiosInstance.get<CountryDTO[]>(`/api/Country`, { params: { buid } });
        return response.data;
    },
    getById: async (id: number) => {
        const response = await axiosInstance.get<CountryDTO>(`/api/Country/${id}`);
        return response.data;
    },
    create: async (data: any) => {
        const response = await axiosInstance.post<CountryDTO>(`/api/Country`, data);
        return response.data;
    },
    update: async (id: number, data: any) => {
        const response = await axiosInstance.put<CountryDTO>(`/api/Country/${id}`, data);
        return response.data;
    },
    delete: async (id: number) => {
        const response = await axiosInstance.delete(`/api/Country/${id}`);
        return response.data;
    }
};

export default countryService;
