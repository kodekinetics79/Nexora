/**
 * PART C step ledger — the certification verdict, printed as a matrix.
 *
 * TEST-ONLY.
 *
 * WHY THIS EXISTS RATHER THAN THE SHARED `zero-skips-reporter`.
 *
 * `e2e/support/zero-skips-reporter.ts` fails any run containing a skipped test. That is the
 * right rule for the V1 acceptance suite and the wrong one here: PART C deliberately skips the
 * steps whose backing feature has not landed, because the alternative — asserting nothing and
 * reporting green — is how a certification comes to mean nothing. Wiring PART C to that
 * reporter would make "feature A has not shipped yet" indistinguishable from "feature A is
 * broken", and both indistinguishable from a harness bug.
 *
 * So this reporter does the opposite job. It never changes the run's status. It prints, for
 * all eighteen steps, which EXECUTED, which were BLOCKED and on exactly what, which were
 * PARTIAL (asserted, but with a named sub-check that had no surface to measure), and which
 * FAILED. A blocked step is loud and legible; it is never a pass.
 */

import type { FullResult, Reporter, TestCase, TestResult } from '@playwright/test/reporter';

type Verdict = 'EXECUTED' | 'BLOCKED' | 'PARTIAL' | 'FAILED' | 'TIMED OUT' | 'INTERRUPTED' | 'NOT RUN';

interface Row {
  title: string;
  verdict: Verdict;
  detail: string;
}

const RESET = '[0m';
const TONE: Record<Verdict, string> = {
  EXECUTED: '[32m',
  PARTIAL: '[33m',
  BLOCKED: '[33m',
  'NOT RUN': '[90m',
  FAILED: '[31m',
  'TIMED OUT': '[31m',
  INTERRUPTED: '[31m',
};

export default class PartCStepLedger implements Reporter {
  private rows: Row[] = [];

  /**
   * Sections are `describe.serial`, so Playwright skips the remaining steps once one fails. That
   * is a DIFFERENT thing from a step blocked on an absent feature, and conflating the two turns
   * one real defect into a screenful of phantom "missing features". Tracked per section.
   */
  private failedSections = new Set<string>();

  onTestEnd(test: TestCase, result: TestResult): void {
    const annotation = (type: string): string | undefined =>
      test.annotations.concat(result.annotations ?? []).find((entry) => entry.type === type)?.description;
    const section = test.parent.title;

    let verdict: Verdict;
    let detail = '';

    if (result.status === 'skipped') {
      const reason = annotation('partc-blocked') ?? annotation('skip');
      if (reason) {
        verdict = 'BLOCKED';
        detail = reason;
      } else if (this.failedSections.has(section)) {
        verdict = 'NOT RUN';
        detail = `an earlier step in "${section}" failed, so this one never executed — no verdict either way`;
      } else {
        verdict = 'BLOCKED';
        detail = 'skipped without a recorded reason — treat as blocked, never as a pass';
      }
    } else if (result.status === 'passed') {
      const missing = annotation('partc-partial');
      verdict = missing ? 'PARTIAL' : 'EXECUTED';
      detail = missing ?? '';
    } else if (result.status === 'timedOut') {
      verdict = 'TIMED OUT';
      detail = result.error?.message?.split('\n')[0] ?? '';
    } else if (result.status === 'interrupted') {
      verdict = 'INTERRUPTED';
      detail = 'the run was cut short before this step reached a verdict';
    } else {
      verdict = 'FAILED';
      detail = result.error?.message?.split('\n')[0] ?? '';
    }

    if (verdict === 'FAILED' || verdict === 'TIMED OUT') this.failedSections.add(section);
    this.rows.push({ title: test.title, verdict, detail });
  }

  onEnd(_result: FullResult): void {
    const tally = this.rows.reduce<Record<string, number>>((accumulator, row) => {
      accumulator[row.verdict] = (accumulator[row.verdict] ?? 0) + 1;
      return accumulator;
    }, {});

    const lines: string[] = [
      '',
      '════════════════════════════════════════════════════════════════════════',
      ' PART C step ledger',
      '════════════════════════════════════════════════════════════════════════',
    ];

    for (const row of this.rows.sort((a, b) => a.title.localeCompare(b.title))) {
      lines.push(`${TONE[row.verdict]}${row.verdict.padEnd(11)}${RESET} ${row.title}`);
      if (row.detail) {
        for (const chunk of wrap(row.detail, 84)) lines.push(`            ${chunk}`);
      }
    }

    lines.push(
      '────────────────────────────────────────────────────────────────────────',
      ` ${this.rows.length} steps · ${Object.entries(tally).map(([k, v]) => `${v} ${k.toLowerCase()}`).join(' · ')}`,
      '',
      ' A BLOCKED step asserted nothing. It certifies nothing. Read its reason: it names the',
      ' route or control the harness needs before that step can produce a verdict at all.',
      '════════════════════════════════════════════════════════════════════════',
      '',
    );

    console.log(lines.join('\n'));
  }
}

function wrap(text: string, width: number): string[] {
  const out: string[] = [];
  let line = '';
  for (const word of text.split(/\s+/)) {
    if (line.length + word.length + 1 > width) {
      out.push(line);
      line = word;
    } else {
      line = line ? `${line} ${word}` : word;
    }
  }
  if (line) out.push(line);
  return out;
}
