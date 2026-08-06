import path from 'node:path';
import contract from './fixture-contract.json' with { type: 'json' };

export type RoleName = 'manager' | 'editor' | 'denied';

export interface LoginCredentials {
  email: string;
  password: string;
  businessUnitId?: string;
}

const prefixByRole: Record<RoleName, string> = {
  manager: 'E2E_MANAGER',
  editor: 'E2E_EDITOR',
  denied: 'E2E_DENIED',
};

const fixtureMode = process.env.E2E_FIXTURE_MODE !== 'false';
const defaultsByRole: Record<RoleName, LoginCredentials> = {
  manager: { ...contract.manager, businessUnitId: contract.businessUnitId },
  editor: { ...contract.editor, businessUnitId: contract.businessUnitId },
  denied: { ...contract.denied, businessUnitId: contract.businessUnitId },
};

export const credentialsFor = (role: RoleName): LoginCredentials | null => {
  const prefix = prefixByRole[role];
  const email = process.env[`${prefix}_EMAIL`];
  const password = process.env[`${prefix}_PASSWORD`];
  if ((!email || !password) && fixtureMode) return defaultsByRole[role];
  if (!email || !password) return null;
  return { email, password, businessUnitId: process.env[`${prefix}_BUSINESS_UNIT_ID`] };
};

export const authStatePath = (role: RoleName): string =>
  path.resolve('node_modules/.cache/nexora-e2e', `${role}.json`);

export const fixture = {
  batchId: process.env.E2E_BATCH_ID ?? (fixtureMode ? contract.batchId : undefined),
  leadId: process.env.E2E_LEAD_ID ?? (fixtureMode ? contract.leadId : undefined),
  revisionLeadId: process.env.E2E_REVISION_LEAD_ID ?? process.env.E2E_LEAD_ID ?? (fixtureMode ? contract.revisionLeadId : undefined),
  rfqId: process.env.E2E_RFQ_ID ?? (fixtureMode ? contract.rfqId : undefined),
  quoteId: process.env.E2E_QUOTE_ID ?? (fixtureMode ? contract.quoteId : undefined),
  nexoraSerial: process.env.E2E_NEXORA_SERIAL ?? (fixtureMode ? contract.nexoraSerial : undefined),
  customerName: process.env.E2E_CUSTOMER_NAME ?? (fixtureMode ? contract.customerName : undefined),
  uploadFile: process.env.E2E_UPLOAD_FILE ?? (fixtureMode ? path.resolve('e2e/fixtures/release-01c-inquiry.csv') : undefined),
  dashboardKpiLabel: process.env.E2E_DASHBOARD_KPI_LABEL ?? contract.dashboardKpiLabel,
  dashboardKpiValue: process.env.E2E_DASHBOARD_KPI_VALUE ?? (fixtureMode ? contract.dashboardKpiValue : undefined),
  dashboardDrillDownCount: process.env.E2E_DASHBOARD_DRILLDOWN_COUNT ?? (fixtureMode ? contract.dashboardDrillDownCount : undefined),
  batchCounts: {
    'New leads': process.env.E2E_EXPECTED_NEW_LEADS ?? (fixtureMode ? contract.expectedNewLeads : undefined),
    'Exact duplicates': process.env.E2E_EXPECTED_EXACT_DUPLICATES ?? (fixtureMode ? contract.expectedExactDuplicates : undefined),
    Revisions: process.env.E2E_EXPECTED_REVISIONS ?? (fixtureMode ? contract.expectedRevisions : undefined),
    'Possible matches': process.env.E2E_EXPECTED_POSSIBLE_MATCHES ?? (fixtureMode ? contract.expectedPossibleMatches : undefined),
  } as Record<string, string | undefined>,
};

export const missingFixtureValues = (...entries: Array<[string, string | undefined]>): string[] =>
  entries.filter(([, value]) => !value).map(([name]) => name);

/**
 * Asserts that every named environment variable is present, and returns them typed.
 *
 * <p><b>Why this exists.</b> Four specs performed the same check at MODULE scope
 * (`if (!password) throw …` beside the imports). Playwright evaluates every spec file to collect
 * its tests, so one missing variable threw during collection and aborted discovery for the
 * ENTIRE suite — `npx playwright test --list` reported `0 tests in 0 files`. The whole acceptance
 * suite became invisible, and the discovered-test-count gate in `zero-skips-reporter.ts` could
 * not fire either, because there was nothing to count.</p>
 *
 * <p>Discovery must never depend on runtime secrets. Called from inside a test body, this keeps
 * the failure exactly as loud — the test fails with the same message naming the same variables —
 * while leaving every other spec discoverable and runnable. It deliberately does NOT skip: a
 * silently skipped acceptance test is the failure mode the zero-skips reporter exists to catch.</p>
 */
export const requireEnv = <K extends string>(context: string, ...names: K[]): Record<K, string> => {
  const missing = names.filter((name) => !process.env[name]?.trim());
  if (missing.length > 0)
    throw new Error(`${context} requires these environment values: ${missing.join(', ')}.`);
  return Object.fromEntries(names.map((name) => [name, process.env[name]!.trim()])) as Record<K, string>;
};
