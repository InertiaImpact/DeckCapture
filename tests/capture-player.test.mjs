import assert from 'node:assert/strict';
import test from 'node:test';
import { createLivePlayerController, livePreviewRoute } from '../src/Supervisor/wwwroot/capture/player.js';
import { Video, hlsBoundary, timers, settle, deferred, withGlobals } from './helpers/browser.mjs';

function fixture(t, options = {}) {
  const { Hls, instances } = hlsBoundary(), clock = timers(), auth = { value: options.token ?? '' }, focus = [], updates = [];
  const api = { getToken: () => auth.value, post() { throw Error('Playback must never command a worker.'); } };
  const controller = createLivePlayerController({ api, document: {}, location: { href: 'http://localhost:5278/' }, loadHls: options.loadHls ?? (async () => Hls), ...clock,
    onAudioFocus: (channelId, player) => focus.push({ channelId, sessionId: player.sessionId }) });
  t.after(() => controller.detachAll());
  const attach = (values = {}) => {
    const channelId = values.channelId ?? 'ch1', sessionId = values.sessionId ?? 'session-1', video = values.video ?? new Video();
    const callbacks = Object.fromEntries(['Status', 'Playing', 'Paused', 'Unavailable', 'Controls'].map(kind => ['set' + kind, (...args) => updates.push({ kind, channelId, sessionId, args })]));
    return controller.attach({ channelId, sessionId, video, token: auth.value, mode: 'detail', ...callbacks, ...values });
  };
  return { controller, attach, Hls, instances, clock, auth, focus, updates };
}

function xhr() { return { headers: [], setRequestHeader(name, value) { this.headers.push([name, value]); } }; }

test('strict attachment rejects a foreign or wrong-session manifest before altering the current player', async t => {
  const f = fixture(t), current = f.attach(); await settle();
  for (const url of ['https://foreign.example/index.m3u8', livePreviewRoute('ch1', 'session-1'), livePreviewRoute('ch1', 'session-2') + '?token=private']) {
    assert.throws(() => f.attach({ sessionId: 'session-2', url }), /URL does not match/);
    assert.equal(f.controller.get('ch1'), current); assert.equal(f.instances[0].destroyed, undefined); assert.equal(f.instances.length, 1);
  }
});

test('HLS authorization is applied only to the current same-origin session assets', async t => {
  const f = fixture(t, { token: 'unit-token' }); f.attach(); await settle();
  const setup = f.instances[0].config.xhrSetup, allowed = xhr();
  setup(allowed, 'http://localhost:5278' + livePreviewRoute('ch1', 'session-1').replace('index.m3u8', 'index1.ts'));
  assert.deepEqual(allowed.headers, [['Authorization', 'Bearer unit-token']]);
  for (const path of ['https://foreign.example/index1.ts', '/api/channels/ch1/live/old-session/index1.ts', 'index1.ts?token=private']) {
    const blocked = xhr(); assert.throws(() => setup(blocked, path)); assert.deepEqual(blocked.headers, []);
  }
});

test('detached and changed-token players cannot authorize a late HLS request', async t => {
  const f = fixture(t, { token: 'unit-token' }); f.attach(); await settle();
  const setup = f.instances[0].config.xhrSetup, media = 'http://localhost:5278' + livePreviewRoute('ch1', 'session-1');
  f.auth.value = 'replacement-token';
  const changed = xhr(); assert.throws(() => setup(changed, media)); assert.deepEqual(changed.headers, []);
  f.auth.value = 'unit-token'; f.controller.detach('ch1');
  const late = xhr(); assert.throws(() => setup(late, media)); assert.deepEqual(late.headers, []);
});

test('same epoch refresh retains a paused player and mounted media node', async t => {
  const f = fixture(t), player = f.attach(); await settle();
  f.instances[0].emit(f.Hls.Events.MANIFEST_PARSED); await settle();
  f.controller.pause('ch1'); const plays = player.video.playCalls, loads = player.video.loadCalls;
  const refreshed = f.attach({ video: player.video }); await settle();
  assert.equal(refreshed, player); assert.equal(refreshed.video.paused, true);
  assert.equal(player.video.playCalls, plays); assert.equal(player.video.loadCalls, loads); assert.equal(f.instances.length, 1);
});

test('session replacement destroys the old engine and ignores old manifest/error callbacks', async t => {
  const f = fixture(t), first = f.attach(); await settle(); const oldHls = f.instances[0];
  const next = f.attach({ sessionId: 'session-2' }); await settle(); const updates = f.updates.length;
  oldHls.emit(f.Hls.Events.MANIFEST_PARSED); oldHls.emit(f.Hls.Events.ERROR, { fatal: true, type: f.Hls.ErrorTypes.NETWORK_ERROR }); await settle();
  assert.equal(oldHls.destroyed, true); assert.equal(f.controller.get('ch1'), next);
  assert.equal(first.video.muted, true); assert.equal(f.updates.length, updates); assert.equal(f.clock.timeouts.size, 0);
});

test('cancelled retry callbacks cannot restart a replaced session', async t => {
  const f = fixture(t); f.attach(); await settle(); const oldHls = f.instances[0];
  f.controller.retry('ch1'); const callback = [...f.clock.timeouts.values()][0]; assert.equal(typeof callback, 'function');
  f.attach({ sessionId: 'session-2' }); await settle(); callback();
  assert.equal(oldHls.restarted, undefined); assert.equal(f.clock.timeouts.size, 0); assert.equal(f.controller.get('ch1').sessionId, 'session-2');
});

test('failed media retries are bounded and cleanup releases every local timer', async t => {
  const f = fixture(t); f.attach(); await settle();
  for (let i = 0; i < 3; i++) {
    f.instances[0].emit(f.Hls.Events.ERROR, { fatal: true, type: f.Hls.ErrorTypes.NETWORK_ERROR });
    const [id, callback] = [...f.clock.timeouts][0]; f.clock.timeouts.delete(id); callback();
  }
  f.instances[0].emit(f.Hls.Events.ERROR, { fatal: true, type: f.Hls.ErrorTypes.NETWORK_ERROR });
  assert.equal(f.instances[0].restarts, 3); assert.equal(f.clock.timeouts.size, 0);
  assert.ok(f.updates.some(update => update.kind === 'Unavailable'));
  f.controller.detachAll();
  assert.equal(f.clock.intervals.size, 0); assert.equal(f.controller.players.size, 0);
});

test('an explicit user retry restores a bounded retry budget after automatic attempts stop', async t => {
  const f = fixture(t); f.attach(); await settle();
  const drain = () => { const [id, callback] = [...f.clock.timeouts][0]; f.clock.timeouts.delete(id); callback(); };
  const fail = () => f.instances[0].emit(f.Hls.Events.ERROR, { fatal: true, type: f.Hls.ErrorTypes.NETWORK_ERROR });
  for (let i = 0; i < 3; i++) { fail(); drain(); }
  fail(); assert.equal(f.clock.timeouts.size, 0);
  f.controller.retry('ch1'); assert.equal(f.clock.timeouts.size, 1); drain();
  for (let i = 0; i < 2; i++) { fail(); drain(); }
  fail();
  assert.equal(f.clock.timeouts.size, 0); assert.equal(f.instances[0].restarts, 6);
});

test('native volume controls keep one audio focus across detail and overview and release it on mute', async t => {
  const f = fixture(t), first = f.attach(), second = f.attach({ channelId: 'ch2', sessionId: 'session-2', mode: 'overview' }); await settle();
  first.video.muted=false;first.video.dispatchEvent(new Event('volumechange'));
  assert.equal(first.video.muted, false); assert.equal(second.video.muted, true);
  second.video.muted=false;second.video.dispatchEvent(new Event('volumechange'));
  assert.equal(first.video.muted, true); assert.equal(second.video.muted, false);
  first.video.muted = false; first.video.dispatchEvent(new Event('volumechange'));
  assert.equal(second.video.muted, true); assert.equal(f.controller.audibleChannelId, 'ch1');
  first.video.muted=true;first.video.dispatchEvent(new Event('volumechange'));
  assert.equal(f.controller.audibleChannelId,null);
});

test('late success or failure from an old play cannot disturb replacement audio or controls', async t => {
  for (const outcome of ['resolve', 'reject']) {
    const pending = deferred(), f = fixture(t), first = f.attach(); await settle();
    first.video.play = () => pending.promise;
    const oldPlay = f.controller.playMuted('ch1').catch(error => error);
    const next = f.attach({ sessionId: 'session-2' }); await settle();
    next.video.muted=false;next.video.dispatchEvent(new Event('volumechange'));
    const updates = f.updates.length, focus = f.focus.length;
    if (outcome === 'reject') pending.reject(Error('Old playback failed')); else pending.resolve();
    await oldPlay;
    assert.equal(f.controller.get('ch1'), next); assert.equal(next.video.muted, false); assert.equal(f.controller.audibleChannelId, 'ch1');
    assert.equal(f.updates.length, updates); assert.equal(f.focus.length, focus);
  }
});

test('native HLS plays without a bearer token, but authenticated playback cannot use it', async t => {
  const f = fixture(t); f.Hls.isSupported = () => false;
  const native = f.attach({ video: new Video({ nativeHls: true }) }); await settle();
  assert.equal(native.native, true); assert.match(native.video.src, /session-1\/index\.m3u8$/);
  f.auth.value = 'unit-token'; const authenticated = f.attach({ video: new Video({ nativeHls: true }), sessionId: 'authenticated' }); await settle();
  assert.equal(authenticated.native, false); assert.equal(authenticated.video.src, '');
  assert.ok(f.updates.some(update => update.sessionId === 'authenticated' && update.kind === 'Unavailable'));
});

test('a pending HLS library load cannot attach media after cleanup', async t => {
  const load = deferred(), f = fixture(t, { loadHls: () => load.promise }); f.attach();
  f.controller.detachAll(); load.resolve(f.Hls); await settle();
  assert.equal(f.instances.length, 0); assert.equal(f.controller.players.size, 0); assert.equal(f.clock.intervals.size, 0);
});

test('default HLS loader requests the pinned local script rather than a remote dependency', async t => {
  const scripts = [], clock = timers(), { Hls } = hlsBoundary();
  await withGlobals({ Hls: undefined }, async () => {
    const document = { createElement: () => ({ dataset: {}, remove() {} }), head: { append: script => scripts.push(script) } };
    const controller = createLivePlayerController({ api: { getToken: () => '' }, document, location: { href: 'http://localhost:5278/' }, ...clock });
    t.after(() => controller.detachAll());
    controller.attach({ channelId: 'ch1', sessionId: 'session-1', video: new Video() });
    assert.equal(scripts.length, 1); assert.equal(scripts[0].src, '/hls-1.6.19.min.js');
    globalThis.Hls = Hls; scripts[0].onload(); await settle();
    assert.ok(controller.get('ch1').hls);
  });
});
