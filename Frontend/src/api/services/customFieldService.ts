import axiosInstance from '../axiosInstance';

/**
 * AA-01 · tenant-defined custom fields.
 *
 * Two halves:
 *   • definitions — what a tenant's manager declares (this is the admin screen),
 *   • records     — the values on one customer/supplier/lead line (this is the value editor).
 *
 * Values live in ONE jsonb bag on the owning row, keyed by the definition's stable key. That
 * is why the key is immutable: renaming it would orphan every value already captured.
 */

export type CustomFieldDataType =
  | 'Text' | 'Integer' | 'Decimal' | 'Boolean' | 'Date'
  | 'Timestamp' | 'Option' | 'MultiOption' | 'Json' | 'Reference';

export type CustomFieldStatus = 'Draft' | 'Active' | 'Retired';

/** Entity types that carry a value bag today. Anything else has no attach point yet. */
export const CUSTOM_FIELD_ENTITY_TYPES = [
  { value: 'Customer', label: 'Customers' },
  { value: 'Supplier', label: 'Suppliers' },
  { value: 'LeadItem', label: 'Lead / RFQ lines' },
] as const;

/** Types the editor can render an input for. The rest exist in the model but have no UI. */
export const CUSTOM_FIELD_TYPE_CHOICES: ReadonlyArray<{ value: CustomFieldDataType; label: string; hint: string }> = [
  { value: 'Text', label: 'Text', hint: 'Any wording — codes, references, short notes.' },
  { value: 'Integer', label: 'Whole number', hint: 'Counts and days. No decimal point.' },
  { value: 'Decimal', label: 'Number', hint: 'Amounts and percentages.' },
  { value: 'Boolean', label: 'Yes / No', hint: 'A single tick box.' },
  { value: 'Date', label: 'Date', hint: 'A calendar date, no time.' },
  { value: 'Option', label: 'Choice', hint: 'Pick one from a list you define.' },
  { value: 'MultiOption', label: 'Multiple choice', hint: 'Pick any number from a list you define.' },
];

export interface CustomFieldOption {
  stableKey: string;
  label: string;
  displayOrder: number;
}

export interface CustomFieldVersion {
  versionNumber: number;
  label: string;
  helpText?: string | null;
  dataType: CustomFieldDataType;
  isRequired: boolean;
  options: CustomFieldOption[];
  createdOn: string;
  createdBy: string;
}

export interface CustomFieldDefinition {
  id: number;
  entityType: string;
  /** Immutable once created — the jsonb bag is keyed by it. */
  stableKey: string;
  status: CustomFieldStatus;
  activeVersionNumber?: number | null;
  versions: CustomFieldVersion[];
  createdOn: string;
  createdBy: string;
  retiredOn?: string | null;
  retiredBy?: string | null;
  retirementReason?: string | null;
  version: number;
  /** Position in the field list and the column picker. Not versioned — reordering is cheap. */
  displayOrder: number;
}

export interface CustomFieldVersionDraft {
  label: string;
  dataType: CustomFieldDataType;
  isRequired: boolean;
  helpText?: string | null;
}

export interface CustomFieldBagItem {
  stableKey: string;
  label: string;
  dataType: CustomFieldDataType;
  isRequired: boolean;
  displayOrder: number;
  options: CustomFieldOption[];
  value?: unknown;
  displayValue?: string | null;
  requiresManagerAccess: boolean;
}

export interface CustomFieldBagResponse {
  entityType: string;
  entityId: number;
  fields: CustomFieldBagItem[];
}

/** The active version of a definition, or its newest version when none is active yet. */
export const activeVersion = (definition: CustomFieldDefinition): CustomFieldVersion | undefined =>
  definition.versions.find((v) => v.versionNumber === definition.activeVersionNumber)
  ?? [...definition.versions].sort((a, b) => b.versionNumber - a.versionNumber)[0];

const customFieldService = {
  listDefinitions: async (entityType: string): Promise<CustomFieldDefinition[]> => {
    const r = await axiosInstance.get('/api/custom-fields/definitions', { params: { entityType } });
    return r.data;
  },

  createDefinition: async (payload: {
    entityType: string;
    stableKey: string;
    version: CustomFieldVersionDraft;
    options?: CustomFieldOption[];
    activate: boolean;
    displayOrder: number;
  }): Promise<CustomFieldDefinition> => {
    const r = await axiosInstance.post('/api/custom-fields/definitions', payload);
    return r.data;
  },

  /**
   * Edits are append-only: a change publishes a NEW version and activates it, so the label a
   * value was captured under stays recoverable. The stable key is not part of the payload —
   * there is no route that can change it.
   */
  addVersion: async (definitionId: number, payload: {
    version: CustomFieldVersionDraft;
    options?: CustomFieldOption[];
    activate: boolean;
  }): Promise<CustomFieldDefinition> => {
    const r = await axiosInstance.post(`/api/custom-fields/definitions/${definitionId}/versions`, payload);
    return r.data;
  },

  /** Batch reposition. One call for a whole reorder, and it does not create versions. */
  reorder: async (
    entityType: string,
    order: Array<{ definitionId: number; displayOrder: number }>,
  ): Promise<CustomFieldDefinition[]> => {
    const r = await axiosInstance.put('/api/custom-fields/definitions/order', { entityType, order });
    return r.data;
  },

  retire: async (definitionId: number, reason: string): Promise<CustomFieldDefinition> => {
    const r = await axiosInstance.post(`/api/custom-fields/definitions/${definitionId}/retire`, { reason });
    return r.data;
  },

  reactivate: async (definitionId: number): Promise<CustomFieldDefinition> => {
    const r = await axiosInstance.post(`/api/custom-fields/definitions/${definitionId}/reactivate`);
    return r.data;
  },

  getRecordFields: async (entityType: string, entityId: number): Promise<CustomFieldBagResponse> => {
    const r = await axiosInstance.get(`/api/custom-fields/records/${entityType}/${entityId}`);
    return r.data;
  },

  updateRecordFields: async (
    entityType: string,
    entityId: number,
    values: Record<string, unknown>,
  ): Promise<CustomFieldBagResponse> => {
    // enforceRequired is true: required fields are enforced by the server, at the boundary
    // that persists them. The client's own checks are a courtesy, never the gate.
    const r = await axiosInstance.put(`/api/custom-fields/records/${entityType}/${entityId}`, {
      values,
      enforceRequired: true,
    });
    return r.data;
  },
};

export default customFieldService;
