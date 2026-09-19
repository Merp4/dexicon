import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { subscribeToProgress } from './api';

/**
 * Progress goes quiet and never comes back.
 *
 * The stream was read once. A normal end broke the loop without telling anyone, and an
 * error reported itself and then stopped, so progress stopped for the life of the page
 * while the header still claimed a connection. A reload was the only cure, and since every
 * server restart does it, it looked like an intermittent fault rather than a missing
 * feature.
 */
describe('subscribeToProgress', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

  /** A response whose body yields the given frames and then ends. */
  function streamOf(...frames: string[]): Response {
    const encoder = new TextEncoder();
    let i = 0;
    return {
      ok: true,
      body: {
        getReader: () => ({
          read: async () =>
            i < frames.length
              ? { done: false, value: encoder.encode(frames[i++]) }
              : { done: true, value: undefined },
        }),
      },
    } as unknown as Response;
  }

  const event = (corpusId: string) =>
    `data: ${JSON.stringify({ corpusId, state: 'running', phase: 'embed' })}\n\n`;

  it('reconnects after the stream ends on its own', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(streamOf(event('a')))
      .mockResolvedValueOnce(streamOf(event('b')))
      .mockImplementation(() => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);

    const seen: string[] = [];
    const stop = subscribeToProgress((p) => seen.push(p.corpusId));

    await vi.advanceTimersByTimeAsync(0);
    expect(seen).toEqual(['a']);

    // The first stream has ended. Without a reconnect nothing further ever arrives.
    await vi.advanceTimersByTimeAsync(1_000);
    expect(seen).toEqual(['a', 'b']);
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2);

    stop();
  });

  it('reconnects after the stream fails', async () => {
    const fetchMock = vi.fn()
      .mockRejectedValueOnce(new Error('connection refused'))
      .mockResolvedValueOnce(streamOf(event('a')))
      .mockImplementation(() => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);

    const seen: string[] = [];
    const stop = subscribeToProgress((p) => seen.push(p.corpusId));

    await vi.advanceTimersByTimeAsync(2_000);
    expect(seen).toEqual(['a']);

    stop();
  });

  it('says it is disconnected and then says it is back', () => {
    // The header offers to show "reconnecting" and nothing could ever take that back, so
    // the one state it could reach was the wrong one to be stuck in.
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(streamOf())
      .mockImplementation(() => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);

    const onError = vi.fn();
    const onOpen = vi.fn();
    const stop = subscribeToProgress(() => {}, onError, onOpen);

    return vi.advanceTimersByTimeAsync(1_000).then(() => {
      expect(onOpen).toHaveBeenCalled();
      expect(onError).toHaveBeenCalled();
      stop();
    });
  });

  it('stops trying once unsubscribed', async () => {
    const fetchMock = vi.fn().mockResolvedValue(streamOf());
    vi.stubGlobal('fetch', fetchMock);

    const stop = subscribeToProgress(() => {});
    await vi.advanceTimersByTimeAsync(0);
    stop();

    const after = fetchMock.mock.calls.length;
    await vi.advanceTimersByTimeAsync(60_000);

    // A page that has navigated away must not keep a retry loop running behind it.
    expect(fetchMock.mock.calls.length).toBe(after);
  });

  it('backs off rather than hammering a server that is down', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new Error('down'));
    vi.stubGlobal('fetch', fetchMock);

    const stop = subscribeToProgress(() => {});
    await vi.advanceTimersByTimeAsync(10_000);

    // Ten seconds of 500ms retries would be twenty. Backoff keeps it in single figures.
    expect(fetchMock.mock.calls.length).toBeLessThan(10);
    expect(fetchMock.mock.calls.length).toBeGreaterThan(1);

    stop();
  });
});
