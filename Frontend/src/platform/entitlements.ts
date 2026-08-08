/**
 * Frontend mirror of Backend/Platform/Entitlements/TypedEntitlementCatalog.cs.
 * Keep this closed: the server rejects unknown keys and non-boolean values.
 */
export const ENTITLEMENT_CATALOG = [
  { key: 'module.rfq', label: 'RFQs', group: 'Modules' },
  { key: 'module.quotes', label: 'Quotes', group: 'Modules' },
  { key: 'module.orders', label: 'Orders', group: 'Modules' },
  { key: 'module.procurement', label: 'Procurement', group: 'Modules' },
  { key: 'module.inventory', label: 'Inventory', group: 'Modules' },
  { key: 'capability.ai', label: 'AI assistance', group: 'Capabilities' },
  { key: 'capability.ocr', label: 'OCR', group: 'Capabilities' },
  { key: 'capability.api', label: 'API access', group: 'Capabilities' },
  { key: 'capability.email-intake', label: 'Email intake', group: 'Capabilities' },
  { key: 'capability.supplier-search', label: 'Supplier search', group: 'Capabilities' },
  { key: 'capability.integrations', label: 'Integrations', group: 'Capabilities' },
  { key: 'capability.exports', label: 'Exports', group: 'Capabilities' },
  { key: 'capability.automation', label: 'Automation', group: 'Capabilities' },
  { key: 'capability.sso', label: 'SSO', group: 'Capabilities' },
  { key: 'capability.scim', label: 'SCIM', group: 'Capabilities' },
  { key: 'capability.dedicated-resources', label: 'Dedicated resources', group: 'Capabilities' },
] as const;

export type EntitlementKey = (typeof ENTITLEMENT_CATALOG)[number]['key'];

const ENTITLEMENT_KEYS = new Set<string>(ENTITLEMENT_CATALOG.map((entry) => entry.key));

export const isEntitlementKey = (value: string): value is EntitlementKey => ENTITLEMENT_KEYS.has(value);

export const splitPlanEntitlements = (values: readonly string[]) => ({
  selected: values.filter(isEntitlementKey),
  unknown: values.filter((value) => !isEntitlementKey(value)),
});

/** Existing API wire format: a JSON object whose selected closed-catalogue keys are true. */
export const serializeEntitlements = (values: readonly EntitlementKey[]): string => JSON.stringify(
  Object.fromEntries(ENTITLEMENT_CATALOG.map(({ key }) => [key, values.includes(key)])),
);
