import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import WorkbenchStageActions from './WorkbenchStageActions';
import type { WorkbenchStage } from './WorkbenchStageNavigation';

const renderActions = (stage: WorkbenchStage, overrides: Partial<React.ComponentProps<typeof WorkbenchStageActions>> = {}) => {
  const onStageChange = vi.fn();
  const props: React.ComponentProps<typeof WorkbenchStageActions> = {
    stage,
    status: { progress: 'needs-action', detail: 'Complete the current stage.' },
    onStageChange,
    canContinueEvidence: true,
    canContinueValidation: true,
    canEdit: true,
    dirty: true,
    hasSavedFitAssessment: true,
    decisionPending: false,
    decisionRecordLocked: false,
    canCommit: true,
    participationCommitted: false,
    participationStatus: 'DRAFT',
    fullNoBid: false,
    fullNoBidClosed: false,
    onSaveDraft: vi.fn(),
    onCommit: vi.fn(),
    canPromote: true,
    promotionBlocked: false,
    promotionPending: false,
    alreadyPromoted: false,
    approvedLineCount: 2,
    onPromote: vi.fn(),
    ...overrides,
  };
  render(<WorkbenchStageActions {...props} />);
  return { props, onStageChange };
};

describe('WorkbenchStageActions', () => {
  it('shows only the evidence-stage continuation action', () => {
    const { onStageChange } = renderActions('evidence');

    fireEvent.click(screen.getByRole('button', { name: 'Review transformation' }));
    expect(onStageChange).toHaveBeenCalledWith('validate');
    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Commit participation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Promote .* to RFQ/ })).not.toBeInTheDocument();
  });

  it('does not advance past blocked source evidence', () => {
    renderActions('evidence', {
      canContinueEvidence: false,
      status: { progress: 'blocked', detail: 'Source lineage is incomplete.' },
    });

    expect(screen.getByRole('button', { name: 'Review transformation' })).toBeDisabled();
    expect(screen.getByText('Source lineage is incomplete.')).toBeInTheDocument();
  });

  it('provides safe Back and Next navigation for transformation review', () => {
    const { onStageChange } = renderActions('validate', { canContinueValidation: false });

    fireEvent.click(screen.getByRole('button', { name: 'Back to evidence' }));
    expect(onStageChange).toHaveBeenCalledWith('evidence');
    expect(screen.getByRole('button', { name: 'Continue to fit & participation' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Commit participation' })).not.toBeInTheDocument();
  });

  it('contains draft and commit controls only on an editable participation stage', () => {
    const onSaveDraft = vi.fn();
    const onCommit = vi.fn();
    renderActions('participation', { onSaveDraft, onCommit });

    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    fireEvent.click(screen.getByRole('button', { name: 'Commit participation' }));
    expect(onSaveDraft).toHaveBeenCalledOnce();
    expect(onCommit).toHaveBeenCalledOnce();
    expect(screen.queryByRole('button', { name: /Promote .* to RFQ/ })).not.toBeInTheDocument();
  });

  it('lets a sales rep save a draft without advertising authority to commit it', () => {
    const onSaveDraft = vi.fn();
    const onCommit = vi.fn();
    renderActions('participation', {
      canEdit: true,
      canCommit: false,
      draftForManagerReview: true,
      onSaveDraft,
      onCommit,
    });

    fireEvent.click(screen.getByRole('button', { name: 'Save draft for manager review' }));
    expect(onSaveDraft).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: 'Commit participation' })).toBeDisabled();
    expect(onCommit).not.toHaveBeenCalled();
  });

  it('replaces edit controls with Continue after participation is committed', () => {
    const { onStageChange } = renderActions('participation', {
      dirty: false,
      participationCommitted: true,
      participationStatus: 'COMMITTED',
    });

    fireEvent.click(screen.getByRole('button', { name: 'Continue to promotion' }));
    expect(onStageChange).toHaveBeenCalledWith('promote');
    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Commit/ })).not.toBeInTheDocument();
  });

  it('contains only the governed promotion action on the promotion stage', () => {
    const onPromote = vi.fn();
    const { onStageChange } = renderActions('promote', { onPromote });

    fireEvent.click(screen.getByRole('button', { name: 'Back to participation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Promote 2 lines to RFQ' }));
    expect(onStageChange).toHaveBeenCalledWith('participation');
    expect(onPromote).toHaveBeenCalledOnce();
    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Commit participation' })).not.toBeInTheDocument();
  });

  it('keeps server-derived promotion blockers authoritative', () => {
    renderActions('promote', { promotionBlocked: true });
    expect(screen.getByRole('button', { name: 'Promote 2 lines to RFQ' })).toBeDisabled();
  });
});
