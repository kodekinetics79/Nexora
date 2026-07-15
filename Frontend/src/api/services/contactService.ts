import axiosInstance from '../axiosInstance';

export interface ContactDTO {
  id: number;
  supplierId?: number;
  customerId?: number;
  supplierName?: string;
  customerName?: string;
  firstName: string;
  middleName?: string;
  lastName?: string;
  email?: string;
  phoneNo?: string;
  mobileNo?: string;
  position?: string;
  isPrimary?: boolean;
  isActive?: boolean;
  createdBy?: string;
  createdOn?: string;
}

const contactService = {
  getBySupplier: async (supplierId: number): Promise<ContactDTO[]> => {
    const r = await axiosInstance.get('/api/Contact', { params: { supplierId, pageSize: 100 } });
    return r.data?.items ?? [];
  },
  getByCustomer: async (customerId: number): Promise<ContactDTO[]> => {
    const r = await axiosInstance.get('/api/Contact', { params: { customerId, pageSize: 100 } });
    return r.data?.items ?? [];
  },
  create: async (body: Partial<ContactDTO>): Promise<ContactDTO> => {
    const r = await axiosInstance.post('/api/Contact', body);
    return r.data;
  },
  update: async (id: number, body: Partial<ContactDTO>): Promise<void> => {
    await axiosInstance.put(`/api/Contact/${id}`, body);
  },
  delete: async (id: number): Promise<void> => {
    await axiosInstance.delete(`/api/Contact/${id}`);
  },
};

export default contactService;
