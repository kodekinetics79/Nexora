import { beforeEach, describe, expect, it, vi } from 'vitest';

type Listener = (event: MessageEvent<unknown>) => void;

class FakeBroadcastChannel {
  static instances: FakeBroadcastChannel[] = [];
  name: string;
  messages: unknown[] = [];
  listener?: Listener;
  constructor(name: string) { this.name = name; FakeBroadcastChannel.instances.push(this); }
  addEventListener(_type: string, listener: Listener) { this.listener = listener; }
  postMessage(message: unknown) { this.messages.push(message); }
  dispatch(message: unknown) { this.listener?.({ data: message } as MessageEvent<unknown>); }
  close() {}
}

describe('platform cross-tab session bridge', () => {
  beforeEach(() => {
    vi.resetModules();
    sessionStorage.clear();
    FakeBroadcastChannel.instances = [];
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
  });

  it('requests a session with a nonce and accepts only its targeted response', async () => {
    const session = await import('./platformSession');
    const bridge = FakeBroadcastChannel.instances[0];
    const request = bridge.messages[0] as { source: string; nonce: string };
    const user = { id: '7', email: 'owner@example.test', role: 'Owner' };

    bridge.dispatch({ type: 'session-response', source: 'other', target: request.source,
      nonce: 'wrong', token: 'wrong-token', user });
    expect(session.getPlatformToken()).toBeNull();

    bridge.dispatch({ type: 'session-response', source: 'other', target: request.source,
      nonce: request.nonce, token: 'shared-token', user });
    expect(session.getPlatformToken()).toBe('shared-token');
    expect(session.getPlatformUser()).toEqual(user);
  });

  it('responds to another tab from sessionStorage without moving the token to localStorage', async () => {
    sessionStorage.setItem('nexora_platform_token', 'tab-token');
    sessionStorage.setItem('nexora_platform_user', JSON.stringify({ email: 'owner@example.test' }));
    await import('./platformSession');
    const bridge = FakeBroadcastChannel.instances[0];

    bridge.dispatch({ type: 'session-request', source: 'new-tab', nonce: 'one-time' });

    expect(bridge.messages).toContainEqual(expect.objectContaining({
      type: 'session-response', target: 'new-tab', nonce: 'one-time', token: 'tab-token',
    }));
    expect(localStorage.getItem('nexora_platform_token')).toBeNull();
  });

  it('propagates logout without rebroadcast loops', async () => {
    const session = await import('./platformSession');
    const bridge = FakeBroadcastChannel.instances[0];
    session.setPlatformSession('live-token', { email: 'owner@example.test' });
    const before = bridge.messages.length;

    bridge.dispatch({ type: 'session-cleared', source: 'other-tab' });

    expect(session.getPlatformToken()).toBeNull();
    expect(bridge.messages).toHaveLength(before);
  });
});
