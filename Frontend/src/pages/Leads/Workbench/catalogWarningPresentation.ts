export interface CatalogWarningSnapshot {
  needsAttention: boolean;
  attentionReason?: string;
  matches: Array<{ productId: number; productName?: string; materialCode?: string }>;
}

interface ParsedCatalogWarningSnapshot {
  snapshot: CatalogWarningSnapshot;
  state: 'absent' | 'valid' | 'invalid';
}

const parseSnapshot = (value?: string | null): ParsedCatalogWarningSnapshot => {
  if (!value) {
    return { state: 'absent', snapshot: { needsAttention: false, matches: [] } };
  }

  try {
    const parsed = JSON.parse(value) as Record<string, unknown>;
    const rawMatches = (parsed.Matches ?? parsed.matches) as Array<Record<string, unknown>> | undefined;
    return {
      state: 'valid',
      snapshot: {
        needsAttention: Boolean(parsed.NeedsAttention ?? parsed.needsAttention),
        attentionReason: String(parsed.AttentionReason ?? parsed.attentionReason ?? '').trim() || undefined,
        matches: (Array.isArray(rawMatches) ? rawMatches : []).map((match) => ({
          productId: Number(match.ProductId ?? match.productId),
          productName: String(match.ProductName ?? match.productName ?? '').trim() || undefined,
          materialCode: String(match.MaterialCode ?? match.materialCode ?? '').trim() || undefined,
        })).filter((match) => Number.isFinite(match.productId)),
      },
    };
  } catch {
    return { state: 'invalid', snapshot: { needsAttention: false, matches: [] } };
  }
};

export const parseCatalogWarningSnapshot = (value?: string | null): CatalogWarningSnapshot =>
  parseSnapshot(value).snapshot;

export const catalogWarningSummary = (
  value?: string | null,
  fallbackAttentionReason?: string | null,
): string => {
  const parsed = parseSnapshot(value);
  const fallback = fallbackAttentionReason?.trim();

  if (parsed.snapshot.needsAttention || fallback) {
    return parsed.snapshot.attentionReason || fallback || 'Catalog review requires acknowledgement.';
  }
  if (parsed.state === 'invalid') {
    return 'Saved catalog review details are unavailable. Verify the source evidence before committing.';
  }
  if (parsed.snapshot.matches.length > 0) {
    return `${parsed.snapshot.matches.length} catalog candidate${parsed.snapshot.matches.length === 1 ? '' : 's'} reviewed; no active warning.`;
  }
  return parsed.state === 'absent'
    ? 'No catalog warning was recorded.'
    : 'No active catalog warning.';
};

export const catalogPolicyLabel = (policyVersion?: string | null): string =>
  `Catalog policy: ${policyVersion?.trim() || 'not recorded'}`;
