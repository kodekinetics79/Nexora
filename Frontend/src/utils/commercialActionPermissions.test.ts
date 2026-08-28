import { describe, expect, it } from 'vitest';
import {
  commercialActionPermissions,
  type PermissionAction,
  type PermissionCheck,
} from './commercialActionPermissions';

type Grant = `${string}:${PermissionAction}`;
const accessFor = (...grants: Grant[]) => {
  const granted = new Set(grants);
  const check: PermissionCheck = (moduleName, action = 'view') => granted.has(`${moduleName}:${action}`);
  return commercialActionPermissions(check);
};

describe('commercial action permission matrix', () => {
  it('lets a Leads viewer inspect the governed workbench without advertising mutations', () => {
    expect(accessFor('Leads:view')).toMatchObject({
      canOpenLeadWorkbench: true,
      canEditLeadDecision: false,
      canResolveLeadDuplicate: false,
      canPromoteLeadToRfq: false,
      canResolveRfqRevisionImpact: false,
      canViewLeadEvidence: true,
    });
  });

  it('lets a Leads editor resolve duplicates but not create an RFQ', () => {
    expect(accessFor('Leads:view', 'Leads:edit')).toMatchObject({
      canOpenLeadWorkbench: true,
      canEditLeadDecision: true,
      canResolveLeadDuplicate: true,
      canPromoteLeadToRfq: false,
    });
  });

  it('requires both Lead edit and RFQ create for promotion, matching server authority', () => {
    expect(accessFor('Leads:view', 'RFQ Management:create').canPromoteLeadToRfq).toBe(false);
    expect(accessFor('Leads:edit', 'RFQ Management:create').canPromoteLeadToRfq).toBe(true);
  });

  it('requires both Lead edit and RFQ edit to close an amendment review', () => {
    expect(accessFor('Leads:edit').canResolveRfqRevisionImpact).toBe(false);
    expect(accessFor('RFQ Management:edit').canResolveRfqRevisionImpact).toBe(false);
    expect(accessFor(
      'Leads:edit',
      'RFQ Management:edit',
    ).canResolveRfqRevisionImpact).toBe(true);
  });

  it('keeps Lead linking separate from cross-module Customer creation authority', () => {
    expect(accessFor('Leads:edit')).toMatchObject({
      canLinkLeadClient: true,
      canCreateClientFromLead: false,
    });
    expect(accessFor('Customers:create')).toMatchObject({
      canLinkLeadClient: false,
      canCreateClientFromLead: false,
    });
    expect(accessFor('Leads:edit', 'Customers:create').canCreateClientFromLead).toBe(true);
  });

  it('keeps destination visibility and destructive authority independent', () => {
    expect(accessFor('RFQ Management:view')).toMatchObject({
      canViewPromotedRfq: true,
      canDeleteDraftRfq: false,
    });
    expect(accessFor('RFQ Management:delete')).toMatchObject({
      canViewPromotedRfq: false,
      canDeleteDraftRfq: true,
    });
  });

  it('requires both RFQ edit and Supplier History view to create or open a Sourcing Case', () => {
    expect(accessFor('RFQ Management:edit').canCreateOrOpenSourcingCase).toBe(false);
    expect(accessFor('Supplier History:view').canCreateOrOpenSourcingCase).toBe(false);
    expect(accessFor(
      'RFQ Management:edit',
      'Supplier History:view',
    ).canCreateOrOpenSourcingCase).toBe(true);
  });
});
