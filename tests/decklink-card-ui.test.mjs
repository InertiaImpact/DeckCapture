import assert from 'node:assert/strict';
import test from 'node:test';
import { getInputCards, draftOperatorBindingsAllowed, connectorForName, connectorDeviceNames } from '../src/Supervisor/wwwroot/config/input-map.js';
import { createConfigView } from '../src/Supervisor/wwwroot/config/view.js';

const ports = [
  { id: 'one', label: 'Input 1', kind: 'SDI', direction: 'Input' },
  { id: 'two', label: 'Input 2', kind: 'SDI', direction: 'Input' },
  { id: 'out', label: 'Output', kind: 'SDI', direction: 'Output' },
  { id: 'ref', label: 'Reference', kind: 'Reference', direction: 'Input' }
];
const catalog = [{ id: 'known', modelName: 'Known', connectors: ports, vendorVerified: true }, { id: 'custom', modelName: 'Custom', connectors: [], vendorVerified: false }];
const wire = { id: 'draft', modelId: 'known', label: 'Card', inputs: [{ deviceName: 'grouped', connectorId: 'one' }, { deviceName: 'ungrouped', connectorId: 'two' }], customConnectors: [] };
const customPorts = [{ id: 'hdmi', label: 'HDMI input', kind: 'HDMI', direction: 'Input' }];
const customWire = { id: 'custom-card', modelId: 'custom', label: 'Custom card', inputs: [{ deviceName: 'custom-endpoint', connectorId: 'hdmi' }], customConnectors: customPorts };
function fixture(wiring, devices, cards = [], savedWiring = []) {
  const options = { wiring, devices, cards, savedWiring, catalog, channels: [] };
  return { options, cards: () => getInputCards(options), selected: name => connectorForName({ ...options, name }), names: (cardId, connectorId) => connectorDeviceNames({ ...options, cardId, connectorId }) };
}
function operatorCard(inputs, connectorLayout = ports, id = 'operator:draft', modelId = 'known') { return { id, modelId, mappingSource: 'Operator', connectorLayout, inputs }; }

test('actual card pipeline blocks a raw mixed grouped/ungrouped draft as a unit', () => {
  const f = fixture([wire], [{ name: 'grouped', inventory: { deviceGroupId: 'G' } }, { name: 'ungrouped' }], [{ id: 'G', mappingSource: 'Sdk', connectorLayout: [], inputs: [] }]);
  assert.equal(draftOperatorBindingsAllowed({ ...f.options, wire }).size, 0);
  assert.equal(f.selected('ungrouped'), null); assert.deepEqual(f.names('operator:draft', 'two'), []);
  assert.ok(f.cards().every(card => !card.projection.length));
});
test('raw SDK connector evidence cannot be replaced when its group is unavailable', () => {
  const f = fixture([{ ...wire, inputs: [{ deviceName: 'raw', connectorId: 'one' }] }], [{ name: 'raw', inventory: { deviceGroupId: null, physicalConnectorId: 'sdi-3' } }]);
  assert.equal(f.selected('raw'), null); assert.deepEqual(f.names('operator:draft', 'one'), []);
});
test('backend applied false remains unapplied through card building and selection', () => {
  const entry = { ...wire, inputs: [{ deviceName: 'blocked', connectorId: 'one' }] };
  const f = fixture([entry], [{ name: 'blocked' }], [operatorCard([{ deviceName: 'blocked', connectorId: 'one', present: true, applied: false }])], [entry]);
  assert.equal(f.selected('blocked'), null); assert.deepEqual(f.names('operator:draft', 'one'), []);
  assert.equal(f.cards()[0].backendInputs[0].applied, false);
});
test('backend present false is authoritative even if the named endpoint is discovered', () => {
  const entry = { ...wire, inputs: [{ deviceName: 'stale', connectorId: 'one' }] };
  const f = fixture([entry], [{ name: 'stale' }], [operatorCard([{ deviceName: 'stale', connectorId: 'one', present: false, applied: false }])], [entry]);
  assert.equal(f.selected('stale'), null); assert.deepEqual(f.names('operator:draft', 'one'), []);
  assert.equal(f.cards()[0].backendInputs[0].present, false);
});
test('an operator connection cannot bypass a backend unapplied binding', () => {
  const entry = { ...wire, inputs: [{ deviceName: 'blocked', connectorId: 'one' }] };
  const f = fixture([entry], [{ name: 'blocked', connection: { cardId: 'operator:draft', connectorIds: ['one'], mappingSource: 'Operator' } }], [operatorCard([{ deviceName: 'blocked', connectorId: 'one', present: true, applied: false }])], [entry]);
  assert.equal(f.selected('blocked'), null);
  assert.deepEqual(f.names('operator:draft', 'one'), []);
});
test('an absent draft endpoint is retained as configuration and never projected live', () => {
  const f = fixture([{ ...wire, inputs: [{ deviceName: 'gone', connectorId: 'one' }] }], []);
  assert.equal(f.selected('gone'), null); assert.deepEqual(f.names('operator:draft', 'one'), []);
  assert.equal(f.cards()[0].operatorWiring.inputs[0].deviceName, 'gone'); assert.deepEqual(f.cards()[0].projection, []);
});
test('a custom backend SDK overlay preserves its effective SDK card ID and explicit layout', () => {
  const f = fixture([customWire], [{ name: 'custom-endpoint', inventory: { deviceGroupId: 'SDK-G' }, connection: { cardId: 'SDK-G', connectorIds: ['hdmi'], mappingSource: 'Operator' } }], [operatorCard([{ deviceName: 'custom-endpoint', connectorId: 'hdmi', present: true, applied: true }], customPorts, 'SDK-G', 'custom')], [customWire]);
  assert.equal(f.selected('custom-endpoint').cardId, 'SDK-G'); assert.equal(f.selected('custom-endpoint').connectorId, 'hdmi');
  assert.deepEqual(f.names('SDK-G', 'hdmi'), ['custom-endpoint']);
  assert.deepEqual(f.cards().map(card => card.id), ['SDK-G']); assert.deepEqual(f.cards()[0].connectorLayout, customPorts);
});
test('a standalone raw custom draft uses its declared connectors despite an empty catalog template', () => {
  const f = fixture([customWire], [{ name: 'custom-endpoint' }]);
  assert.equal(f.selected('custom-endpoint').connectorId, 'hdmi'); assert.deepEqual(f.names('operator:custom-card', 'hdmi'), ['custom-endpoint']);
  assert.deepEqual(f.cards()[0].connectorLayout, customPorts);
});
test('a custom draft augments one unknown SDK group without inventing another card identity', () => {
  const f = fixture([customWire], [{ name: 'custom-endpoint', inventory: { deviceGroupId: 'SDK-G' } }], [{ id: 'SDK-G', modelName: 'Unknown layout', mappingSource: 'Sdk', connectorLayout: [], inputs: [] }]);
  assert.deepEqual(f.cards().map(card => card.id), ['SDK-G']);
  assert.deepEqual(f.cards()[0].connectorLayout, customPorts);
  assert.equal(f.selected('custom-endpoint').cardId, 'SDK-G');
  assert.deepEqual(f.names('SDK-G', 'hdmi'), ['custom-endpoint']);
});
test('SDK connection authority survives contradictory operator draft wiring', () => {
  const f = fixture([{ ...wire, inputs: [{ deviceName: 'sdk', connectorId: 'two' }] }], [{ name: 'sdk', inventory: { deviceGroupId: 'S' }, connection: { cardId: 'S', connectorIds: ['one'], mappingSource: 'Sdk' } }], [{ id: 'S', modelId: 'known', mappingSource: 'Sdk', connectorLayout: ports, inputs: [{ deviceName: 'sdk', connectorId: 'one', present: true, applied: true }] }]);
  assert.equal(f.selected('sdk').cardId, 'S'); assert.equal(f.selected('sdk').connectorId, 'one');
  assert.deepEqual(f.names('operator:draft', 'two'), []);
});

test('the actual inventory renderer leaves output and reference connectors inert', () => {
  const state = { config: { channels: [], inputWiring: [{ ...wire, inputs: [] }] }, savedConfig: { inputWiring: [] }, inputCatalog: catalog,
    discoveredInputs: { devices: [], cards: [] }, selectedEndpointName: null, selectedConnector: null };
  const html = createConfigView({ state, document: {} }).renderInputInventory();
  assert.doesNotMatch(html, /<button[^>]*data-connector-id="(?:out|ref)"/);
  assert.match(html, /Reference only/); assert.match(html, /Output · not selectable/);
});
