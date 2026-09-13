import React, { Suspense } from 'react';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import lazyWithRetry, { RouteChunkLoadError } from './lazyWithRetry';
import { isDeploymentUpdated, resetDeploymentUpdateForTests } from './deploymentUpdate';

const staleChunk = () => new TypeError('Failed to fetch dynamically imported module: /assets/QuotesPage-1a2b.js');

class Catch extends React.Component<{ children: React.ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) { return { error }; }
  render() {
    return this.state.error
      ? <p>{`caught ${this.state.error.name}`}</p>
      : this.props.children;
  }
}

const mount = (Screen: React.ComponentType) => render(
  <Catch>
    <Suspense fallback={<p>loading</p>}>
      <Screen />
    </Suspense>
  </Catch>,
);

describe('lazyWithRetry', () => {
  afterEach(() => {
    resetDeploymentUpdateForTests();
    vi.restoreAllMocks();
  });

  it('retries a failed screen download once, in place, before anyone sees a failure', async () => {
    const factory = vi.fn<() => Promise<{ default: React.ComponentType }>>()
      .mockRejectedValueOnce(staleChunk())
      .mockResolvedValueOnce({ default: () => <h1>Quotes</h1> });
    const Screen = lazyWithRetry(factory, 0);

    mount(Screen);

    expect(await screen.findByRole('heading', { name: 'Quotes' })).toBeInTheDocument();
    expect(factory).toHaveBeenCalledTimes(2);
    expect(isDeploymentUpdated()).toBe(false);
  });

  it('raises the update and throws RouteChunkLoadError when the retry fails too', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const factory = vi.fn<() => Promise<{ default: React.ComponentType }>>()
      .mockRejectedValue(staleChunk());
    const Screen = lazyWithRetry(factory, 0);

    mount(Screen);

    expect(await screen.findByText(`caught ${new RouteChunkLoadError(staleChunk()).name}`)).toBeInTheDocument();
    expect(factory).toHaveBeenCalledTimes(2);
    expect(isDeploymentUpdated()).toBe(true);
  });

  it('does not retry a failure that is not a missing code file', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const factory = vi.fn<() => Promise<{ default: React.ComponentType }>>()
      .mockRejectedValue(new Error('module evaluation failed'));
    const Screen = lazyWithRetry(factory, 0);

    mount(Screen);

    expect(await screen.findByText('caught Error')).toBeInTheDocument();
    expect(factory).toHaveBeenCalledTimes(1);
    expect(isDeploymentUpdated()).toBe(false);
  });
});
