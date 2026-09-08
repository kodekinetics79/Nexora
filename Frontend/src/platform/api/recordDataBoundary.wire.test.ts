import { afterEach, describe, expect, it, vi } from 'vitest';
import platformHttp from './platformHttp';
import { platformApi } from './client';

/**
 * The dead end this closes, and why the panel's own test could not see it.
 *
 * <p>The server refuses to record this deployment's database while a tenant is still registered
 * against a different one, and — correctly — offers a way out only on the observed path. The
 * console renders that offer as "Move it onto this database", which re-sends the request asking
 * for the move.</p>
 *
 * <p>The flag never reached the wire. It was named in the input type, passed by the panel, and
 * then dropped when the request body was assembled, so the retry was byte for byte the first
 * attempt: the server refused it for the same reason, the same banner re-rendered, and one tenant
 * left over from a test run blocked activation for every tenant on the deployment — with a button
 * right there that did nothing. `DeploymentDatabasePanel.conflict.test.tsx` mocks
 * `platformApi.recordPlatformDataBoundary` itself, so it proves the button calls the function and
 * cannot prove the function says anything. This asserts the body.</p>
 */
describe('recording the deployment database puts the move on the wire', () => {
  afterEach(() => vi.restoreAllMocks());

  const putBody = () => {
    const put = vi.spyOn(platformHttp, 'put').mockResolvedValue({
      data: { configured: true, boundaries: [], defects: [] },
    } as never);
    return () => put.mock.calls[0][1] as Record<string, unknown>;
  };

  const confirmingTheObservedDatabase = {
    opaqueProviderReference: null,
    region: null,
    backupPolicyReference: 'pitr-7d',
    backupPolicyVersion: 1,
    reason: null,
  };

  it('asks for the move when the operator pressed the button that offers it', async () => {
    const body = putBody();

    await platformApi.recordPlatformDataBoundary({
      ...confirmingTheObservedDatabase,
      reregisterConflictingTenants: true,
    });

    expect(body()).toMatchObject({ reregisterConflictingTenants: true });
  });

  /**
   * The first attempt must never carry it. A move is something an Owner asked for after being
   * told what it would move — never a default that quietly re-points registrations on the way past.
   */
  it('never asks for the move on the first attempt', async () => {
    const body = putBody();

    await platformApi.recordPlatformDataBoundary(confirmingTheObservedDatabase);

    expect(body()).toMatchObject({ reregisterConflictingTenants: false });
  });
});
