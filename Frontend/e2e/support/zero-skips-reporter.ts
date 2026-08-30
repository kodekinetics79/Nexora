import type { FullResult, Reporter, Suite, TestCase, TestResult } from '@playwright/test/reporter';

// SCOPE: this count applies ONLY to the suite that sets E2E_FULL_ACCEPTANCE=true — the
// `e2e:acceptance` script, which runs `playwright.commercial-journey-v2.config.ts` with
// `testMatch: /commercial-journey-v2\.spec\.ts/`. That single file contains exactly 41 tests.
//
// The reporter is shared by ten configs, so this constant is easy to misread as a global. It was
// briefly raised to 40 when a test was added to core-commercial-journey.spec.ts — a DIFFERENT
// file under a DIFFERENT config — which would have failed the acceptance run by expecting a test
// that suite never contained. Only change this when the count of tests in
// commercial-journey-v2.spec.ts itself changes.
const EXPECTED_TESTS = 41;

export default class ZeroSkipsReporter implements Reporter {
  private discovered = 0;
  private skipped: string[] = [];
  private expectedTests: number | undefined;

  constructor(options?: { expectedTests?: number }) {
    this.expectedTests = options?.expectedTests;
  }

  onBegin(_config: unknown, suite: Suite): void {
    this.discovered = suite.allTests().length;
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    if (result.status === 'skipped') this.skipped.push(test.titlePath().join(' > '));
  }

  onEnd(result: FullResult): { status: FullResult['status'] } | void {
    const expected = this.expectedTests ?? (process.env.E2E_FULL_ACCEPTANCE === 'true' ? EXPECTED_TESTS : undefined);
    if ((expected === undefined || this.discovered === expected) && this.skipped.length === 0) return;
    const reasons = [
      expected !== undefined && this.discovered !== expected
        ? `expected ${expected} discovered tests, found ${this.discovered}`
        : null,
      this.skipped.length > 0 ? `skipped tests: ${this.skipped.join('; ')}` : null,
    ].filter(Boolean).join(' | ');
    console.error(`V1 acceptance gate failed: ${reasons}`);
    return { status: 'failed' };
  }
}
