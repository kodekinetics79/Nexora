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
  modifiedBy?: string;
  modifiedOn?: string;
  concurrencyToken: string;
}

export type ContactMutationRequest = Partial<Pick<ContactDTO,
  'customerId' | 'supplierId' | 'firstName' | 'middleName' | 'lastName' | 'email' |
  'phoneNo' | 'mobileNo' | 'position' | 'isPrimary' | 'isActive' | 'concurrencyToken'>>;

const toMutationRequest = (body: Partial<ContactDTO>, includeLifecycleState: boolean): ContactMutationRequest => ({
  customerId: body.customerId,
  supplierId: body.supplierId,
  firstName: body.firstName,
  middleName: body.middleName,
  lastName: body.lastName,
  email: body.email,
  phoneNo: body.phoneNo,
  mobileNo: body.mobileNo,
  position: body.position,
  isPrimary: body.isPrimary,
  ...(includeLifecycleState ? { isActive: body.isActive } : {}),
  concurrencyToken: body.concurrencyToken,
});

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
    const r = await axiosInstance.post('/api/Contact', toMutationRequest(body, true));
    return r.data;
  },
  update: async (id: number, body: Partial<ContactDTO>): Promise<void> => {
    await axiosInstance.put(`/api/Contact/${id}`, toMutationRequest(body, false));
  },
  delete: async (id: number, concurrencyToken: string): Promise<void> => {
    await axiosInstance.delete(`/api/Contact/${id}`, { params: { concurrencyToken } });
  },
};

export default contactService;
