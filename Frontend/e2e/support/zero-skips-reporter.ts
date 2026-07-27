import type { FullResult, Reporter, Suite, TestCase, TestResult } from '@playwright/test/reporter';

const EXPECTED_TESTS = 38;

export default class ZeroSkipsReporter implements Reporter {
  private discovered = 0;
  private skipped: string[] = [];

  onBegin(_config: unknown, suite: Suite): void {
    this.discovered = suite.allTests().length;
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    if (result.status === 'skipped') this.skipped.push(test.titlePath().join(' > '));
  }

  onEnd(result: FullResult): { status: FullResult['status'] } | void {
    const enforceFullSuite = process.env.E2E_FULL_ACCEPTANCE === 'true';
    if ((!enforceFullSuite || this.discovered === EXPECTED_TESTS) && this.skipped.length === 0) return;
    const reasons = [
      enforceFullSuite && this.discovered !== EXPECTED_TESTS
        ? `expected ${EXPECTED_TESTS} discovered tests, found ${this.discovered}`
        : null,
      this.skipped.length > 0 ? `skipped tests: ${this.skipped.join('; ')}` : null,
    ].filter(Boolean).join(' | ');
    console.error(`V1 acceptance gate failed: ${reasons}`);
    return { status: 'failed' };
  }
}
