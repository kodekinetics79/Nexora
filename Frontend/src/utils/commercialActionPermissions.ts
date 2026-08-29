export type PermissionAction = 'view' | 'create' | 'edit' | 'delete';
export type PermissionCheck = (moduleName: string, action?: PermissionAction) => boolean;

export type CommercialAuthorityIdentity = {
  isManager?: boolean;
  isSuperAdmin?: boolean;
};

/**
 * The commercial decision boundary accepts manager-ranked tenant users and the tenant Owner.
 * `/me/permissions` reports Owner separately as `isSuperAdmin`, so checking only `isManager`
 * incorrectly turns the most privileged tenant user into a read-only reviewer.
 */
export const hasCommercialDecisionAuthority = (identity: CommercialAuthorityIdentity) =>
  identity.isManager === true || identity.isSuperAdmin === true;

/**
 * Client-side discoverability for the Lead → participation → RFQ boundary.
 *
 * This never replaces API authorization. It mirrors the server attributes so the interface does
 * not advertise a mutation the server will refuse, or hide a read-only record a viewer may open.
 */
export const commercialActionPermissions = (hasPermission: PermissionCheck) => ({
  canOpenLeadWorkbench: hasPermission('Leads', 'view'),
  canEditLeadDecision: hasPermission('Leads', 'edit'),
  canResolveLeadDuplicate: hasPermission('Leads', 'edit'),
  // LeadParticipationController requires BOTH permissions for promotion.
  canPromoteLeadToRfq:
    hasPermission('Leads', 'edit') && hasPermission('RFQ Management', 'create'),
  canResolveRfqRevisionImpact:
    hasPermission('Leads', 'edit') && hasPermission('RFQ Management', 'edit'),
  canDeleteDraftRfq: hasPermission('RFQ Management', 'delete'),
  // The write is RFQ-scoped and the picker must be able to read this tenant's catalogue.
  canResolveRfqProduct:
    hasPermission('RFQ Management', 'edit') && hasPermission('Products', 'view'),
  canViewPromotedRfq: hasPermission('RFQ Management', 'view'),
  canViewLeadEvidence: hasPermission('Leads', 'view'),
  canLinkLeadClient: hasPermission('Leads', 'edit'),
  // The inline path writes a Customer first, then lets the user link it separately.
  canCreateClientFromLead:
    hasPermission('Leads', 'edit') && hasPermission('Customers', 'create'),
  // ProcurementController requires BOTH permissions to create or open a Sourcing Case.
  canCreateOrOpenSourcingCase:
    hasPermission('RFQ Management', 'edit') && hasPermission('Supplier History', 'view'),
});
