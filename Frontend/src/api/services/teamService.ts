import axiosInstance from '../axiosInstance';

/**
 * Sales teams. Read here so FR-CST-02's account team can be SET by a user rather than only by
 * a developer with database access — wiring-contract failure #5 is a setting with no way to set it.
 */
export interface TeamDTO {
  id: number;
  teamName: string;
  subTeamId?: number | null;
  subTeamName?: string | null;
  managerId?: number | null;
  businessUnitId: number;
}

export interface PaginatedTeamResponse {
  items: TeamDTO[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

const teamService = {
  /** The tenant's teams. The business unit comes from the token; the query value is ignored. */
  getAll: async (pageSize = 200): Promise<TeamDTO[]> => {
    const response = await axiosInstance.get<PaginatedTeamResponse>('/api/Team', {
      params: { pageNumber: 1, pageSize },
    });
    return response.data.items ?? [];
  },
};

export default teamService;
