import { describe, expect, it } from 'vitest';
import workbenchSource from './LeadDecisionWorkbenchPage.tsx?raw';
import rfqSource from '../../Procurement/RFQs/ViewRFQPage.tsx?raw';
import leadsSource from '../LeadsPage.tsx?raw';
import batchSource from '../LeadIngestionBatchPage.tsx?raw';
import leadDetailSource from '../LeadDetailPage.tsx?raw';

describe('commercial cross-module link contracts', () => {
  it('keeps read-only workbench entry under Leads view authority', () => {
    expect(leadsSource).toContain('commercialAccess.canOpenLeadWorkbench');
    expect(batchSource).toContain('canOpenWorkbench');
    expect(batchSource).not.toContain("hasPermission('RFQ Management', 'create')");
  });

  it('does not advertise a promoted RFQ destination without RFQ view authority', () => {
    expect(workbenchSource).toContain('commercialAccess.canViewPromotedRfq');
    expect(workbenchSource).toContain('if (commercialAccess.canViewPromotedRfq)');
  });

  it('uses the shared Owner-or-manager authority rule for participation and promotion', () => {
    expect(workbenchSource).toContain('hasCommercialDecisionAuthority(userData)');
    expect(workbenchSource).not.toContain('const isManager = userData.isManager === true;');
  });

  it('guards duplicate-resolution controls and the mutation with Leads edit authority', () => {
    expect(leadDetailSource.match(/commercialAccess\.canResolveLeadDuplicate/g)?.length).toBeGreaterThanOrEqual(3);
    expect(leadDetailSource).toContain('Lead edit permission is required to resolve a duplicate.');
  });

  it('takes exact RFQ-line evidence to the governed Lead Evidence stage', () => {
    // General Lead detail, decision record, and exact evidence are three distinct destinations;
    // each must carry the destination module's view authority.
    expect(rfqSource.match(/commercialAccess\.canViewLeadEvidence/g)?.length).toBeGreaterThanOrEqual(3);
    expect(rfqSource).toContain('/workbench?stage=evidence');
    expect(rfqSource).toContain('Open exact source evidence');
  });
});
