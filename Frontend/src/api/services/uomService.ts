import axiosInstance from '../axiosInstance';

export interface UomDTO {
    uomId: number;
    businessUnitId: number;
    uomCode: string;
    uomName: string;
    description: string | null;
    isActive: boolean;
    createdBy?: string;
    createdDate?: string;
    modifiedBy?: string;
    modifiedDate?: string;
}

const uomService = {
    getAll: async (businessUnitId: number) => {
        const response = await axiosInstance.get<UomDTO[]>(`/api/Uom`, { params: { businessUnitId } });
        return response.data;
    },
    getById: async (id: number) => {
        const response = await axiosInstance.get<UomDTO>(`/api/Uom/${id}`);
        return response.data;
    },
    create: async (data: any) => {
        const response = await axiosInstance.post<UomDTO>(`/api/Uom`, data);
        return response.data;
    },
    update: async (id: number, data: any) => {
        const response = await axiosInstance.put<UomDTO>(`/api/Uom/${id}`, data);
        return response.data;
    },
    delete: async (id: number) => {
        const response = await axiosInstance.delete(`/api/Uom/${id}`);
        return response.data;
    }
};

export default uomService;
