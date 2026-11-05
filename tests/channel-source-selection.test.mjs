import assert from 'node:assert/strict';
import test from 'node:test';
import * as api from '../src/Supervisor/wwwroot/api.js';
import { applyConfigField } from '../src/Supervisor/wwwroot/config/controller.js';
import { buildPreparePayload } from '../src/Supervisor/wwwroot/capture/controls.js';
import { createConfigView } from '../src/Supervisor/wwwroot/config/view.js';

function fixture(channel) {
  const config = { channels: [channel] }, details = {};
  const pick = value => applyConfigField({ config, element: { dataset: { cfg: 'channels.0.sourcePick' }, value }, api, channelDetailsOpen: details });
  return { config, details, pick };
}

test('switching unified source preserves exact driver key, format and the saved synthetic pattern', () => {
  const f = fixture({ id: 'ch1', sourceKind: 'DeckLink', deviceName: '46:00000000:002e0c00', formatCode: 'Hi50', syntheticPattern: 'testsrc2' });
  f.pick('synthetic:black'); assert.equal(f.config.channels[0].sourceKind, 'Synthetic'); assert.equal(f.config.channels[0].syntheticPattern, 'black');
  f.pick('decklink:46:00000000:002e0c00');
  assert.equal(f.config.channels[0].sourceKind, 'DeckLink'); assert.equal(f.config.channels[0].deviceName, '46:00000000:002e0c00');
  assert.equal(f.config.channels[0].formatCode, 'Hi50'); assert.equal(f.config.channels[0].syntheticPattern, 'black');
  assert.equal(f.config.channels[0].sourcePick, undefined);
});

test('manual selection retains an unavailable endpoint and opens its explicit editing controls', () => {
  const f = fixture({ id: 'ch1', sourceKind: 'Synthetic', deviceName: 'missing:exact-key', formatCode: 'auto', syntheticPattern: 'black' });
  f.pick('manual');
  assert.equal(f.details.ch1, true); assert.equal(f.config.channels[0].sourceKind, 'DeckLink');
  assert.equal(f.config.channels[0].deviceName, 'missing:exact-key'); assert.equal(f.config.channels[0].formatCode, 'auto');
});

test('a chosen duplicate endpoint stays explicit and never remaps another channel', () => {
  const config = { channels: [{ id: 'ch1', sourceKind: 'DeckLink', deviceName: 'exact:A' }, { id: 'ch2', sourceKind: 'DeckLink', deviceName: 'exact:B' }] };
  applyConfigField({ config, element: { dataset: { cfg: 'channels.1.sourcePick' }, value: 'decklink:exact:A' }, api });
  assert.deepEqual(config.channels.map(channel => channel.deviceName), ['exact:A', 'exact:A']);
});

test('placeholder or unknown source choices cannot silently replace a configured input', () => {
  const channel = { id: 'ch1', sourceKind: 'DeckLink', deviceName: 'exact:A', formatCode: 'auto' }, f = fixture(channel), saved = structuredClone(channel);
  for (const choice of ['', 'synthetic:unrecognized']) {
    assert.equal(f.pick(choice).applied, false); assert.deepEqual(channel, saved);
  }
});

test('prepare payload excludes hidden, unknown and empty metadata without applying defaults', () => {
  const snapshot = { configuration: { metadataFields: { title: { visible: true }, shelf: { visible: false, defaultValue: 'hidden default' }, optional: { visible: true, defaultValue: 'visible default' } } } };
  const values = { fileName: ' Tape 1 ', durationMinutes: '15', destinationId: 'archive', relativeDirectory: ' tapes ', startImmediately:true, metadata: { title: ' Original title ', shelf: 'previous private value', optional: '   ', unknown: 'not configured' } };
  const before = structuredClone({ snapshot, values });
  const payload = buildPreparePayload({ channel: { configuration: { id: 'ch2' } }, snapshot, values });
  assert.deepEqual(payload.metadata, { title: 'Original title' });
  assert.equal(payload.channelId, 'ch2'); assert.equal(payload.fileName, 'Tape 1'); assert.equal(payload.durationSeconds, 900); assert.equal(payload.startImmediately,true); assert.equal(payload.waitForContent,false); assert.equal(payload.relativeDirectory, 'tapes');
  assert.equal(payload.endMode, 'Off'); assert.equal(payload.waitForContent, false);
  assert.deepEqual({ snapshot, values }, before);
});

test('a field hidden after a prior draft cannot leak through its retained value', () => {
  const snapshot = { configuration: { metadataFields: { title: { visible: true } } } }, values = { metadata: { title: 'retained typing' } };
  assert.deepEqual(buildPreparePayload({ snapshot, values:{...values,durationMinutes:'0.1'} }).metadata, { title: 'retained typing' });
  assert.equal(buildPreparePayload({snapshot,values:{durationMinutes:'0.1'}}).durationSeconds,6);
  assert.equal(buildPreparePayload({snapshot,values:{durationMinutes:'0.05'}}).durationSeconds,3);
  const waiting=buildPreparePayload({snapshot,values:{durationMinutes:'1.5',waitForContent:true}});
  assert.equal(waiting.durationSeconds,90);assert.equal(waiting.waitForContent,true);assert.equal(waiting.startImmediately,false);
  snapshot.configuration.metadataFields.title.visible = false;
  assert.deepEqual(buildPreparePayload({ snapshot, values }).metadata, {});
  assert.equal(values.metadata.title, 'retained typing');
});

test('actual source rows offer physical and synthetic choices together for either source kind', () => {
  for (const sourceKind of ['Synthetic', 'DeckLink']) {
    const channel = { id: 'ch1', sourceKind, deviceName: 'raw:input', syntheticPattern: 'black', formatCode: 'auto' };
    const state = { config: { channels: [channel] }, formatModes: {}, discoveredInputs: { devices: [{ name: 'raw:input' }] } };
    const html = createConfigView({ state, document: {} }).renderChannelRows(state.config.channels);
    const source = html.match(/<select[^>]*data-cfg="channels\.0\.sourcePick"[^>]*>([\s\S]*?)<\/select>/)?.[1];
    assert.ok(source);
    assert.deepEqual([...source.matchAll(/<option value="([^"]*)"/g)].map(match => match[1]), ['', 'decklink:raw:input', 'synthetic:testsrc2', 'synthetic:black', 'manual']);
    assert.ok(source.includes('value="' + (sourceKind === 'Synthetic' ? 'synthetic:black' : 'decklink:raw:input') + '" selected'));
    assert.doesNotMatch(html, /data-cfg="channels\.0\.(?:sourceKind|syntheticPattern)"/);
  }
});

test('rendering highlights every exact hardware assignment without remapping synthetic or duplicate rows', () => {
  const channels = [{ id: 'ch1', sourceKind: 'DeckLink', deviceName: 'exact:A' }, { id: 'ch2', sourceKind: 'DeckLink', deviceName: 'exact:A' }, { id: 'ch3', sourceKind: 'Synthetic', deviceName: 'exact:A', syntheticPattern: 'black' }];
  const state = { config: { channels }, formatModes: {}, selectedEndpointName: 'exact:A', discoveredInputs: { devices: [{ name: 'exact:A' }] } };
  const before = structuredClone(state.config);
  const html = createConfigView({ state, document: {} }).renderChannelRows(channels);
  assert.equal([...html.matchAll(/class="channel-row channel-row-mapped"/g)].length, 2);
  assert.equal([...html.matchAll(/2 channels use this input/g)].length, 2);
  assert.deepEqual(state.config, before);
});
