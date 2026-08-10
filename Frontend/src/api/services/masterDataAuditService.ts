import axiosInstance from '../axiosInstance';

/**
 * FR-MDM-05 · the before/after trail for one master-data record.
 *
 * An audit row nobody can query is not a control, so the trail is read back through the SAME
 * module permission as the record it describes — `/api/Product/{id}/change-history` sits behind
 * Products·View, `/api/Customer/{id}/change-history` behind Customers·View. There is no separate
 * audit right to grant and therefore no way for the trail to end up readable by nobody.
 */
export type MasterDataEntityType = 'Customer' | 'Supplier' | 'Product';

/** COMMERCIAL — cost, price or payment terms. PERSONAL — contact or address data under PDPL. */
export type MasterDataSensitivity = 'COMMERCIAL' | 'PERSONAL';

export interface MasterDataFieldChangeDTO {
  field: string;
  /** Null when the field genuinely had no value. Nothing is withheld from a caller who can read the record. */
  before: string | null;
  after: string | null;
  sensitivity: MasterDataSensitivity | null;
}

export interface MasterDataChangeEventDTO {
  id: number;
  occurredOn: string;
  changeType: 'CREATED' | 'UPDATED' | 'DELETED';
  actor: string;
  actorUserId: number | null;
  /** API — a screen edit. IMPORT — a spreadsheet upload. SYSTEM — a worker or a save with no actor. */
  changeSource: 'API' | 'IMPORT' | 'SYSTEM';
  correlationId: string | null;
  reason: string | null;
  entityLabel: string | null;
  fieldCount: number;
  changes: MasterDataFieldChangeDTO[];
}

const ROUTES: Record<MasterDataEntityType, string> = {
  Customer: '/api/Customer',
  Supplier: '/api/Supplier',
  Product: '/api/Product',
};

const getChangeHistory = async (
  entityType: MasterDataEntityType,
  id: number,
  limit = 50,
): Promise<MasterDataChangeEventDTO[]> => {
  const response = await axiosInstance.get<MasterDataChangeEventDTO[]>(
    `${ROUTES[entityType]}/${id}/change-history`,
    { params: { limit } },
  );
  return response.data ?? [];
};

export default { getChangeHistory };
