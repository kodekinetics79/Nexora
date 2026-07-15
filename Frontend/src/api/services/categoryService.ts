import axiosInstance from '../axiosInstance';

// ─── Types ────────────────────────────────────────────────────────────────
export interface ProductCategoryDTO {
  id: number;
  categoryName: string;
  description?: string;
  parentCategoryId?: number;
  parentCategoryName?: string;
  businessUnitId?: number;
  isActive: boolean;
  createdBy?: string;
  createdOn?: string;
}

export interface ProductSubCategoryDTO {
  id: number;
  subCategoryName: string;
  description?: string;
  businessUnitId?: number;
  isActive: boolean;
  createdBy?: string;
  createdOn?: string;
}

export interface PaginatedCategoryResponse {
  items: ProductCategoryDTO[];
  totalItems: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
}

export interface PaginatedSubCategoryResponse {
  items: ProductSubCategoryDTO[];
  totalItems: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
}

// ─── Category Service ─────────────────────────────────────────────────────
const categoryService = {
  getAll: async (params?: { pageNumber?: number; pageSize?: number; search?: string; isActive?: boolean }): Promise<PaginatedCategoryResponse> => {
    const r = await axiosInstance.get('/api/ProductCategory', { params });
    return r.data;
  },
  create: async (body: Partial<ProductCategoryDTO>): Promise<ProductCategoryDTO> => {
    const r = await axiosInstance.post('/api/ProductCategory', body);
    return r.data;
  },
  update: async (id: number, body: Partial<ProductCategoryDTO>): Promise<void> => {
    await axiosInstance.put(`/api/ProductCategory/${id}`, body);
  },
  delete: async (id: number): Promise<void> => {
    await axiosInstance.delete(`/api/ProductCategory/${id}`);
  },
  // Upload/Export
  downloadTemplate: () => axiosInstance.get('/api/ProductCategoryUploader/category/download-template', { responseType: 'blob' }),
  uploadTemplate: (file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return axiosInstance.post('/api/ProductCategoryUploader/category/upload-template', fd, { headers: { 'Content-Type': 'multipart/form-data' } });
  },
  export: () => axiosInstance.get('/api/ProductCategoryUploader/category/export', { responseType: 'blob' }),
};

// ─── SubCategory Service ──────────────────────────────────────────────────
const subCategoryService = {
  getAll: async (params?: { pageNumber?: number; pageSize?: number; search?: string; isActive?: boolean }): Promise<PaginatedSubCategoryResponse> => {
    const r = await axiosInstance.get('/api/ProductSubCategory', { params });
    return r.data;
  },
  create: async (body: Partial<ProductSubCategoryDTO>): Promise<ProductSubCategoryDTO> => {
    const r = await axiosInstance.post('/api/ProductSubCategory', body);
    return r.data;
  },
  update: async (id: number, body: Partial<ProductSubCategoryDTO>): Promise<void> => {
    await axiosInstance.put(`/api/ProductSubCategory/${id}`, body);
  },
  delete: async (id: number): Promise<void> => {
    await axiosInstance.delete(`/api/ProductSubCategory/${id}`);
  },
  // Upload/Export
  downloadTemplate: () => axiosInstance.get('/api/ProductCategoryUploader/sub-category/download-template', { responseType: 'blob' }),
  uploadTemplate: (file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return axiosInstance.post('/api/ProductCategoryUploader/sub-category/upload-template', fd, { headers: { 'Content-Type': 'multipart/form-data' } });
  },
  export: () => axiosInstance.get('/api/ProductCategoryUploader/sub-category/export', { responseType: 'blob' }),
};

export { categoryService, subCategoryService };
