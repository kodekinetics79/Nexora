import axiosInstance from '../axiosInstance';

export interface QuoteConfigurationDTO {
    id: number;
    businessUnitId: number;
    logo: string | null;
    primaryColor: string | null;
    termsAndConditions: string | null;
    companyAddress: string | null;
    companyPhone: string | null;
    companyEmail: string | null;
    footerText: string | null;
    modifiedBy: string | null;
    modifiedOn: string | null;
}

const quoteConfigurationService = {
    getByBusinessUnitId: async (businessUnitId: number) => {
        const response = await axiosInstance.get<QuoteConfigurationDTO>(`/api/QuoteConfiguration/${businessUnitId}`);
        return response.data;
    },
    createOrUpdate: async (data: any) => {
        const response = await axiosInstance.post<QuoteConfigurationDTO>(`/api/QuoteConfiguration`, data);
        return response.data;
    }
};

export default quoteConfigurationService;
