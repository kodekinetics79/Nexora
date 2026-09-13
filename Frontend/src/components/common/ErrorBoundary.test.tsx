import { render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ErrorBoundary from './ErrorBoundary';
import { pageReloader, resetDeploymentUpdateForTests } from '../../utils/deploymentUpdate';

const Crash = () => {
  throw new Error('render failure');
};

describe('ErrorBoundary (application level)', () => {
  let reload: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(() => {
    reload = vi.fn<() => void>();
    pageReloader.reload = reload;
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    resetDeploymentUpdateForTests();
    vi.restoreAllMocks();
  });

  it('clears the crash card when the address changes, so Back recovers without a reload', () => {
    const { rerender } = render(<ErrorBoundary resetKey="/platform/tenants"><Crash /></ErrorBoundary>);
    expect(screen.getByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();

    rerender(<ErrorBoundary resetKey="/login"><p>Sign in</p></ErrorBoundary>);

    expect(screen.getByText('Sign in')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Something went wrong' })).not.toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
  });

  it('does not reload by itself for an ordinary render failure', () => {
    render(<ErrorBoundary resetKey="/login"><Crash /></ErrorBoundary>);
    expect(screen.getByRole('button', { name: 'Reload Page' })).toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
  });
});
