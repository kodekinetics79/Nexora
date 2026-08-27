import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import {
  WorkbenchStagePanel,
  WorkbenchStageTabs,
  workbenchStageFromValue,
  workbenchStagePanelId,
  workbenchStageTabId,
} from './WorkbenchStageNavigation';

describe('WorkbenchStageNavigation', () => {
  it('opens an explicitly linked stage and defaults unknown links to evidence', () => {
    expect(workbenchStageFromValue('evidence')).toBe('evidence');
    expect(workbenchStageFromValue('promote')).toBe('promote');
    expect(workbenchStageFromValue('anything-else')).toBe('evidence');
    expect(workbenchStageFromValue(null)).toBe('evidence');
  });

  it('associates every stage tab with its panel and exposes only the active panel', () => {
    render(
      <>
        <WorkbenchStageTabs value="evidence" onChange={vi.fn()} />
        <WorkbenchStagePanel stage="evidence" activeStage="evidence">Evidence content</WorkbenchStagePanel>
        <WorkbenchStagePanel stage="promote" activeStage="evidence">Promotion content</WorkbenchStagePanel>
      </>,
    );

    const evidenceTab = screen.getByRole('tab', { name: '1. Evidence' });
    expect(evidenceTab).toHaveAttribute('id', workbenchStageTabId('evidence'));
    expect(evidenceTab).toHaveAttribute('aria-controls', workbenchStagePanelId('evidence'));

    const panel = screen.getByRole('tabpanel', { name: '1. Evidence' });
    expect(panel).toHaveAttribute('id', workbenchStagePanelId('evidence'));
    expect(panel).toHaveAttribute('aria-labelledby', workbenchStageTabId('evidence'));
    expect(panel).toHaveAttribute('tabindex', '0');
    expect(screen.queryByText('Promotion content')).not.toBeInTheDocument();
  });

  it('supports arrow-key focus and reports stage activation', () => {
    const onChange = vi.fn();
    render(<WorkbenchStageTabs value="evidence" onChange={onChange} />);

    const evidenceTab = screen.getByRole('tab', { name: '1. Evidence' });
    const reviewTab = screen.getByRole('tab', { name: '2. Review transformation' });
    evidenceTab.focus();
    fireEvent.keyDown(evidenceTab, { key: 'ArrowRight' });
    expect(reviewTab).toHaveFocus();

    fireEvent.click(reviewTab);
    expect(onChange).toHaveBeenCalledWith('validate');
  });
});
