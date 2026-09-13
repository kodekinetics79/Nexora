import { lazy, type ComponentType, type LazyExoticComponent } from 'react';
import { isStaleDeploymentChunkError } from './chunkRecovery';
import { markDeploymentUpdated } from './deploymentUpdate';

/**
 * A screen's code file could not be loaded even after one quiet retry.
 *
 * Distinct from any other render failure so the in-shell boundary knows the person was opening a
 * screen (a navigation they just made) rather than working inside one.
 */
export class RouteChunkLoadError extends Error {
  readonly cause: unknown;

  constructor(cause: unknown) {
    super(cause instanceof Error ? cause.message : String(cause ?? 'Screen code could not be loaded'));
    this.name = 'RouteChunkLoadError';
    this.cause = cause;
  }
}

export const isRouteChunkLoadError = (error: unknown): error is RouteChunkLoadError =>
  error instanceof RouteChunkLoadError;

const wait = (ms: number) => new Promise<void>((resolve) => {
  setTimeout(resolve, ms);
});

/**
 * `React.lazy` for route screens, with one retry in place before anyone sees a failure.
 *
 * A network blip while fetching a screen's code used to go straight to the crash card. The retry
 * costs nothing when the first attempt works. When the second attempt also fails the file belongs
 * to an older deployment, so the update flag is raised and a `RouteChunkLoadError` is thrown for
 * the in-shell boundary to handle.
 */
export function lazyWithRetry<T extends ComponentType<any>>(
  factory: () => Promise<{ default: T }>,
  retryDelayMs = 400,
): LazyExoticComponent<T> {
  return lazy(async () => {
    try {
      return await factory();
    } catch (error) {
      if (!isStaleDeploymentChunkError(error)) throw error;
      await wait(retryDelayMs);
      try {
        return await factory();
      } catch (retryError) {
        markDeploymentUpdated();
        throw new RouteChunkLoadError(retryError);
      }
    }
  });
}

export default lazyWithRetry;
