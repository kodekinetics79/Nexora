import axiosInstance from '../axiosInstance';

export interface CurrencyDTO {
    id: number;
    code: string;
    currencyName: string;
    symbol: string | null;
    exchangeRate: number;
    isBaseCurrency: boolean;
    businessUnitId: number;
    isActive: boolean;
}

export interface PaginatedCurrencyResponse {
    items: CurrencyDTO[];
    totalItems: number;
    pageNumber: number;
    pageSize: number;
    totalPages: number;
}

const currencyService = {
    getAll: async (params: any) => {
        const response = await axiosInstance.get<PaginatedCurrencyResponse>(`/api/Currency`, { params });
        return response.data;
    },
    getById: async (id: number, businessUnitId: number) => {
        const response = await axiosInstance.get<CurrencyDTO>(`/api/Currency/${id}`, { params: { businessUnitId } });
        return response.data;
    },
    create: async (data: any) => {
        const response = await axiosInstance.post<CurrencyDTO>(`/api/Currency`, data);
        return response.data;
    },
    update: async (id: number, data: any) => {
        const response = await axiosInstance.put(`/api/Currency/${id}`, data);
        return response.data;
    },
    delete: async (id: number, businessUnitId: number) => {
        const response = await axiosInstance.delete(`/api/Currency/${id}`, { params: { businessUnitId } });
        return response.data;
    }
};

export default currencyService;
