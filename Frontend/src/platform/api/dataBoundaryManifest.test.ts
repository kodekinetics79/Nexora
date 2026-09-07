import { afterEach, describe, expect, it, vi } from 'vitest';
import platformHttp from './platformHttp';
import { platformApi } from './client';

/**
 * The console and the API deploy separately, and the console is the faster of the two.
 *
 * <p>Vercel publishes a merge in about a minute; Render rebuilds a .NET service in several, and
 * only when something triggers it. In that window a NEW console is talking to the PREVIOUS API.
 * When `observation` was added, the panel read `manifest.observation.isUsable` straight off the
 * payload, the deployed server did not send it, and the entire Activation tab fell to the error
 * boundary: an operator was shown "Something went wrong" on the one screen whose job is to tell
 * them what is wrong, on a tenant that was merely waiting for a backend to restart.</p>
 *
 * <p>So a field a deployed server might not send is an optional field, whatever the TypeScript
 * says. These pin the completed shape at the boundary, once, rather than trusting every reader to
 * guard it.</p>
 */
describe('the data-boundary manifest survives a server that predates its fields', () => {
  afterEach(() => vi.restoreAllMocks());

  const respond = (data: unknown) => {
    vi.spyOn(platformHttp, 'get').mockResolvedValue({ data } as never);
  };

  it('completes an old payload instead of handing the console an undefined', async () => {
    // Exactly what the API served before this feature: no observation, no source, no provenance.
    respond({
      configured: false,
      primaryPostgreSqlScope: null,
      boundaries: [],
      defects: [],
      configurationKey: 'Platform:DataBoundaries',
    });

    const manifest = await platformApi.getPlatformDataBoundaries();

    expect(manifest.observation).toBeDefined();
    expect(manifest.observation.isUsable).toBe(false);
    // And it says why, in words an operator can act on — the fix is to wait, not to start typing.
    expect(manifest.observation.basis).toMatch(/predates the question/i);
    expect(manifest.source).toBe('none');
    expect(manifest.recordedBy).toBeNull();
  });

  /**
   * The same old server, but one that HAS the environment variables set. It reports `configured`
   * and no `source`, and the console must not read that as "nobody has declared anything" — the
   * one-button register path depends on knowing a boundary exists.
   */
  it('reads a configured old payload as configuration rather than as nothing', async () => {
    respond({
      configured: true,
      primaryPostgreSqlScope: {
        assetType: 'PostgreSqlTenantScope', logicalKey: 'postgresql.primary',
        opaqueProviderReference: 'neon-prod', region: 'us-east-1',
        classification: 'CustomerData', disposition: 'BackupRetainedUntilExpiryThenDestroy',
        backupPolicyReference: 'pitr-7d', backupPolicyVersion: 1,
      },
      boundaries: [],
      defects: [],
      configurationKey: 'Platform:DataBoundaries',
    });

    const manifest = await platformApi.getPlatformDataBoundaries();

    expect(manifest.source).toBe('configuration');
    expect(manifest.primaryPostgreSqlScope?.opaqueProviderReference).toBe('neon-prod');
  });

  it('leaves a current payload exactly as the server sent it', async () => {
    respond({
      configured: true,
      source: 'console',
      primaryPostgreSqlScope: null,
      boundaries: [],
      defects: [],
      observation: {
        host: 'ep-a.c-2.us-east-1.aws.neon.tech', providerName: 'Neon',
        opaqueProviderReference: 'neon-ep-a', region: 'us-east-1',
        basis: 'Read from the database host this process is connected to.', isUsable: true,
      },
      recordedBy: 'owner@nexora.app',
      recordedOn: '2026-09-06T10:00:00Z',
      recordedBasis: 'observed-and-confirmed',
      configurationKey: 'Platform:DataBoundaries',
    });

    const manifest = await platformApi.getPlatformDataBoundaries();

    expect(manifest.source).toBe('console');
    expect(manifest.observation.isUsable).toBe(true);
    expect(manifest.recordedBy).toBe('owner@nexora.app');
  });
});
