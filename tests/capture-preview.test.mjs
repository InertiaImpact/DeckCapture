import assert from 'node:assert/strict';
import test from 'node:test';
import { createPreviewImageController } from '../src/Supervisor/wwwroot/capture/preview.js';
import { deferred, settle } from './helpers/browser.mjs';

function image(dataset) { return { dataset, isConnected: true, hidden: true, src: '', getAttribute(name) { return name === 'src' ? this.src : null; }, removeAttribute(name) { if (name === 'src') this.src = ''; } }; }
function fixture(t) {
  const detail = image({ preview: 'ch1' }), thumb = image({ channelThumb: 'ch1' }), empty = { hidden: true }, thumbStatus = { dataset: { channelThumbStatus: 'ch1' }, hidden: true }, live = { hidden: true };
  const panel = { ownerDocument: { hidden: false }, querySelectorAll(selector) { return selector === '[data-preview], [data-channel-thumb]' ? [detail, thumb] : selector === '[data-channel-thumb-status]' ? [thumbStatus] : []; }, querySelector(selector) { return selector === '[data-live-preview]' ? live : selector === '[data-preview-empty]' ? empty : null; } };
  const f = { detail, thumb, panel, empty, thumbStatus, live, now: 1000, token: '', tab: 'capture', created: [], revoked: [], decoded: [], requests: [], snapshot: { channels: [{ configuration: { id: 'ch1' }, monitoring: { id: 'session-1', source: 'Monitor', state: 'Live' }, metrics: { signalLocked: null } }] } };
  f.fetch = async () => ({ ok: true, blob: async () => ({ bytes: 'synthetic image bytes' }) });
  f.decode = async () => {};
  f.controller = createPreviewImageController({ api: { getToken: () => f.token }, fetchImpl: (...args) => { f.requests.push(args); return f.fetch(...args); },
    URLImpl: { createObjectURL: () => { const url = 'blob:unit-' + (f.created.length + 1); f.created.push(url); return url; }, revokeObjectURL: url => f.revoked.push(url) },
    ImageCtor: class { decode() { return f.decode(); } }, getSnapshot: () => f.snapshot, getTab: () => f.tab, isPanelCurrent: () => true,
    nowImpl: () => f.now, onDecoded: value => f.decoded.push(value) });
  f.refresh = () => f.controller.refresh('ch1', panel);
  f.advance = () => { f.now += 2000; };
  t.after(() => f.controller.clearAll());
  return f;
}

test('one decoded image serves the detail and thumbnail, using a header-only credential', async t => {
  const f = fixture(t); f.token = 'unit-preview-token';
  assert.equal(await f.refresh(), true);
  assert.equal(f.requests.length, 1); assert.equal(f.created.length, 1); assert.equal(f.decoded.length, 1);
  assert.equal(f.detail.src, 'blob:unit-1'); assert.equal(f.thumb.src, f.detail.src);
  assert.equal(f.requests[0][0], '/api/channels/ch1/preview');
  assert.deepEqual(f.requests[0][1].headers, { Authorization: 'Bearer unit-preview-token' });
});

test('preview failure retains the last decoded frame and marks both images stale', async t => {
  const f = fixture(t); await f.refresh(); const last = f.controller.get('ch1'); f.advance();
  f.fetch = async () => { throw Error('offline'); };
  assert.equal(await f.refresh(), false);
  assert.equal(f.detail.src, last.url); assert.equal(f.thumb.src, last.url); assert.equal(f.detail.hidden, false);
  assert.match(f.empty.textContent, /last image shown/); assert.match(f.thumbStatus.textContent, /last image remains/);
  assert.equal(f.controller.get('ch1').lastFetch, last.lastFetch); assert.deepEqual(f.revoked, []);
});

test('replacement is decoded before either mounted image changes', async t => {
  const f = fixture(t); await f.refresh(); f.advance(); const decoding = deferred(); f.decode = () => decoding.promise;
  const loading = f.refresh(); await settle();
  assert.equal(f.detail.src, 'blob:unit-1'); assert.equal(f.thumb.src, 'blob:unit-1'); assert.deepEqual(f.revoked, []);
  decoding.resolve(); await loading;
  assert.equal(f.detail.src, 'blob:unit-2'); assert.equal(f.thumb.src, 'blob:unit-2'); assert.deepEqual(f.revoked, ['blob:unit-1']);
});

test('only one request is in flight and a detached image is rejected before URL allocation', async t => {
  const f = fixture(t), response = deferred(); f.fetch = () => response.promise;
  const pending = f.refresh(); assert.equal(f.refresh(), pending); await settle(); assert.equal(f.requests.length, 1);
  f.detail.isConnected = false; f.thumb.isConnected = false;
  response.resolve({ ok: true, blob: async () => ({}) }); assert.equal(await pending, false);
  assert.deepEqual(f.created, []); assert.equal(f.detail.src, ''); assert.equal(f.decoded.length, 0);
});

test('failed decoding revokes the rejected blob while retaining the previous image', async t => {
  const f = fixture(t); await f.refresh(); f.advance(); f.decode = async () => { throw Error('invalid image'); };
  assert.equal(await f.refresh(), false); assert.equal(f.detail.src, 'blob:unit-1');
  assert.deepEqual(f.revoked, ['blob:unit-2']); assert.equal(f.decoded.length, 1);
});

test('late success or failure cannot replace the status or cache of a new token/session', async t => {
  for (const changed of ['token', 'session']) for (const outcome of ['resolve', 'reject']) {
    const f = fixture(t); await f.refresh(); f.advance(); const decoding = deferred(); f.decode = () => decoding.promise;
    const pending = f.refresh(); await settle();
    if (changed === 'token') f.token = 'new-token'; else f.snapshot.channels[0].monitoring.id = 'new-session';
    assert.equal(f.controller.get('ch1'), null);
    f.empty.textContent = 'New session is waiting'; f.thumbStatus.textContent = 'New session is waiting';
    if (outcome === 'resolve') decoding.resolve(); else decoding.reject(Error('old image failed'));
    assert.equal(await pending, false); assert.equal(f.empty.textContent, 'New session is waiting');
    assert.equal(f.thumbStatus.textContent, 'New session is waiting'); assert.equal(f.decoded.length, 1);
    assert.deepEqual(f.revoked.sort(), ['blob:unit-1', 'blob:unit-2']);
  }
});

test('input loss clears cached images without another fetch; unknown lock remains eligible', async t => {
  const f = fixture(t); await f.refresh(); assert.equal(f.detail.hidden, false);
  f.snapshot.channels[0].metrics.inputSignalPresent = false; f.advance(); assert.equal(await f.refresh(), false);
  assert.equal(f.requests.length, 1); assert.equal(f.controller.get('ch1'), null); assert.equal(f.detail.hidden, true); assert.equal(f.thumb.hidden, true);
  assert.deepEqual(f.revoked, ['blob:unit-1']);
  f.snapshot.channels[0].metrics = { signalLocked: null }; assert.equal(await f.refresh(), true);
  assert.equal(f.requests.length, 2);
});

test('removed channels evict their disposable cache and a visible live player keeps JPEG hidden', async t => {
  const f = fixture(t); f.live.hidden = false; await f.refresh();
  assert.equal(f.detail.hidden, true); assert.equal(f.thumb.hidden, false); assert.equal(f.empty.hidden, true);
  f.snapshot.channels = []; assert.equal(f.controller.get('ch1'), null); assert.deepEqual(f.revoked, ['blob:unit-1']);
});
