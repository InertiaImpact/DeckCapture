import assert from 'node:assert/strict';
import test from 'node:test';
import * as api from '../src/Supervisor/wwwroot/api.js';

test('only input and bidirectional physical connectors can be wired', () => {
  assert.equal(api.isAssignableDeckLinkConnector({ kind: 'SDI', direction: 'Input' }), true);
  assert.equal(api.isAssignableDeckLinkConnector({ kind: 'SDI', direction: 'Bidirectional' }), true);
  assert.equal(api.isAssignableDeckLinkConnector({ kind: 'SDI', direction: 'Output' }), false);
  assert.equal(api.isAssignableDeckLinkConnector({ kind: 'Reference', direction: 'Input' }), false);
  assert.equal(api.isAssignableDeckLinkConnector({ kind: 'Reference', direction: 'Output' }), false);
  assert.equal(api.isAssignableDeckLinkConnector({ kind: 'SDI' }), false);
  assert.equal(api.isAssignableDeckLinkConnector(null), false);
});

test('manual wiring resolves by stable connector ID and retains the exact driver key', () => {
  const entries = [
    { deviceName: 'decklink:3', connectorId: 'sdi-2-in' },
    { deviceName: 'DeckLink Duo (2)', connectorId: 'sdi-1-in' }
  ];
  assert.equal(api.getMappedInputName(entries, 'sdi-1-in'), 'DeckLink Duo (2)');
  assert.equal(api.getMappedInputName(entries, 'sdi-2-in'), 'decklink:3');
  assert.equal(api.getMappedInputName(entries, 'sdi-2-out'), '');
  assert.equal(api.getMappedInputName(null, 'sdi-1-in'), '');
});

test('mixed grouped and ungrouped operator mappings stay unapplied as a unit', () => {
  const cards = [{ id: 'sdk-group-7', mappingSource: 'Operator', inputs: [
    { deviceName: 'grouped-input', connectorId: 'sdi-1-in', present: true, applied: false },
    { deviceName: 'ungrouped-input', connectorId: 'sdi-2-in', present: true, applied: false }
  ] }];
  const devices = [
    { name: 'grouped-input', inventory: { deviceGroupId: 'sdk-group-7' }, connection: { cardId: 'sdk-group-7', connectorIds: ['sdi-3'], mappingSource: 'Sdk' } },
    { name: 'ungrouped-input', inventory: null }
  ];
  const projection = api.projectDeckLinkConnections(devices, cards);
  assert.deepEqual(projection, [{ cardId: 'sdk-group-7', connectorId: 'sdi-3', deviceName: 'grouped-input', mappingSource: 'Sdk' }]);
  assert.equal(projection.some(item => item.deviceName === 'ungrouped-input'), false);
});

test('a raw physical connector without a group cannot be replaced by operator wiring', () => {
  const devices = [{ name: 'known-endpoint', inventory: { deviceGroupId: null, physicalConnectorId: 'sdi-1' }, connection: null }];
  const cards = [{ id: 'operator:card-1', mappingSource: 'Operator', inputs: [
    { deviceName: 'known-endpoint', connectorId: 'input-1', present: true, applied: false }
  ] }];
  assert.deepEqual(api.projectDeckLinkConnections(devices, cards), []);
});

test('stale operator bindings are retained by configuration but absent from the effective map', () => {
  const cards = [{ id: 'operator:card-2', mappingSource: 'Operator', inputs: [
    { deviceName: 'removed-input', connectorId: 'sdi-1-in', present: false, applied: false }
  ] }];
  assert.deepEqual(api.projectDeckLinkConnections([], cards), []);
});

test('SDK-group overlays keep the SDK card ID and expose only applied operator sockets', () => {
  const cardId = 'A1B2C3D4';
  const devices = [{ name: 'quad-input', inventory: { deviceGroupId: cardId }, connection: { cardId, connectorIds: ['sdi-1-in'], mappingSource: 'Operator' } }];
  const cards = [{ id: cardId, modelId: 'custom', mappingSource: 'Operator', inputs: [
    { deviceName: 'quad-input', connectorId: 'sdi-1-in', present: true, applied: true },
    { deviceName: 'other-input', connectorId: 'sdi-2-in', present: false, applied: false }
  ] }];
  assert.deepEqual(api.projectDeckLinkConnections(devices, cards), [
    { cardId, connectorId: 'sdi-1-in', deviceName: 'quad-input', mappingSource: 'Operator' }
  ]);
});

test('one applied profile may select several connector IDs for the same exact endpoint', () => {
  const cardId = '8k-group';
  const devices = [{ name: '8k-endpoint', connection: { cardId, connectorIds: ['sdi-1', 'sdi-2', 'sdi-3', 'sdi-4'], mappingSource: 'Sdk' } }];
  const projection = api.projectDeckLinkConnections(devices, []);
  assert.deepEqual(projection.map(item => item.connectorId), ['sdi-1', 'sdi-2', 'sdi-3', 'sdi-4']);
  assert.ok(projection.every(item => item.deviceName === '8k-endpoint' && item.cardId === cardId));
});

test('a mixed grouped and ungrouped draft is blocked as one card', () => {
  const grouped = { name: 'grouped', inventory: { deviceGroupId: 'sdk-G' } };
  const standalone = { name: 'standalone', inventory: null };
  const wire = { id: 'wire-1', inputs: [
    { deviceName: grouped.name, connectorId: 'sdi-1-in' },
    { deviceName: standalone.name, connectorId: 'sdi-2-in' }
  ] };
  assert.equal(api.projectOperatorDraftBindings(wire, [wire], [grouped, standalone], []).size, 0);
});

test('a device-reported connector blocks an operator draft even without an SDK group', () => {
  const device = { name: 'known', inventory: { deviceGroupId: null, physicalConnectorId: 'sdi-1' } };
  const binding = { deviceName: device.name, connectorId: 'sdi-1-in' };
  const wire = { id: 'wire-1', inputs: [binding] };
  assert.equal(api.projectOperatorDraftBindings(wire, [wire], [device], []).size, 0);
});

test('stale or explicitly unapplied backend status cannot become a draft highlight', () => {
  const device = { name: 'input', inventory: null };
  const binding = { deviceName: device.name, connectorId: 'sdi-1-in' };
  const wire = { id: 'wire-1', inputs: [binding] };
  const stale = [{ id: 'operator:wire-1', mappingSource: 'Operator', inputs: [{ ...binding, present: false, applied: false }] }];
  const conflict = [{ id: 'operator:wire-1', mappingSource: 'Operator', inputs: [{ ...binding, present: true, applied: false }] }];
  assert.equal(api.projectOperatorDraftBindings(wire, [wire], [device], stale).size, 0);
  assert.equal(api.projectOperatorDraftBindings(wire, [wire], [device], conflict).size, 0);
});

test('an explicitly applied operator overlay keeps and highlights its SDK card ID', () => {
  const cardId = 'sdk-G';
  const device = { name: 'input', inventory: { deviceGroupId: cardId }, connection: { cardId, connectorIds: ['sdi-1-in'], mappingSource: 'Operator' } };
  const binding = { deviceName: device.name, connectorId: 'sdi-1-in' };
  const wire = { id: 'wire-1', inputs: [binding] };
  const card = { id: cardId, mappingSource: 'Operator', connectorLayout: [{ id: 'sdi-1-in' }], inputs: [{ ...binding, present: true, applied: true }] };
  const result = api.projectOperatorDraftBindings(wire, [wire], [device], [card]);
  assert.deepEqual([...result], [binding]);
});
