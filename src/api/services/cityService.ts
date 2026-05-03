import axiosInstance from '../axiosInstance';

export interface CityDTO {
    cityId: number;
    cityName: string;
    stateId: number;
    stateName?: string;
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

const cityService = {
    getAll: async (buid: number) => {
        const response = await axiosInstance.get<CityDTO[]>(`/api/City`, { params: { buid } });
        return response.data;
    },
    getById: async (id: number) => {
        const response = await axiosInstance.get<CityDTO>(`/api/City/${id}`);
        return response.data;
    },
    create: async (data: any) => {
        const response = await axiosInstance.post<CityDTO>(`/api/City`, data);
        return response.data;
    },
    update: async (id: number, data: any) => {
        const response = await axiosInstance.put<CityDTO>(`/api/City/${id}`, data);
        return response.data;
    },
    delete: async (id: number) => {
        const response = await axiosInstance.delete(`/api/City/${id}`);
        return response.data;
    }
};

export default cityService;
