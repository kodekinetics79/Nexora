// ---------------------------------------------------------------------------
// How certain was this one value?
//
// The evidence ledger has recorded a per-field confidence since it was built,
// and the API deliberately left it out of the DTO. A reviewer could see the raw
// text, the stored value, the source address and the validation outcome — good
// provenance — but not whether the value was read with certainty or salvaged.
// That is precisely the fact that decides which cell to check first.
//
// It is carried through now, and rendered as its MEANING rather than as a
// percentage. The number is real and sourced, but it is a parse verdict from a
// closed set, not a measured accuracy:
//
//   1.0  the value parsed exactly
//   0.2  the document holds text here that no rule could interpret
//   0    the document states nothing here
//
// Painting "20%" on that would re-introduce the invented precision this screen
// removed on purpose. So the sentence carries the judgement and the number is
// quoted beside it, labelled as what it is. Where no ledger exists — the model
// path writes none, and its per-field confidences were removed from the prompt
// in v2 because they were self-reported and discarded — nothing is shown.
// ---------------------------------------------------------------------------

export interface CertaintyView {
  label: string;
  color: 'success' | 'warning' | 'error' | 'default';
  detail: string;
  /** The recorded number, quoted verbatim and labelled. Null when none exists. */
  recorded: string | null;
}

export interface CertaintyInput {
  confidence?: number | null;
  valueKind?: string | null;
  rawValue?: string | null;
}

const isBlank = (value: string | null | undefined): boolean => (value ?? '').trim().length === 0;

export const describeCertainty = (entry: CertaintyInput | null | undefined): CertaintyView | null => {
  if (!entry) return null;
  const confidence = entry.confidence;
  if (confidence == null || !Number.isFinite(confidence)) return null;

  const recorded = `Recorded certainty ${confidence.toFixed(2)} — the parser's own verdict for this value, not a measured accuracy.`;
  const kind = (entry.valueKind ?? '').toLowerCase();
  const hasSourceText = !isBlank(entry.rawValue);

  if (!hasSourceText && confidence === 0) {
    return {
      label: 'Not stated in the document',
      color: 'default',
      detail: 'The document carries no value at this location, so there was nothing to read. This is the document’s own shape, not a reading failure.',
      recorded,
    };
  }

  if (confidence >= 1) {
    return kind === 'derived'
      ? {
          label: 'Derived, not read',
          color: 'default',
          detail: 'The parser produced this value itself — it is not text from the document. Check it against the surrounding lines rather than against a cell.',
          recorded,
        }
      : {
          label: 'Read exactly',
          color: 'success',
          detail: 'The source text parsed without ambiguity. What is stored follows from what the document says.',
          recorded,
        };
  }

  if (confidence > 0) {
    return {
      label: 'Could not be interpreted',
      color: 'warning',
      detail: 'The document holds text here that no parsing rule could turn into a value. Its exact wording is shown above — check this one first.',
      recorded,
    };
  }

  // Zero confidence with source text present would be a contradiction: something
  // was read, and nothing recorded how well. Say so rather than pick a side.
  return {
    label: 'Certainty not recorded',
    color: 'default',
    detail: 'This value carries source text but no recorded certainty. Treat it as unverified and check it against the document.',
    recorded,
  };
};
