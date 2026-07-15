import axiosInstance from '../axiosInstance';

export interface WarehouseDTO {
    id: number;
    warehouseCode: string;
    warehouseName: string;
    location: string | null;
    addressLine1: string | null;
    addressLine2: string | null;
    city: string | null;
    state: string | null;
    country: string | null;
    postalCode: string | null;
    capacity: number | null;
    managerName: string | null;
    contactPhone: string | null;
    contactEmail: string | null;
    businessUnitId: number;
    isActive: boolean;
}

export interface PaginatedWarehouseResponse {
    items: WarehouseDTO[];
    totalItems: number;
    pageNumber: number;
    pageSize: number;
    totalPages: number;
}

const warehouseService = {
    getAll: async (params: any) => {
        const response = await axiosInstance.get<PaginatedWarehouseResponse>(`/api/Warehouse`, { params });
        return response.data;
    },
    getById: async (id: number, businessUnitId: number) => {
        const response = await axiosInstance.get<WarehouseDTO>(`/api/Warehouse/${id}`, { params: { businessUnitId } });
        return response.data;
    },
    create: async (data: any) => {
        const response = await axiosInstance.post<WarehouseDTO>(`/api/Warehouse`, data);
        return response.data;
    },
    update: async (id: number, data: any) => {
        const response = await axiosInstance.put(`/api/Warehouse/${id}`, data);
        return response.data;
    },
    delete: async (id: number, businessUnitId: number) => {
        const response = await axiosInstance.delete(`/api/Warehouse/${id}`, { params: { businessUnitId } });
        return response.data;
    }
};

export default warehouseService;
