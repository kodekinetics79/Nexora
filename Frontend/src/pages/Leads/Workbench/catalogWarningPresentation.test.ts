import { describe, expect, it } from 'vitest';
import {
  catalogPolicyLabel,
  catalogWarningSummary,
  parseCatalogWarningSnapshot,
} from './catalogWarningPresentation';

describe('catalog warning presentation', () => {
  it('turns a governed warning snapshot into concise client-facing copy', () => {
    const rawSnapshot = JSON.stringify({
      NeedsAttention: true,
      AttentionReason: 'No catalog match found',
      Confidence: 0.42,
      Matches: [{ ProductId: 17, ProductName: 'Approved valve', MaterialCode: 'VAL-17' }],
    });

    expect(catalogWarningSummary(rawSnapshot)).toBe('No catalog match found');
    expect(catalogWarningSummary(rawSnapshot)).not.toContain('{');
    expect(catalogPolicyLabel('lead-conversion-preview/v1')).toBe('Catalog policy: lead-conversion-preview/v1');
    expect(parseCatalogWarningSnapshot(rawSnapshot)).toMatchObject({
      needsAttention: true,
      attentionReason: 'No catalog match found',
      matches: [{ productId: 17, productName: 'Approved valve', materialCode: 'VAL-17' }],
    });
  });

  it('summarizes warning-free snapshots without exposing serialized fields', () => {
    const rawSnapshot = JSON.stringify({
      needsAttention: false,
      matches: [{ productId: 10 }, { productId: 11 }],
    });

    expect(catalogWarningSummary(rawSnapshot)).toBe('2 catalog candidates reviewed; no active warning.');
    expect(catalogWarningSummary('{}')).toBe('No active catalog warning.');
    expect(catalogWarningSummary()).toBe('No catalog warning was recorded.');
  });

  it('fails safely when a legacy snapshot is malformed', () => {
    const summary = catalogWarningSummary('{not-json');

    expect(summary).toBe('Saved catalog review details are unavailable. Verify the source evidence before committing.');
    expect(summary).not.toContain('{not-json');
    expect(catalogPolicyLabel()).toBe('Catalog policy: not recorded');
  });

  it('uses an explicit line warning when a snapshot is absent', () => {
    expect(catalogWarningSummary(undefined, 'Quantity needs confirmation')).toBe('Quantity needs confirmation');
  });
});
