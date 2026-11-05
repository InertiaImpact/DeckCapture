import assert from 'node:assert/strict';
import test from 'node:test';
import { createLivePlayerController, livePreviewRoute } from '../src/Supervisor/wwwroot/capture/player.js';
import { createCapturePlayerAdapters } from '../src/Supervisor/wwwroot/capture/adapters.js';
import { createCaptureView, patchPreviewCard } from '../src/Supervisor/wwwroot/capture/view.js';
import { Video, hlsBoundary, timers, settle } from './helpers/browser.mjs';

function node(values = {}) {
  return Object.assign({ hidden: false, textContent: '', classList: { toggle() {} },
    setAttribute(name, value) { this[name] = String(value); }, getAttribute(name) { return this[name] ?? null; } }, values);
}

function previewNodes(mode) {
  const video = new Video(), image = node({ src: 'blob:last-good-jpeg' }), empty = node({ textContent: 'Waiting' }), status = node(), play = node({ hidden: true });
  video.hidden = true;
  const selectors = mode === 'overview'
    ? { '[data-overview-video]': video, '[data-overview-empty]': empty, '[data-overview-player-status]': status, '[data-action="overview-play"]': play }
    : { '[data-live-preview]': video, '[data-preview]': image, '[data-preview-empty]': empty, '[data-live-preview-status]': status, '[data-action="live-preview-play"]': play };
  const panel = { dataset: {}, querySelector: selector => selectors[selector] ?? null, querySelectorAll: () => [] };
  return { video, image, empty, status, play, panel, selectors };
}

function fixture(t, mode) {
  const { Hls, instances } = hlsBoundary(), clock = timers(), refs = previewNodes(mode), { video, image, empty, status, play, panel, selectors } = refs;
  const channel = { configuration: { id: 'ch1', label: 'Channel 1' }, monitoring: { id: 'session-1', source: 'Monitor', state: 'Live' }, metrics: { signalLocked: null, livePreviewAvailable: true } };
  const state = { snapshot: { configuration: { capture: { livePreviewEnabled: true } }, channels: [channel] }, overviewCards: new Map([['ch1', panel]]) };
  const api = { getToken: () => '', post() { throw Error('A preview adapter must never command capture.'); } };
  const controller = createLivePlayerController({ api, document: {}, location: { href: 'http://localhost:5278/' }, loadHls: async () => Hls, ...clock });
  const adapters = createCapturePlayerAdapters({ state, controller, api, location: { href: 'http://localhost:5278/' } });
  const attach = () => mode === 'overview' ? adapters.attachOverviewPlayer(channel, panel) : adapters.attachLivePreview(channel, panel);
  const start = async () => { const player = attach(); await settle(); instances.at(-1).emit(Hls.Events.MANIFEST_PARSED); await settle(); return player; };
  const refresh = () => {
    if (mode === 'detail') {
      patchPreviewCard(panel, previewNodes(mode).panel, { activePlayer: controller.get('ch1'), updateControls: player => adapters.updateControls(player.channelId) });
      return attach();
    }
    for (const selector of ['title', 'state', 'time', 'file', 'remaining', 'actions']) selectors[`[data-overview-${selector}]`] ||= node();
    const root = { dataset: { captureView: 'overview' }, querySelector: selector => selector === '[data-overview-grid]' ? { append() { throw Error('Existing cards must remain mounted'); } } : null };
    const document = { querySelector: selector => selector === '#panel-capture' ? root : null };
    const view = createCaptureView({ state, controller, adapters, document, previewImages: { get: () => null, clearAll() {} } });
    view.renderOverview();
    return controller.get('ch1');
  };
  t.after(() => controller.detachAll());
  return { video, image, empty, status, play, channel, state, controller, attach, start, refresh, instances };
}

test('detail and overview refresh retain visible playback and hidden loading/fallback overlays', async t => {
  for (const mode of ['detail', 'overview']) {
    const f = fixture(t, mode), player = await f.start(), plays = f.video.playCalls, text = f.status.textContent;
    assert.equal(f.video.hidden, false); assert.equal(f.empty.hidden, true); assert.equal(f.play.hidden, true);
    if (mode === 'detail') assert.equal(f.image.hidden, true);
    assert.equal(f.refresh(), player); await settle();
    assert.equal(f.video.hidden, false); assert.equal(f.empty.hidden, true); assert.equal(f.play.hidden, true);
    assert.equal(f.status.textContent, text); assert.equal(f.video.playCalls, plays); assert.equal(f.instances.length, 1);
    if (mode === 'detail') assert.equal(f.image.hidden, true);
    f.video.muted=false;f.video.dispatchEvent(new Event('volumechange')); assert.match(f.status.textContent, /audio on/i);
    f.video.muted=true;f.video.dispatchEvent(new Event('volumechange')); assert.match(f.status.textContent, /muted/i);
  }
});

test('paused previews retain their visible frame and paused controls through same-epoch refresh', async t => {
  for (const mode of ['detail', 'overview']) {
    const f = fixture(t, mode); await f.start(); f.controller.pause('ch1');
    const text = f.status.textContent, plays = f.video.playCalls;
    assert.match(text, /paused/i); assert.equal(f.play.hidden, false);
    f.refresh(); await settle();
    assert.equal(f.video.hidden, false); assert.equal(f.video.paused, true); assert.equal(f.empty.hidden, true);
    assert.equal(f.play.hidden, false); assert.equal(f.status.textContent, text); assert.equal(f.video.playCalls, plays);
  }
});

test('explicit input loss detaches playback and removes stale playable controls in both views', async t => {
  for (const mode of ['detail', 'overview']) for (const paused of [false, true]) {
    const f = fixture(t, mode); await f.start(); if (paused) f.controller.pause('ch1'); f.channel.metrics.inputSignalPresent = false;
    assert.equal(f.attach(), null); assert.equal(f.controller.get('ch1'), null); assert.equal(f.video.hidden, true);
    assert.equal(f.empty.hidden, false); assert.match(f.empty.textContent, /No input/);
    assert.equal(f.video.muted,true,'Input loss must mute the retired player');
    assert.ok(f.play.hidden || f.play.disabled, 'No-input state cannot retain an enabled Play button');
    assert.equal(f.instances.length, 1);
  }
});

test('unavailable detail A/V returns to the retained JPEG without stopping capture', async t => {
  const f = fixture(t, 'detail'); await f.start(); f.channel.metrics.livePreviewAvailable = false; f.channel.metrics.livePreviewError = 'Encoder unavailable';
  f.attach();
  assert.equal(f.video.hidden, true); assert.equal(f.image.hidden, false); assert.equal(f.image.src, 'blob:last-good-jpeg');
  assert.equal(f.empty.hidden, true); assert.match(f.status.textContent, /Encoder unavailable/); assert.equal(f.controller.players.size, 0);
});

for (const mode of ['detail', 'overview']) {
  test(mode + ' waits for a prepared session URL instead of propagating old-metrics epoch errors', async t => {
    const f = fixture(t, mode); await f.start(); const oldEngine = f.instances[0];
    f.channel.job = { id: 'prepared-job', state: 'Prepared', revision: 1 };
    f.channel.monitoring = { id: 'prepared-job', source: 'RecordingJob', state: 'Live' };
    const currentUrl = livePreviewRoute('ch1', 'prepared-job');
    for (const url of [livePreviewRoute('ch1', 'session-1'), 'https://foreign.example' + currentUrl, currentUrl + '?token=private']) {
      f.channel.metrics.livePreviewUrl = url;
      let result;
      assert.doesNotThrow(() => { result = f.attach(); });
      assert.equal(result, null); assert.equal(f.controller.get('ch1'), null);
      assert.equal(oldEngine.destroyed, true); assert.equal(f.instances.length, 1); assert.equal(f.video.hidden, true);
    }
    f.channel.metrics.livePreviewUrl = livePreviewRoute('ch1', 'prepared-job');
    const current = await f.start();
    assert.equal(current.sessionId, 'prepared-job'); assert.equal(f.instances.length, 2);
    assert.equal(f.video.hidden, false); assert.equal(f.empty.hidden, true);
  });
}
