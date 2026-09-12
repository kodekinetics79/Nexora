import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import UploadProgressPanel, { documentProgress } from './UploadProgressPanel';
import type { BatchReconciliationDTO, BatchReconciliationItemDTO } from '../../api/services/leadService';

const item = (overrides: Partial<BatchReconciliationItemDTO>): BatchReconciliationItemDTO => ({
  occurrenceId: 1, leadId: null, classification: 'Pending', fileName: 'SE RFP-C001835789.doc', ingestedAtUtc: '2026-09-12T10:00:00Z',
  processingPath: 'Deterministic', externalAiUsed: false, confidence: 0, reasons: [], matchCandidates: [],
  customerResolutionStatus: 'Awaiting customer resolution', ...overrides,
});
const batch = (items: BatchReconciliationItemDTO[], filesReceived = items.length): BatchReconciliationDTO => ({
  batchId: 'b1', filesReceived, logicalInquiries: 0, newLeads: 0, exactDuplicates: 0, revisions: 0, possibleMatches: 0, rejected: 0,
  externalOccurrences: 0, awaitingSecurityScan: 0, localFirstOccurrences: 0, items,
});

describe('documentProgress — the step a document is on, in the rep\'s words', () => {
  it('walks safety check → reading → matching → customer → ready', () => {
    expect(documentProgress(item({ securityStatus: 'Pending' }))).toMatchObject({ step: 1, state: 'active' });
    expect(documentProgress(item({ securityStatus: 'Cleared', extractionStatus: 'Extracting' }))).toMatchObject({ step: 2, state: 'active', sentence: expect.stringMatching(/Reading the document/) });
    expect(documentProgress(item({ securityStatus: 'Cleared', extractionStatus: 'Succeeded', classification: 'Pending' }))).toMatchObject({ step: 3, state: 'active' });
    expect(documentProgress(item({ securityStatus: 'Cleared', extractionStatus: 'Succeeded', classification: 'New', customerResolutionStatus: 'UNRESOLVED' }))).toMatchObject({ step: 4, state: 'active' });
    expect(documentProgress(item({ securityStatus: 'Cleared', extractionStatus: 'Succeeded', classification: 'New', leadId: 7, customerResolutionStatus: 'AUTO_MATCHED_CONTACT_UNRESOLVED' })))
      .toMatchObject({ step: 5, state: 'done', sentence: expect.stringMatching(/Customer identified. Ready to decide/) });
  });

  it('says so when a file is refused, fails to read, or is held by an offline scanner', () => {
    expect(documentProgress(item({ securityStatus: 'Rejected' }))).toMatchObject({ step: 1, state: 'failed' });
    expect(documentProgress(item({ securityStatus: 'Cleared', extractionStatus: 'DeadLetter' }))).toMatchObject({ step: 2, state: 'failed' });
    expect(documentProgress(item({ recoverableSecurityHold: true }))).toMatchObject({ step: 1, state: 'held' });
  });
});

describe('UploadProgressPanel', () => {
  it('keeps the rep on the page while the document is being read', () => {
    render(<UploadProgressPanel batch={batch([item({ securityStatus: 'Cleared', extractionStatus: 'Extracting' })], 2)} onDecide={vi.fn()} onOpenInquiries={vi.fn()} />);
    expect(screen.getByText(/Reading your documents/)).toBeInTheDocument();
    expect(screen.getByText(/0 of 2 finished/)).toBeInTheDocument();
    expect(screen.getByText(/Reading the document: headers, dates, lines and part numbers/)).toBeInTheDocument();
    expect(screen.getByText(/Document 2 of 2: received, waiting to be recorded/)).toBeInTheDocument();
  });

  it('says done and offers Decide when the one inquiry is ready', () => {
    const onDecide = vi.fn();
    render(<UploadProgressPanel batch={batch([item({ securityStatus: 'Cleared', extractionStatus: 'Succeeded', classification: 'New', leadId: 42, customerResolutionStatus: 'AUTO_MATCHED_CONTACT_UNRESOLVED' })])} onDecide={onDecide} onOpenInquiries={vi.fn()} />);
    expect(screen.getByText('Done')).toBeInTheDocument();
    expect(screen.getByText(/One inquiry is ready/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Decide' }));
    expect(onDecide).toHaveBeenCalledWith(42);
  });

  it('says nothing new to decide when every document was a repeat', () => {
    render(<UploadProgressPanel batch={batch([item({ securityStatus: 'Cleared', extractionStatus: 'Duplicate', classification: 'ExactDuplicate', leadId: 9, customerResolutionStatus: 'AUTO_MATCHED' })])} onDecide={vi.fn()} onOpenInquiries={vi.fn()} />);
    expect(screen.getByText(/Nothing new to decide/)).toBeInTheDocument();
  });
});
