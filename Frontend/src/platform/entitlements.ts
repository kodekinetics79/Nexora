/**
 * Frontend mirror of Backend/Platform/Entitlements/TypedEntitlementCatalog.cs.
 * Keep this closed: the server rejects unknown keys and non-boolean values.
 */
/**
 * `available: false` mirrors the server's `TypedEntitlementCatalog.RuntimeAvailableKeys`: those
 * five keys have no production execution boundary, so runtime authorization denies them however
 * the packaging flag reads. They stay in the catalogue — a plan may legitimately carry a false
 * flag for a future capability — but they must never be presented as something a sale can
 * promise. An unmarked switch over an unimplemented capability is how "the toggle exists, the
 * tracking does not" gets onto a signed order form.
 */
export const ENTITLEMENT_CATALOG = [
  { key: 'module.rfq', label: 'RFQs', group: 'Modules', available: true },
  { key: 'module.quotes', label: 'Quotes', group: 'Modules', available: true },
  { key: 'module.orders', label: 'Orders', group: 'Modules', available: true },
  { key: 'module.procurement', label: 'Procurement', group: 'Modules', available: true },
  { key: 'module.inventory', label: 'Inventory', group: 'Modules', available: true },
  { key: 'capability.ai', label: 'AI assistance', group: 'Capabilities', available: true },
  { key: 'capability.ocr', label: 'OCR', group: 'Capabilities', available: true },
  { key: 'capability.api', label: 'API access', group: 'Capabilities', available: false },
  { key: 'capability.email-intake', label: 'Email intake', group: 'Capabilities', available: true },
  { key: 'capability.supplier-search', label: 'Supplier search', group: 'Capabilities', available: true },
  { key: 'capability.integrations', label: 'Integrations', group: 'Capabilities', available: true },
  { key: 'capability.exports', label: 'Exports', group: 'Capabilities', available: true },
  { key: 'capability.automation', label: 'Automation', group: 'Capabilities', available: false },
  { key: 'capability.sso', label: 'SSO', group: 'Capabilities', available: false },
  { key: 'capability.scim', label: 'SCIM', group: 'Capabilities', available: false },
  { key: 'capability.dedicated-resources', label: 'Dedicated resources', group: 'Capabilities', available: false },
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
