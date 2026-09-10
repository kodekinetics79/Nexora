import { describe, expect, it } from 'vitest';
import decideSource from './DecidePage.tsx?raw';
import rfqSource from '../../Procurement/RFQs/ViewRFQPage.tsx?raw';
import leadsSource from '../LeadsPage.tsx?raw';
import batchSource from '../LeadIngestionBatchPage.tsx?raw';
import leadDetailSource from '../LeadDetailPage.tsx?raw';

/**
 * Source-string contracts between the lead decision screen and the screens that link into it.
 * They pin authority checks that a refactor could drop without any test noticing, because the
 * button would simply disappear for the role that lacks the permission.
 */
describe('commercial cross-module link contracts', () => {
  it('keeps read-only decision entry under Leads view authority', () => {
    expect(leadsSource).toContain('commercialAccess.canOpenLeadWorkbench');
    expect(batchSource).toContain('canOpenWorkbench');
    expect(batchSource).not.toContain("hasPermission('RFQ Management', 'create')");
  });

  it('does not advertise a promoted RFQ destination without RFQ view authority', () => {
    expect(decideSource).toContain('commercialAccess.canViewPromotedRfq');
    expect(decideSource).toContain('if (commercialAccess.canViewPromotedRfq)');
  });

  it('clears and suppresses obsolete browser drafts once promotion makes the revision terminal', () => {
    expect(decideSource).toContain('guard.markSaved({ decisions, concern });');
    expect(decideSource).toContain('guard.recoveredDraft && !locked');
  });

  it('uses the shared Owner-or-manager authority rule for participation and promotion', () => {
    expect(decideSource).toContain('hasCommercialDecisionAuthority(userData)');
    expect(decideSource).not.toContain('const isManager = userData.isManager === true;');
  });

  it('guards duplicate-resolution controls and the mutation with Leads edit authority', () => {
    expect(leadDetailSource.match(/commercialAccess\.canResolveLeadDuplicate/g)?.length).toBeGreaterThanOrEqual(3);
    expect(leadDetailSource).toContain('Lead edit permission is required to resolve a duplicate.');
  });

  it('takes exact RFQ-line evidence to the lead decision history', () => {
    // General Lead detail, decision record, and exact evidence are three distinct destinations;
    // each must carry the destination module's view authority.
    expect(rfqSource.match(/commercialAccess\.canViewLeadEvidence/g)?.length).toBeGreaterThanOrEqual(3);
    expect(rfqSource).toContain('/workbench?stage=evidence');
    expect(rfqSource).toContain('Open exact source evidence');
    // The decision screen honours that address by opening its history drawer.
    expect(decideSource).toContain("['evidence', 'validate'].includes(searchParams.get('stage')");
  });
});
