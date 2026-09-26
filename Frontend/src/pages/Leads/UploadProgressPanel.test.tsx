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

/** Exactly what the batch API returns for a file the hash caught at intake: no lead, no score, no customer. */
const intakeDuplicate = (duplicateOf: BatchReconciliationItemDTO['duplicateOf']) => item({
  occurrenceId: 0, classification: 'ExactDuplicate', leadId: null, confidence: 0, processingPath: 'IntakeResolved',
  customerResolutionStatus: 'Awaiting customer resolution', securityStatus: 'Cleared', intakeStatus: 'Resolved',
  fileName: 'AJP-RFQ-2026-0917 (1).pdf', duplicateOf,
});

const handlers = () => ({ onDecide: vi.fn(), onOpenInquiries: vi.fn(), onOpenLead: vi.fn(), onOpenRfq: vi.fn() });

/** A revision row exactly as the batch API returns it, with the compact diff and (optionally) the RFQ it became. */
const revision = (overrides: Partial<BatchReconciliationItemDTO> = {}) => item({
  occurrenceId: 12, classification: 'Revision', leadId: 88, revisionNumber: 2, nexoraSerial: 'NX-000088',
  customerReference: 'AJP-RFQ-2026-0917', securityStatus: 'Cleared', extractionStatus: 'Succeeded',
  customerResolutionStatus: 'AUTO_MATCHED', confidence: 0.98,
  changes: [
    { line: '2', field: 'qty', from: '20', to: '35' },
    { line: '4', field: 'qty', from: '1500', to: '2000' },
  ],
  ...overrides,
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

  it('treats an exact duplicate as finished, naming the original instead of identifying a customer', () => {
    const progress = documentProgress(intakeDuplicate({ leadId: 41, rfqNo: 'AJP-RFQ-2026-0917', customerName: 'Aramco', ownerName: 'Oze Khan' }));
    expect(progress).toMatchObject({ step: 5, state: 'done' });
    expect(progress.stages.every((s) => s === 'done')).toBe(true);
    expect(progress.sentence).toBe("This is the same file as AJP-RFQ-2026-0917 (Aramco), already on Oze Khan's desk. Nothing to do.");
    expect(progress.sentence).not.toMatch(/Identifying the customer/);
  });

  it('still finishes an exact duplicate whose original is unknown, without pretending to know it', () => {
    const progress = documentProgress(intakeDuplicate(null));
    expect(progress).toMatchObject({ step: 5, state: 'done', sentence: 'This is the same file as one you already have. Nothing to do.' });
  });
});

describe('UploadProgressPanel', () => {
  it('keeps the rep on the page while the document is being read', () => {
    render(<UploadProgressPanel batch={batch([item({ securityStatus: 'Cleared', extractionStatus: 'Extracting' })], 2)} {...handlers()} />);
    expect(screen.getByText(/Reading your documents/)).toBeInTheDocument();
    expect(screen.getByText(/0 of 2 finished/)).toBeInTheDocument();
    expect(screen.getByText(/Reading the document: headers, dates, lines and part numbers/)).toBeInTheDocument();
    expect(screen.getByText(/Document 2 of 2: received, waiting to be recorded/)).toBeInTheDocument();
  });

  it('says done and offers Decide when the one inquiry is ready', () => {
    const h = handlers();
    render(<UploadProgressPanel batch={batch([item({ securityStatus: 'Cleared', extractionStatus: 'Succeeded', classification: 'New', leadId: 42, customerResolutionStatus: 'AUTO_MATCHED_CONTACT_UNRESOLVED' })])} {...h} />);
    expect(screen.getByText('Done')).toBeInTheDocument();
    expect(screen.getByText(/One inquiry is ready/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Decide' }));
    expect(h.onDecide).toHaveBeenCalledWith(42);
  });

  it('closes an exact-duplicate upload with the original named and one button that opens it', () => {
    const h = handlers();
    render(<UploadProgressPanel batch={batch([intakeDuplicate({ leadId: 41, rfqNo: 'AJP-RFQ-2026-0917', customerName: 'Aramco', ownerName: 'Oze Khan' })], 1)} {...h} />);
    expect(screen.getByText('Nothing to do')).toBeInTheDocument();
    expect(screen.queryByText(/Reading your document/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Identifying the customer/)).not.toBeInTheDocument();
    expect(screen.queryByText(/% confidence/)).not.toBeInTheDocument();
    expect(screen.getAllByText("This is the same file as AJP-RFQ-2026-0917 (Aramco), already on Oze Khan's desk. Nothing to do.").length).toBeGreaterThan(0);
    const [only] = screen.getAllByRole('button');
    expect(only).toHaveTextContent('Open the original');
    expect(only).toHaveClass('MuiButton-contained');
    fireEvent.click(only);
    expect(h.onOpenLead).toHaveBeenCalledWith(41);
    expect(h.onDecide).not.toHaveBeenCalled();
  });

  it('says what a revision changed and, when the inquiry is already an RFQ, opens the RFQ instead of offering Decide', () => {
    const h = handlers();
    render(<UploadProgressPanel batch={batch([revision({ rfq: { rfqId: 31, rfqNo: 'RFQ-2026-000031', ownerName: 'Oze Khan' } })])} {...h} />);
    expect(screen.getByText('Already an RFQ')).toBeInTheDocument();
    expect(screen.getAllByText('AJP-RFQ-2026-0917 was revised: line 2 qty 20→35, line 4 qty 1,500→2,000. It is already RFQ RFQ-2026-000031, owned by Oze Khan.').length).toBeGreaterThan(0);
    expect(screen.queryByText(/One inquiry is ready/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Decide' })).not.toBeInTheDocument();
    const [only] = screen.getAllByRole('button');
    expect(only).toHaveTextContent('Open the RFQ');
    expect(only).toHaveClass('MuiButton-contained');
    fireEvent.click(only);
    expect(h.onOpenRfq).toHaveBeenCalledWith(31);
    expect(h.onDecide).not.toHaveBeenCalled();
  });

  it('says what a revision changed and offers Decide when the inquiry is not yet an RFQ', () => {
    const h = handlers();
    render(<UploadProgressPanel batch={batch([revision({ rfq: null })])} {...h} />);
    expect(screen.getByText('Done')).toBeInTheDocument();
    expect(screen.getByText(/AJP-RFQ-2026-0917 was revised: line 2 qty 20→35, line 4 qty 1,500→2,000\. Decide whether to quote the revised inquiry\./)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Decide' }));
    expect(h.onDecide).toHaveBeenCalledWith(88);
  });

  it('states plainly when a revision changed nothing commercial', () => {
    render(<UploadProgressPanel batch={batch([revision({ changes: [], rfq: null })])} {...handlers()} />);
    expect(screen.getAllByText(/AJP-RFQ-2026-0917 was revised, but nothing commercial changed/).length).toBeGreaterThan(0);
  });

  it('falls back to the inquiries list when a repeat has no known original', () => {
    const h = handlers();
    render(<UploadProgressPanel batch={batch([item({ securityStatus: 'Cleared', extractionStatus: 'Duplicate', classification: 'ExactDuplicate', leadId: 9, customerResolutionStatus: 'AUTO_MATCHED' })])} {...h} />);
    expect(screen.getByText('Nothing to do')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Open inquiries' }));
    expect(h.onOpenInquiries).toHaveBeenCalled();
  });
});
