import axiosInstance from '../axiosInstance';

// ─── FR-DSH-06: scheduled report delivery ───────────────────────────────────

export type ReportCadence = 'DAILY' | 'WEEKLY' | 'MONTHLY';
export type ReportFormat = 'PDF' | 'XLSX';
export type ReportRunOutcome = 'DELIVERED' | 'FAILED' | 'NOTHING_TO_REPORT';

export interface ReportCatalogDTO {
  reportKeys: string[];
  cadences: ReportCadence[];
  formats: ReportFormat[];
  maximumWindowDays: number;
  maximumDayOfMonth: number;
}

export interface ReportSubscriptionDTO {
  id: number;
  reportKey: string;
  reportTitle: string;
  cadence: ReportCadence;
  format: ReportFormat;
  hourUtc: number;
  dayOfWeek: number;
  dayOfMonth: number;
  windowDays: number;
  recipients: string[];
  isActive: boolean;
  /** Null means NOT SCHEDULED. It never means "due now" — the column has no epoch default. */
  nextRunOn: string | null;
  lastRunOn: string | null;
  lastRunOutcome: ReportRunOutcome | null;
  /** What happened in words. Surfaced on the row so a failure is visible without reading a log. */
  lastRunDetail: string | null;
}

export interface UpsertReportSubscriptionRequest {
  id?: number | null;
  reportKey: string;
  cadence: ReportCadence;
  format: ReportFormat;
  hourUtc: number;
  dayOfWeek: number;
  dayOfMonth: number;
  windowDays: number;
  recipients: string;
  isActive: boolean;
}

const reportingService = {
  getCatalog: async (): Promise<ReportCatalogDTO> => {
    const r = await axiosInstance.get<ReportCatalogDTO>('/api/reporting/catalog');
    return r.data;
  },

  listSubscriptions: async (): Promise<ReportSubscriptionDTO[]> => {
    const r = await axiosInstance.get<ReportSubscriptionDTO[]>('/api/reporting/subscriptions');
    return r.data ?? [];
  },

  upsertSubscription: async (body: UpsertReportSubscriptionRequest): Promise<ReportSubscriptionDTO> => {
    const r = await axiosInstance.put<ReportSubscriptionDTO>('/api/reporting/subscriptions', body);
    return r.data;
  },

  deleteSubscription: async (id: number): Promise<void> => {
    await axiosInstance.delete(`/api/reporting/subscriptions/${id}`);
  },

  /**
   * Downloads the same document the schedule would attach. Exists so a schedule can be checked
   * before anyone waits a week for the first delivery.
   */
  download: async (reportKey: string, format: ReportFormat, windowDays: number): Promise<void> => {
    const r = await axiosInstance.get(`/api/reporting/render/${reportKey}`, {
      params: { format, windowDays },
      responseType: 'blob',
    });

    const disposition = String(r.headers['content-disposition'] ?? '');
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    const fileName = match ? decodeURIComponent(match[1]) : `${reportKey}.${format.toLowerCase()}`;

    const url = URL.createObjectURL(r.data as Blob);
    try {
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();
    } finally {
      URL.revokeObjectURL(url);
    }
  },
};

export default reportingService;
